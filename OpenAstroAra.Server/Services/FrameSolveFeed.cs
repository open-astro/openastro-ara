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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Interfaces;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The side the camera's capture path writes to: one call per LIGHT frame, in capture order.</summary>
public interface ISavedLightSink {
    /// <param name="saved">Completes once the frame is on disk and in the catalog (faults when it never
    /// is). Sequence frames are saved in the background, so a light is recorded before its save lands;
    /// null means it is already saved.</param>
    void RecordLightSaved(Guid frameId, Task? saved = null);
}

/// <summary>
/// #1195 — the saved-light solve feed the sequencer's <c>CenterAfterDriftTrigger</c> reads
/// (<see cref="IFrameSolveFeed"/>) and <see cref="CameraService"/> writes (<see cref="ISavedLightSink"/>).
/// A solve re-reads the catalogued FITS from disk (the frame repository owns the path) and runs it
/// through the same <see cref="IPlateSolveService"/> the REST solve uses, seeded with the mount's
/// current position. Solves are serialized: on the Pi one ASTAP run at a time beside the §59.5
/// analysis worker is the budget, and the trigger only ever asks for the newest frame anyway.
/// </summary>
public sealed partial class FrameSolveFeed : IFrameSolveFeed, ISavedLightSink, IDisposable {
    private readonly IFrameRepository? _frames;
    private readonly IPlateSolveService? _solver;
    private readonly IProfileService? _profileService;
    private readonly Func<ITelescopeMediator?>? _telescope;
    private readonly ILogger<FrameSolveFeed> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _solveGate = new(1, 1);
    private long _sequence;
    private SavedLight? _latest;
    // Lights recorded before their background save landed, by id; removed once it does.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Task> _pendingSaves = new();

    public FrameSolveFeed(IFrameRepository? frames = null, IPlateSolveService? solver = null,
            IProfileService? profileService = null, Func<ITelescopeMediator?>? telescope = null,
            ILogger<FrameSolveFeed>? logger = null, Func<DateTimeOffset>? clock = null) {
        _frames = frames;
        _solver = solver;
        _profileService = profileService;
        _telescope = telescope;
        _logger = logger ?? NullLogger<FrameSolveFeed>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SavedLight? LatestLight => Volatile.Read(ref _latest);

    public void RecordLightSaved(Guid frameId, Task? saved = null) {
        if (saved is { IsCompleted: false }) {
            _pendingSaves[frameId] = saved;
            _ = saved.ContinueWith(_ => _pendingSaves.TryRemove(frameId, out var _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        } else if (saved is { IsCompletedSuccessfully: false }) {
            _pendingSaves[frameId] = saved; // already failed: the solve reports it unsolved
        }
        var seq = Interlocked.Increment(ref _sequence);
        Volatile.Write(ref _latest, new SavedLight(frameId, seq, _clock()));
    }

    /// <summary>Test seam: load a catalogued frame's pixels. Default reads the FITS at the catalog's path.</summary>
    internal Func<string, CancellationToken, Task<(ushort[] Pixels, int Width, int Height, int Binning)>>? PixelLoader { get; set; }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort drift measurement: a missing file, a cfitsio fault or a solver failure must read as 'did not solve' (logged), never fail the trigger or the run. Log-and-recover boundary.")]
    public async Task<Coordinates?> SolveFrameAsync(Guid frameId, CancellationToken ct) {
        if (_frames is null || _solver is null) {
            return null;
        }
        await _solveGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_pendingSaves.TryGetValue(frameId, out var saved)) {
                try {
                    // The light was recorded at hand-off; its file and catalog row land with the save.
                    await saved.WaitAsync(ct).ConfigureAwait(false);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    LogFrameNeverSaved(frameId);
                    return null;
                }
            }
            var frame = await _frames.GetAsync(frameId, ct).ConfigureAwait(false);
            if (frame is null || string.IsNullOrWhiteSpace(frame.FilePath)) {
                LogFrameMissing(frameId);
                return null;
            }
            var (pixels, width, height, binning) = await (PixelLoader ?? LoadFrameAsync)(frame.FilePath, ct).ConfigureAwait(false);
            // The solver ignores the CFA; a one-shot-colour mosaic solves as luminance like every other path here.
            // The frame's binning sets the solve scale (#1136): a 2x2 light is not a 1x1 one.
            var metaData = new OpenAstroAra.Image.ImageData.ImageMetaData();
            metaData.Camera.BinX = binning;
            metaData.Camera.BinY = binning;
            var image = new OpenAstroAra.Image.ImageData.BaseImageData(
                pixels, width, height, bitDepth: 16, isBayered: false,
                metaData, _profileService!, null!, null!);
            Coordinates? hint = null;
            try {
                var telescope = _telescope?.Invoke();
                hint = telescope?.GetInfo() is { Connected: true } ? telescope.GetCurrentPosition() : null;
            } catch (Exception ex) {
                LogHintFailed(ex);
            }
            var result = await _solver.SolveImage(image, hint, progress: null, ct).ConfigureAwait(false);
            if (result?.Success != true || result.Coordinates is null) {
                LogSolveFailed(frameId);
                return null;
            }
            return result.Coordinates;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogSolveFaulted(ex, frameId);
            return null;
        } finally {
            _solveGate.Release();
        }
    }

    public void Dispose() => _solveGate.Dispose();

    // #1148 — the catalogued frame is FITS or XISF; the reader seam dispatches on the extension.
    private static Task<(ushort[] Pixels, int Width, int Height, int Binning)> LoadFrameAsync(string path, CancellationToken ct) =>
        Task.Run(() => {
            var frame = FrameFileReader.Read(path);
            return (frame.Pixels, frame.Width, frame.Height, SqliteFrameRepository.BinningFromHeaders(frame.Headers));
        }, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Center after drift: frame {FrameId} is not in the catalog (or has no file) — not solved")]
    private partial void LogFrameMissing(Guid frameId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Center after drift: frame {FrameId} was never saved — not solved")]
    private partial void LogFrameNeverSaved(Guid frameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Center after drift: frame {FrameId} did not plate-solve")]
    private partial void LogSolveFailed(Guid frameId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Center after drift: solving frame {FrameId} faulted")]
    private partial void LogSolveFaulted(Exception ex, Guid frameId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Center after drift: reading the mount position for the solve hint failed; solving blind")]
    private partial void LogHintFailed(Exception ex);
}
