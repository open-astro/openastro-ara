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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services.LiveStack;
using OpenAstroAra.Stretch;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// ↑ara#1787 Session Live Stack: a colour <see cref="LiveStackEngine"/> fed by the sequencer's background-saved lights
/// (<see cref="AfterFrameSaved"/>), beside Live View's engine; the two share no state. Off at daemon start; the switch
/// lives in memory and survives Sessions and a camera disconnect (the stack clears, the switch stays: known limit).
/// One long-lived worker stacks one light while one waits (at most two pending); a third is counted
/// <c>skipped_busy</c> (saved and catalogued all the same). The engine's session argument is the generation: Reset, Stop,
/// disconnect and a restart-key change bump it, so an older light is dropped and the status reads empty at once.
/// Lock order: <c>_sessionStackSync</c> is a leaf (fields only); the engine is never called while it, <c>_gate</c> or
/// <c>_liveViewMutex</c> is held, because Enable/Disable wait for a running AddColour (seconds on a Pi).
/// </summary>
public sealed partial class CameraService : ISessionLiveStackControl {

    private sealed record SessionStackJob(ushort[] Pixels, int Width, int Height, BayerPattern Pattern,
        string TargetName, string? FilterName, Guid? SessionId, long Generation);

    private LiveStackEngine _sessionStack = new();
    private readonly Lock _sessionStackSync = new();
    // Unbounded on purpose: the bound is _sessionStackPending (one stacking plus one waiting). A bounded(1) channel keeps its slot
    // taken until the worker thread runs TryRead, so a light fed right behind another read as busy though the stacker was idle.
    private readonly Channel<SessionStackJob> _sessionStackQueue = Channel.CreateUnbounded<SessionStackJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _sessionStackPending;
    private const int SessionStackMaxPending = 2;
    private Task? _sessionStackWorker;
    // Set once the worker is about to read: Start waits for it, so a light fed right after never finds the slot taken by a worker that has not started.
    private readonly TaskCompletionSource _sessionStackWorkerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // All below guarded by _sessionStackSync.
    private bool _sessionStackOn;
    private long _sessionStackGeneration;
    private (string Target, string? Filter, Guid? Session, int Width, int Height)? _sessionStackKey;
    private int _sessionStackSkippedBusy;
    private string? _sessionStackOutcome;

    internal LiveStackEngine SessionLiveStack => _sessionStack;

    /// <summary>Test seam (↑ara#1787): swaps the engine (a small pixel cap) before the stack is switched on.</summary>
    internal void UseSessionLiveStackForTest(LiveStackEngine engine) => _sessionStack = engine;

    public void StartSessionLiveStack() {
        long gen;
        lock (_sessionStackSync) {
            if (_sessionStackOn) {
                return;
            }
            _sessionStackOn = true;
            gen = BumpSessionStackLocked();
            _sessionStackWorker ??= Task.Run(RunSessionStackWorkerAsync);
        }
        _sessionStack.Enable(gen);
        _sessionStackWorkerReady.Task.Wait(TimeSpan.FromSeconds(2));
    }

    public void StopSessionLiveStack() {
        lock (_sessionStackSync) {
            _sessionStackOn = false;
            BumpSessionStackLocked();
        }
        _sessionStack.Disable();
    }

    public void ResetSessionLiveStack() {
        long gen;
        lock (_sessionStackSync) {
            if (!_sessionStackOn) {
                return;
            }
            gen = BumpSessionStackLocked();
        }
        _sessionStack.Enable(gen);
    }

    // Caller holds _sessionStackSync: a new generation, with the key, the skipped count and the outcome cleared.
    private long BumpSessionStackLocked() {
        _sessionStackKey = null;
        _sessionStackSkippedBusy = 0;
        _sessionStackOutcome = null;
        return ++_sessionStackGeneration;
    }

    // The camera session ended (disconnect, connect supersede): the old camera's stack and its queued lights go. Called under
    // _gate, so the engine is cleared off this thread.
    private void ClearSessionStackForNewSession() {
        long gen;
        lock (_sessionStackSync) {
            if (!_sessionStackOn) {
                return;
            }
            gen = BumpSessionStackLocked();
        }
        _ = Task.Run(() => _sessionStack.Enable(gen));
    }

    public SessionLiveStackStatusDto GetSessionLiveStackStatus() {
        string? unsupported;
        lock (_gate) {
            unsupported = SessionStackUnsupportedLocked();
        }
        bool on;
        long gen;
        (string Target, string? Filter, Guid? Session, int Width, int Height)? key;
        int skipped;
        string? outcome;
        lock (_sessionStackSync) {
            (on, gen, key, skipped, outcome) = (_sessionStackOn, _sessionStackGeneration, _sessionStackKey, _sessionStackSkippedBusy, _sessionStackOutcome);
        }
        var s = _sessionStack.Snapshot;
        var current = s.SessionId == gen; // an older generation's snapshot reads as empty
        return new SessionLiveStackStatusDto(
            Enabled: on, Unsupported: unsupported, SessionId: key?.Session,
            TargetName: key?.Target, FilterName: key?.Filter,
            StackSeq: s.Seq, LightsStacked: current ? s.FramesStacked : 0, LightsUnregistered: current ? s.FramesUnregistered : 0,
            LightsSkippedBusy: skipped, RejectedSamples: current ? s.RejectedSamples : 0,
            Width: current ? s.Width : null, Height: current ? s.Height : null,
            LastOutcome: outcome ?? (current && s.LastOutcome is { } o ? SessionOutcomeToken(o) : null),
            LastLightAtUtc: current ? s.LastFrameAt?.ToString("O", CultureInfo.InvariantCulture) : null);
    }

    public (ReadOnlyMemory<byte> Jpeg, long Seq, int Lights)? GetSessionLiveStackFrame() {
        long gen;
        lock (_sessionStackSync) {
            gen = _sessionStackGeneration;
        }
        var s = _sessionStack.Snapshot;
        return s.Jpeg is null || s.SessionId != gen ? null : (s.Jpeg, s.Seq, s.FramesStacked);
    }

    // From the capabilities only, so an offline or mono camera reads the same before and after the first light.
    private string? SessionStackUnsupportedLocked() {
        var caps = _capabilities;
        if (caps is null) {
            return "no_camera";
        }
        if (!Debayer.TryParse(caps.BayerPattern, out _)) {
            return "not_colour";
        }
        return (long)(caps.SensorWidth / 2) * (caps.SensorHeight / 2) > LiveStackEngine.DefaultMaxPixels ? "too_large" : null;
    }

    private static string SessionOutcomeToken(StackAddOutcome o) => o switch {
        StackAddOutcome.Reference => "reference",
        StackAddOutcome.Stacked => "stacked",
        StackAddOutcome.Flipped => "flipped",
        StackAddOutcome.Unregistered => "unregistered",
        StackAddOutcome.WrongSize => "wrong_size",
        StackAddOutcome.TooLarge => "too_large",
        _ => "dropped",
    };

    // On the save worker, after the light is saved and catalogued. Never waits for the stacker (invariant: capture never waits).
    private void FeedSessionLiveStack(FrameSaveJob job) {
        if (!job.Background || job.FrameType != FrameType.Light) {
            return;
        }
        long gen;
        lock (_sessionStackSync) {
            if (!_sessionStackOn) {
                return;
            }
            gen = _sessionStackGeneration;
        }
        string? bayer;
        lock (_gate) {
            bayer = _capabilities?.BayerPattern;
        }
        if (!Debayer.TryParse(bayer, out var pattern) || job.Applied.BinX != 1 || job.Applied.BinY != 1) {
            SetSessionStackOutcome(gen, "not_colour");
            return;
        }
        var filter = string.IsNullOrWhiteSpace(job.Request.FilterName) ? null : job.Request.FilterName;
        if (Interlocked.Increment(ref _sessionStackPending) > SessionStackMaxPending
                || !_sessionStackQueue.Writer.TryWrite(new SessionStackJob(job.Pixels, job.Width, job.Height, pattern,
                    job.TargetName, filter, job.SessionId, gen))) {
            Interlocked.Decrement(ref _sessionStackPending);
            lock (_sessionStackSync) {
                if (_sessionStackOn && _sessionStackGeneration == gen) {
                    _sessionStackSkippedBusy++;
                    _sessionStackOutcome = "skipped_busy";
                }
            }
        }
    }

    private void SetSessionStackOutcome(long gen, string outcome) {
        lock (_sessionStackSync) {
            if (_sessionStackOn && _sessionStackGeneration == gen) {
                _sessionStackOutcome = outcome;
            }
        }
    }

    private async Task RunSessionStackWorkerAsync() {
        _sessionStackWorkerReady.TrySetResult();
        await foreach (var job in _sessionStackQueue.Reader.ReadAllAsync().ConfigureAwait(false)) {
            StackOneLight(job);
            Interlocked.Decrement(ref _sessionStackPending);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Session stack worker boundary: a stack failure on one light is a log line and the next light retries; it never reaches the save or the capture.")]
    private void StackOneLight(SessionStackJob job) {
        try {
            var gen = job.Generation;
            var key = (job.TargetName, job.FilterName, job.SessionId, job.Width, job.Height);
            bool restart;
            lock (_sessionStackSync) {
                if (!_sessionStackOn || _sessionStackGeneration != gen) {
                    return; // the stack was reset, stopped or the camera went since this light was fed
                }
                restart = _sessionStackKey is { } last && last != key;
                if (restart) {
                    gen = BumpSessionStackLocked();
                }
                _sessionStackKey = key;
            }
            if (restart) {
                _sessionStack.Enable(gen);
            }
            var r = _sessionStack.AddColour(job.Pixels, job.Width, job.Height, job.Pattern, gen);
            if (r.Outcome != StackAddOutcome.Dropped) {
                lock (_sessionStackSync) {
                    if (_sessionStackGeneration == gen) {
                        _sessionStackOutcome = null; // the engine's own outcome reads through
                    }
                }
            }
        } catch (Exception ex) {
            LogSessionLiveStackFailed(ex);
        }
    }

    [LoggerMessage(EventId = 6416, Level = LogLevel.Warning, Message = "Session live stack light skipped after an error.")]
    partial void LogSessionLiveStackFailed(Exception ex);
}
