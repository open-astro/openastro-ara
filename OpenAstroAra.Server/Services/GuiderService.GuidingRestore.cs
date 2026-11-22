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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2169 — the guider link went down while guiding (rig night 2026-11-21: the guider was killed mid-frame).
/// The shared reaction's pause stops the light in progress (<see cref="IGuiderEpisodeListener.GuidedWhenLost"/>),
/// so no unguided frame is saved or counted (a guide camera dropped while guiding is treated the same way).
/// Once the §63.3 auto-reconnect (or any connect) brings the link
/// back, PHD2 is up with guiding stopped: guiding is started again and must settle before the runs resume
/// (<see cref="IGuiderEpisodeListener.RestoreGuidingAsync"/>), and the stopped light is taken again. A start
/// that fails or never settles is tried again later by the shared reaction until it works or the user takes
/// the runs over. A guider loss during the restore cuts it short; the reaction waits for that loss's recovery
/// and restores again.
/// A guide camera drop is closed only by real frames (rig check 2026-11-21: the guider said "camera back"
/// while the camera was still unplugged): a guide step, or a guide frame this service takes to check. From the
/// drop until then the camera watch (<see cref="WatchGuideCameraAsync"/>) takes one such frame now and then,
/// whether or not a sequence is paused for it. The guider's "camera back" event is a hint that makes the watch
/// check now. The shared reaction waits for the fault's recovery like any other.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>How long a restarted guider may take to settle (after PHD2's own settle wait) before the
    /// runs are left paused. Internal so tests can shorten it.</summary>
    internal TimeSpan GuidingRestoreSettleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the restore waits for the new connection's guide-camera reconcile to start and
    /// finish before it starts guiding anyway.</summary>
    internal TimeSpan GuidingRestoreReconcileWait { get; set; } = TimeSpan.FromSeconds(30);

    // The last link-down or guide-camera-drop fault published while guiding was on (under _gate). Only
    // compared by reference, so a stale one never matches a later fault.
    private EquipmentFaultEvent? _guidedLossFault;
    private int _reconcileStartsAtLinkLoss;        // ReconcileStarts when the link went down (under _gate)

    /// <summary>Exposure of the frame that checks a dropped guide camera is back.</summary>
    internal TimeSpan GuideCameraCheckExposure { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long that frame may take to arrive before the check counts as failed.</summary>
    internal TimeSpan GuideCameraCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>#2169 — the waits between the guide frames that check a dropped camera; the last one repeats.
    /// Internal so tests can shorten it.</summary>
    internal IReadOnlyList<TimeSpan> GuideCameraRetryBackoff { get; set; } = [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
    ];

    // Completed by the guider's "camera back" while a camera-drop fault is open: the watch checks now (under _gate).
    private TaskCompletionSource _cameraBackHint = NewCameraBackHint();
    private int _cameraChecksFailed; // failed check frames for the open camera-drop fault (under _gate)

    private static TaskCompletionSource NewCameraBackHint() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    int IGuiderEpisodeListener.FailedCameraChecks(EquipmentFaultEvent fault) {
        lock (_gate) {
            return ReferenceEquals(fault, _cameraFault) ? _cameraChecksFailed : 0;
        }
    }

    // Caller holds _gate. A new camera-drop fault: its watch runs until frames close it, a user disconnect or
    // the service's dispose drops it.
    private void StartCameraWatchLocked(EquipmentFaultEvent fault) {
        _cameraBackHint = NewCameraBackHint(); // a "camera back" from before this drop does not count
        _cameraChecksFailed = 0;
        var token = _shutdownCts.Token;
        _ = Task.Run(() => WatchGuideCameraAsync(fault, token), token);
    }

    // Caller holds _gate. Real frames came from the guide camera: an open camera-drop fault is over.
    private void RecoverCameraFaultOnFramesLocked() {
        if (_cameraFault is not null) {
            LogCameraFramesBack();
            RecoverFaultLocked(ref _cameraFault);
        }
    }

    // #2169 — Joey's rule: never give up after one try. Waits (the backoff, or less on the guider's "camera
    // back"), then takes one guide frame; again until a frame comes back or the fault is gone.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget watch: any escape is logged; the fault stays open for a guide step to close.")]
    private async Task WatchGuideCameraAsync(EquipmentFaultEvent fault, CancellationToken ct) {
        try {
            for (var waits = 0; ; waits++) {
                Task hint;
                lock (_gate) {
                    if (_disposed || !ReferenceEquals(fault, _cameraFault)) {
                        return;
                    }
                    hint = _cameraBackHint.Task;
                }
                var backoff = GuideCameraRetryBackoff;
                var delay = backoff.Count == 0 ? TimeSpan.Zero : backoff[Math.Min(waits, backoff.Count - 1)];
                await Task.WhenAny(hint, Task.Delay(delay, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_gate) {
                    if (_cameraBackHint.Task.IsCompleted) {
                        _cameraBackHint = NewCameraBackHint(); // used: the next wait needs a new hint
                    }
                }
                if (!await CheckGuideCameraAsync(fault, ct).ConfigureAwait(false)) {
                    lock (_gate) {
                        if (ReferenceEquals(fault, _cameraFault)) {
                            _cameraChecksFailed++;
                        }
                    }
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // the service is disposed
        } catch (Exception ex) {
            LogGuideCameraCheckFailed(ex.Message);
        }
    }

    // One guide frame: true = it came back and [fault] is recovered (or gone already).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A capture that throws (camera still gone, RPC) is a failed check: tried again later. Logged.")]
    private async Task<bool> CheckGuideCameraAsync(EquipmentFaultEvent fault, CancellationToken ct) {
        PHD2Guider guider;
        lock (_gate) {
            if (!ReferenceEquals(fault, _cameraFault)) {
                return true; // frames came back already, or the user let the guider go
            }
            if (_guider is null || _state != EquipmentConnectionState.Connected) {
                return false; // the link is down too: checked again once it is back
            }
            guider = _guider;
        }
        var frame = new TaskCompletionSource<SingleFrameCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, SingleFrameCompleteEventArgs e) => frame.TrySetResult(e);
        guider.SingleFrameComplete += OnFrame;
        try {
            await guider.CaptureSolverFrameAsync((int)GuideCameraCheckExposure.TotalMilliseconds, binning: null, gain: null,
                subframe: null, path: null, save: false, ct).ConfigureAwait(false);
            var done = await frame.Task.WaitAsync(GuideCameraCheckExposure + GuideCameraCheckTimeout, ct).ConfigureAwait(false);
            if (!done.Success) {
                LogGuideCameraCheckFailed(done.Error ?? "the frame failed");
                return false;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogGuideCameraCheckFailed(ex.Message);
            return false;
        } finally {
            guider.SingleFrameComplete -= OnFrame;
        }
        lock (_gate) {
            if (ReferenceEquals(guider, _guider) && ReferenceEquals(fault, _cameraFault)) {
                RecoverCameraFaultOnFramesLocked();
            }
            return !ReferenceEquals(fault, _cameraFault);
        }
    }

    // Caller holds _gate. A guide step or guiding_started since the last stop, or PHD2 still reading a
    // guiding state: either says the frame in progress was being guided.
    private bool GuidingOnLocked() => _guidingSeen || (_guider is not null && IsGuidingState(_guider.State));

    bool IGuiderEpisodeListener.GuidedWhenLost(EquipmentFaultEvent fault) {
        lock (_gate) {
            return _guidedLossFault is not null && ReferenceEquals(fault, _guidedLossFault);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A guiding start that throws (out of step, custody, RPC) is a failed restore: the runs stay paused. Logged.")]
    async Task<bool> IGuiderEpisodeListener.RestoreGuidingAsync(IReadOnlyList<Guid> runIds, CancellationToken ct) {
        int reconcileStartsAtLoss;
        lock (_gate) {
            reconcileStartsAtLoss = _reconcileStartsAtLinkLoss;
        }
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null || runIds.Count == 0) {
            return true;
        }
        if ((await sequencer.DaemonPausedRunsAsync(runIds, ct).ConfigureAwait(false)).Count == 0) {
            return true; // the user resumed, paused or stopped the runs: guiding is theirs to start
        }
        if (SafetyHoldsRig?.Invoke() == true) {
            LogGuidingRestoreFailed("the safety engine holds the rig");
            return false;
        }
        await WaitForReconcileAsync(reconcileStartsAtLoss, ct).ConfigureAwait(false);
        LogGuidingRestoreStarted();
        bool started;
        try {
            started = RestartGuidingOverride is { } restart
                ? await restart(ct).ConfigureAwait(false)
                : await StartGuiding(forceCalibration: false, _noProgress, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogGuidingRestoreFailed(ex.Message);
            return false;
        }
        if (!started) {
            LogGuidingRestoreFailed("guiding did not start");
            return false;
        }
        if (!await WaitForGuidingSettledAsync(GuidingRestoreSettleTimeout, ct).ConfigureAwait(false)) {
            LogGuidingRestoreFailed("guiding did not settle");
            return false;
        }
        LogGuidingRestored();
        return true;
    }

    // The connect task starts the guide-camera reconcile right after Connected (and Recovered); guiding
    // starts after it, so it never races a camera re-pick.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The reconcile contains its own errors; a late or faulted one must not block the restart.")]
    private async Task WaitForReconcileAsync(int startsAtLoss, CancellationToken ct) {
        var deadline = DateTime.UtcNow + GuidingRestoreReconcileWait;
        while (ReconcileStarts == startsAtLoss && DateTime.UtcNow < deadline) {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) {
            return;
        }
        try {
            await ReconcileTask.WaitAsync(left, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            // timed out or faulted: start guiding anyway
        }
    }

    [LoggerMessage(EventId = 4261, Level = LogLevel.Information, Message = "#2169 guider back after a loss while guiding — restarting guiding before the paused runs resume")]
    private partial void LogGuidingRestoreStarted();

    [LoggerMessage(EventId = 4262, Level = LogLevel.Information, Message = "#2169 guiding restarted and settled — the paused runs resume and the stopped frame is taken again")]
    private partial void LogGuidingRestored();

    [LoggerMessage(EventId = 4263, Level = LogLevel.Warning, Message = "#2169 guiding not restored after the guider loss ({Why}) — the paused runs stay paused and Ara tries again")]
    private partial void LogGuidingRestoreFailed(string why);

    [LoggerMessage(EventId = 4264, Level = LogLevel.Information, Message = "#2169 real guide frames again — the guide camera drop is over")]
    private partial void LogCameraFramesBack();

    [LoggerMessage(EventId = 4265, Level = LogLevel.Warning, Message = "#2169 guide camera check frame failed ({Why}) — the camera drop stays open and Ara tries again")]
    private partial void LogGuideCameraCheckFailed(string why);
}
