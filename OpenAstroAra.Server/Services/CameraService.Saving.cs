#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A downloaded frame on its way to disk and the catalog: the pixels plus everything read from the
/// rig at capture time, so the headers describe the moment of the exposure whenever the write runs.
/// <see cref="FilePath"/> is set once the file is written, so a retry after a failed registration
/// registers that file instead of writing a second one.
/// </summary>
internal sealed class FrameSaveJob {
    public required Guid FrameId { get; init; }
    public required ushort[] Pixels { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ExposureRequestDto Request { get; init; }
    public required ExposureRequestDto Applied { get; init; }
    public required string ImageType { get; init; }
    public required FrameType FrameType { get; init; }
    public required string TargetName { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public int? FocuserPosition { get; init; }
    public double? RotatorDeg { get; init; }
    public CameraService.FramePointing? Pointing { get; init; }
    public double? SensorTemp { get; init; }
    public double? TempSetPoint { get; init; }
    public double? CoolerSetpointC { get; init; }
    public ObservingConditionsDto? Conditions { get; init; }
    public DateOnly? Night { get; init; }
    public Guid? SessionId { get; init; }
    public string? FilePath { get; set; }
}

/// <summary>
/// Background frame saving for the sequencer (NINA's async save): the frame is handed off once it
/// is downloaded and the next exposure starts while it is written, so the write (0.2 s FITS,
/// 0.5 s XISF on a CM4) and its catalog registration overlap sky time instead of adding to it.
///
/// One save runs at a time, oldest first, and at most <see cref="SaveQueueCapacity"/> frames wait
/// in memory: a third hand-off waits for a slot. A save that fails stops the queue with the frame
/// still held. The next capture retries it before the shutter opens, and if it fails again the
/// capture throws <see cref="StoreUnavailableException"/>, which pauses the run awaiting the user
/// (#1374: no disk, no run). Resume retries again, so a re-plugged drive gets the held frames and
/// nothing is lost. A run drains the queue before it ends (<see cref="FinishPendingSavesAsync"/>).
/// </summary>
public sealed partial class CameraService {
    // The frame being written plus one waiting: two full frames of RAM at most (104 MB for a
    // 26 MP sensor) beside the one downloading.
    internal const int SaveQueueCapacity = 2;

    private static readonly TimeSpan PendingSaveShutdownWait = TimeSpan.FromSeconds(30);

    private readonly Lock _saveQueueGate = new();
    // Oldest first; the head is the frame being written (or the one that failed).
    private readonly List<FrameSaveJob> _saveQueue = [];
    // Background and inline saves never write at once: two writers could pick the same templated name.
    private readonly Lock _saveWriteGate = new();
    private Task _saveWorker = Task.CompletedTask;
    private bool _saveWorkerRunning;
    private Exception? _saveFault;

    /// <summary>Frames handed off but not yet written and registered, held ones included.</summary>
    internal bool HasPendingSaves {
        get {
            lock (_saveQueueGate) {
                return _saveQueue.Count > 0;
            }
        }
    }

    /// <summary>A save failed and its frame is held until the next capture's retry.</summary>
    internal bool SaveHeld {
        get {
            lock (_saveQueueGate) {
                return _saveFault is not null;
            }
        }
    }

    /// <summary>Writes and registers <paramref name="job"/>, then starts its enrichment (preview,
    /// background, star analysis). Throws when the write or the registration fails.</summary>
    internal async Task SaveFrameAsync(FrameSaveJob job) {
        var timing = Stopwatch.StartNew();
        string path;
        lock (_saveWriteGate) {
            path = job.FilePath ??= WriteFrame(job.FrameId, job.Pixels, job.Width, job.Height, job.Applied, job.ImageType, job.CapturedAt,
                job.FocuserPosition, targetName: job.TargetName, sensorTemp: job.SensorTemp, tempSetPoint: job.TempSetPoint,
                conditions: job.Conditions, pointing: job.Pointing, rotatorMechanicalDeg: job.RotatorDeg, night: job.Night);
        }
        var writeMs = timing.ElapsedMilliseconds;
        await RegisterFrameAsync(job.FrameId, job.Request, job.FrameType, job.TargetName, job.CapturedAt, path,
            job.Width, job.Height, job.FocuserPosition, appliedBin: job.Applied.BinX, rotatorMechanicalDeg: job.RotatorDeg,
            coolerSetpointC: job.CoolerSetpointC, sessionId: job.SessionId).ConfigureAwait(false);
        LogCaptureComplete(job.FrameId, job.Width, job.Height, path);
        LogCaptureStoreTiming(job.FrameId, writeMs, timing.ElapsedMilliseconds - writeMs);
        AfterFrameSaved(job);
    }

    /// <summary>Hands <paramref name="job"/> to the background saver and returns once it is queued,
    /// waiting only while <see cref="SaveQueueCapacity"/> frames are already waiting.</summary>
    internal async Task EnqueueSaveAsync(FrameSaveJob job) {
        while (true) {
            Task worker;
            lock (_saveQueueGate) {
                // A held (failed) frame never frees its slot on its own; the next capture's retry
                // deals with it, so this frame joins the queue behind it.
                if (_saveQueue.Count < SaveQueueCapacity || _saveFault is not null) {
                    _saveQueue.Add(job);
                    if (_saveFault is null) {
                        StartSaveWorkerLocked();
                    }
                    return;
                }
                worker = _saveWorker;
            }
            await worker.ConfigureAwait(false);
        }
    }

    private void StartSaveWorkerLocked() {
        if (_saveWorkerRunning) {
            return;
        }
        _saveWorkerRunning = true;
        _saveWorker = Task.Run(RunSaveWorkerAsync);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The save worker's boundary: any write or catalog fault holds the frame for the retry before the next exposure, which turns it into a run pause; the worker itself must never fault.")]
    private async Task RunSaveWorkerAsync() {
        while (true) {
            FrameSaveJob job;
            lock (_saveQueueGate) {
                if (_saveQueue.Count == 0) {
                    _saveWorkerRunning = false;
                    return;
                }
                job = _saveQueue[0];
            }
            try {
                await SaveFrameAsync(job).ConfigureAwait(false);
            } catch (Exception ex) {
                LogBackgroundSaveFailed(ex, job.FrameId, job.FilePath ?? "(not written)");
                lock (_saveQueueGate) {
                    _saveFault = ex;
                    _saveWorkerRunning = false;
                }
                return;
            }
            lock (_saveQueueGate) {
                _saveQueue.RemoveAt(0);
            }
        }
    }

    /// <summary>Restarts the queue (retrying a held frame) and waits until it is empty or fails again.
    /// Returns the fault when frames are still held, else null.</summary>
    private async Task<Exception?> FlushSavesAsync() {
        Task worker;
        lock (_saveQueueGate) {
            _saveFault = null;
            if (_saveQueue.Count > 0) {
                StartSaveWorkerLocked();
            }
            worker = _saveWorker;
        }
        await worker.ConfigureAwait(false);
        lock (_saveQueueGate) {
            return _saveFault;
        }
    }

    /// <summary>
    /// Before a sequencer exposure: a frame whose background save failed is retried now, before the
    /// shutter opens. Still failing → <see cref="StoreUnavailableException"/>, so the run pauses with
    /// the frame held rather than exposing more frames that can't be saved. A save still running
    /// without a fault is left to overlap this exposure.
    /// </summary>
    internal async Task RetryFailedSavesAsync() {
        lock (_saveQueueGate) {
            if (_saveFault is null) {
                return;
            }
        }
        if (await FlushSavesAsync().ConfigureAwait(false) is { } fault) {
            throw new StoreUnavailableException(UnsavedFrameMessage(fault), fault);
        }
    }

    internal static string UnsavedFrameMessage(Exception fault) =>
        $"A frame couldn't be saved to the storage drive ({fault.Message}). Ara is holding it in memory and saves it "
        + "before the next exposure: check the drive, then resume.";

    /// <inheritdoc />
    public async Task<int> FinishPendingSavesAsync() {
        if (await FlushSavesAsync().ConfigureAwait(false) is null) {
            return 0;
        }
        List<FrameSaveJob> lost;
        lock (_saveQueueGate) {
            lost = [.. _saveQueue];
            _saveQueue.Clear();
            _saveFault = null;
        }
        foreach (var job in lost) {
            LogUnsavedFrameDropped(job.FrameId, job.FilePath ?? "(not written)");
        }
        return lost.Count;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Shutdown is best-effort: a save that can't finish in the bound is logged by the saver; Dispose must not throw.")]
    private void FinishPendingSavesForDispose() {
        try {
            if (!FinishPendingSavesAsync().Wait(PendingSaveShutdownWait)) {
                LogPendingSavesAtShutdown();
            }
        } catch (Exception ex) {
            LogBackgroundSaveFailed(ex, Guid.Empty, "(shutdown)");
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Frame {FrameId} could not be saved ({Path}); it is held in memory and retried before the next exposure.")]
    private partial void LogBackgroundSaveFailed(Exception ex, Guid frameId, string path);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Frame {FrameId} was never saved and is dropped as its run ends ({Path}). A written but unregistered file is recovered by the startup scan.")]
    private partial void LogUnsavedFrameDropped(Guid frameId, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Frames were still being saved when the daemon shut down.")]
    private partial void LogPendingSavesAtShutdown();
}
