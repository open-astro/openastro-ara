#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 "Guider (PHD2): loses guide star", the long-outage half. The star-lost watch pauses the run
/// and waits for the guider's next guide step. That is right for a short cloud: the guider keeps
/// hunting near its lock position and locks again. On a long one the mount drifts on tracking alone,
/// the star comes back outside the guider's search box, the guider never relocks, and without this
/// the run sat paused all night under a clear sky. So after <see cref="StarLostRecoverAfter"/> the
/// guider's own frames are asked whether stars are back (<c>get_star_centroids</c>, which touches no
/// guider state): clouds still there → ask again in a minute; stars back → stop guiding, re-center
/// on the run's target by plate solve, restart guiding and resume the run. A failed attempt (the
/// solve fails, the restart does not take) keeps the run paused and tries again with backoff —
/// never parks: the safety engine and the plan's own end conditions decide that.
/// </summary>
public sealed partial class GuiderService {

    internal static readonly TimeSpan DefaultStarLostRecoverAfter = TimeSpan.FromMinutes(3);

    /// <summary>How long a star may stay lost (after the pause) before the first clear-sky probe.
    /// Under it the guider's own relock does the recovering; past it the mount has drifted enough
    /// that a relock is unlikely. Internal so tests can shorten it.</summary>
    internal TimeSpan StarLostRecoverAfter { get; set; } = DefaultStarLostRecoverAfter;

    /// <summary>Cadence of the clear-sky probe while the frames stay starless.</summary>
    internal TimeSpan StarLostProbeInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Waits before the next probe after a failed recovery attempt (the last one repeats).</summary>
    internal IReadOnlyList<TimeSpan> StarLostRetryBackoff { get; set; } = new[] {
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15),
    };

    /// <summary>Bound on the re-center (capture → solve → slew iterates for minutes on a slow rig).</summary>
    internal TimeSpan StarLostRecenterTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A guide frame counts as clear when one detected star reaches this SNR; a cloud bank's
    /// noise peaks sit well under it, a real star well over.</summary>
    internal const double StarLostProbeMinSnr = 8.0;

    /// <summary>True while the safety engine owns the rig (unsafe latched, holding paused runs, or
    /// counting down an auto-resume). Set by the composition root; null = no safety engine.</summary>
    public Func<bool>? SafetyHoldsRig { get; set; }

    /// <summary>The shared re-center (horizon guard + bounded CenterOnTarget). Null = this daemon cannot
    /// re-center, and the recovery says so once and leaves the run paused.</summary>
    public TargetRecenter? Recenter { get; set; }

    /// <summary>The restart after a successful re-center: the mediator's StartGuiding (stored
    /// calibration, the guider's own star pick, settle wait). Internal so tests can stand in for the
    /// guider's multi-RPC start flow.</summary>
    internal Func<CancellationToken, Task<bool>>? RestartGuidingOverride { get; set; }

    private Timer? _starLostProbeTimer;          // the next probe / attempt (under _gate)
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Owned by the in-flight probe/attempt task, which disposes it in a finally after nulling this field; Dispose only cancels it (the SafetyReactionService._resumeCountdown pattern).")]
    private CancellationTokenSource? _recoveryCts; // the in-flight probe/attempt's token (under _gate)
    private bool _recoveryInFlight;
    private bool _recoveryStoppedGuiding;        // this episode's attempt stopped the guider itself
    private bool _recoveryGaveUp;                // target too low / nothing to re-center with: no more probes
    private int _recoveryFailures;               // failed attempts this episode (drives the backoff)
    private bool _recoveryFailureNotified;       // one Warning per episode, not one per retry

    // Caller holds _gate. Arms the next probe; a no-op once the episode is over or given up.
    private void ArmStarLostRecoveryLocked(TimeSpan delay) {
        if (_disposed || !_starLostReacted || _starLostPausedRuns.Count == 0 || _recoveryGaveUp) {
            return;
        }
        _starLostProbeTimer?.Dispose();
        _starLostProbeTimer = new Timer(OnStarLostProbeDue, state: null, delay, Timeout.InfiniteTimeSpan);
    }

    // Caller holds _gate. Stops the next probe and cancels an attempt in flight (the attempt's own
    // finally disposes the token source).
    private void DisarmStarLostRecoveryLocked() {
        _starLostProbeTimer?.Dispose();
        _starLostProbeTimer = null;
        _recoveryCts?.Cancel();
    }

    // Caller holds _gate. A new episode starts clean.
    private void ResetStarLostRecoveryLocked() {
        DisarmStarLostRecoveryLocked();
        _recoveryStoppedGuiding = false;
        _recoveryGaveUp = false;
        _recoveryFailures = 0;
        _recoveryFailureNotified = false;
    }

    private void OnStarLostProbeDue(object? _) {
        PHD2Guider guider;
        CancellationTokenSource cts;
        lock (_gate) {
            _starLostProbeTimer?.Dispose();
            _starLostProbeTimer = null;
            if (_disposed || _guider is null || _state != EquipmentConnectionState.Connected
                || !_starLostReacted || _starLostPausedRuns.Count == 0 || _recoveryGaveUp) {
                return;
            }
            if (_recoveryInFlight) {
                ArmStarLostRecoveryLocked(StarLostProbeInterval);
                return;
            }
            // The star-lost pause is the guider's to recover only while the guider is still the one
            // looking: once it relocks (Guiding) the guide step resumes the run; once it was stopped by
            // the user or a park there is nothing to probe. Our own stop (a failed attempt) keeps the
            // episode alive: the guider was put back to looping for the next probe.
            var state = _guider.AppState?.State;
            var ownStop = _recoveryStoppedGuiding && (state == PhdAppState.LOOPING || state == PhdAppState.STOPPED);
            if (state != PhdAppState.LOSTLOCK && !ownStop) {
                return;
            }
            if (SafetyHoldsRig?.Invoke() == true) {
                LogStarLostRecoveryDeferredToSafety();
                ArmStarLostRecoveryLocked(StarLostProbeInterval);
                return;
            }
            guider = _guider;
            cts = new CancellationTokenSource();
            _recoveryCts = cts;
            _recoveryInFlight = true;
        }
        _ = Task.Run(() => ProbeAndRecoverAsync(guider, cts), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background recovery boundary: any rig fault leaves the run paused and schedules a retry; logged, never an unobserved task exception.")]
    private async Task ProbeAndRecoverAsync(PHD2Guider guider, CancellationTokenSource cts) {
        var ct = cts.Token;
        TimeSpan? next = null;
        try {
            if (!await EpisodeStillOursAsync(ct).ConfigureAwait(false)) {
                return; // the user acted on the pause: no more probes
            }
            if (!await SkyIsClearAsync(guider, ct).ConfigureAwait(false)) {
                LogStarLostProbeStillCloudy();
                next = StarLostProbeInterval;
                return;
            }
            next = await RecoverAsync(guider, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // A guide step, a stop, a disconnect or the user ended the episode: nothing of ours is left.
        } catch (Exception ex) {
            LogStarLostRecoveryFailed(ex);
            next = await OnAttemptFailedAsync(guider, ex.Message).ConfigureAwait(false);
        } finally {
            lock (_gate) {
                _recoveryInFlight = false;
                if (ReferenceEquals(_recoveryCts, cts)) {
                    _recoveryCts = null;
                }
                if (next is { } delay && !ct.IsCancellationRequested) {
                    ArmStarLostRecoveryLocked(delay);
                }
            }
            cts.Dispose();
        }
    }

    /// <summary>Stars on the guider's current frame, without touching its state. A guider that is not
    /// looping (or any RPC fault) answers "not clear": the next probe asks again.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A probe RPC fault is the same as a starless frame: try again next interval. Log-and-recover boundary.")]
    private async Task<bool> SkyIsClearAsync(PHD2Guider guider, CancellationToken ct) {
        try {
            var stars = await guider.GetStarCentroidsAsync(roi: null, maxStars: 5, ct, PHD2Guider.LiveRpcTimeoutMs, notifyOnError: false)
                .ConfigureAwait(false);
            foreach (var star in stars) {
                if (star.Snr is double snr && snr >= StarLostProbeMinSnr) {
                    return true;
                }
            }
            return false;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogStarLostProbeFailed(ex);
            return false;
        }
    }

    /// <summary>Which of the episode's runs are still paused under daemon custody. Empty = the user
    /// resumed, paused or stopped the run (or it finished): the episode is closed here, its fault row
    /// resolved, and the caller stops. Asked before every probe and again right before the slew, so
    /// a Resume pressed during the wait or the stop never meets a mount in motion.</summary>
    private async Task<IReadOnlyList<Guid>> StillDaemonPausedAsync(ISequencerService sequencer, CancellationToken ct) {
        IReadOnlyList<Guid> paused;
        EquipmentFaultEvent? fault;
        lock (_gate) {
            paused = _starLostPausedRuns;
            fault = _starLostFault;
        }
        var still = await sequencer.DaemonPausedRunsAsync(paused, ct).ConfigureAwait(false);
        if (still.Count > 0) {
            return still;
        }
        lock (_gate) {
            if (!_starLostReacted) {
                return still;
            }
            _starLostReacted = false;
            _starLostFault = null;
            _starLostPausedRuns = Array.Empty<Guid>();
            _recoveryStoppedGuiding = false;
        }
        LogStarLostRecoveryUserActed();
        if (fault is not null) {
            await ResolveFaultQuietlyAsync(fault, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        return still;
    }

    private async Task<bool> EpisodeStillOursAsync(CancellationToken ct) {
        var sequencer = _sequencerResolver?.Invoke();
        return sequencer is not null && (await StillDaemonPausedAsync(sequencer, ct).ConfigureAwait(false)).Count > 0;
    }

    /// <summary>One recovery attempt. Returns the delay before the next probe, or null when the episode
    /// is over (resumed, handed to the user, or given up).</summary>
    private async Task<TimeSpan?> RecoverAsync(PHD2Guider guider, CancellationToken ct) {
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null) {
            return null;
        }
        var still = await StillDaemonPausedAsync(sequencer, ct).ConfigureAwait(false);
        if (still.Count == 0) {
            return null;
        }
        EquipmentFaultEvent? fault;
        IReadOnlyList<Guid> paused;
        lock (_gate) {
            fault = _starLostFault;
        }
        var lostMinutes = fault is null ? 0 : Math.Max(1, (int) Math.Round((DateTimeOffset.UtcNow - fault.DetectedUtc).TotalMinutes));
        LogStarLostRecoveryStarted(lostMinutes);

        // 1. Stop the guider chasing a star that is gone; its own guiding_stopped must not end the episode.
        lock (_gate) {
            _recoveryStoppedGuiding = true;
        }
        await guider.StopGuiding(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // The stop takes seconds and the user may have pressed Resume meanwhile: never slew under a
        // run that is imaging again.
        still = await StillDaemonPausedAsync(sequencer, ct).ConfigureAwait(false);
        if (still.Count == 0) {
            return null;
        }

        // 2. Re-center by plate solve: the solve doubles as the clear-sky test for the imaging scope.
        if (Recenter is null) {
            return await GiveUpAsync(fault, "Run stays paused — Ara cannot re-center on this rig",
                $"The guide star has been lost for {lostMinutes} min and the guider has not found it again. "
                + "This daemon has no plate solver wired, so Ara cannot re-center the target itself. Check the sky and press Resume.").ConfigureAwait(false);
        }
        var result = await Recenter.RecenterActiveTargetAsync(sequencer, still, StarLostRecenterTimeout, ct).ConfigureAwait(false);
        switch (result.Outcome) {
            case TargetRecenterOutcome.Recentered:
                break;
            case TargetRecenterOutcome.Cancelled:
                throw new OperationCanceledException(ct);
            case TargetRecenterOutcome.TargetTooLow:
                return await GiveUpAsync(fault, "Run stays paused — target is low",
                    $"The guide star has been lost for {lostMinutes} min. The target now sits at {result.AltitudeDeg:F0}°, below your "
                    + $"{result.LimitDeg:F0}° horizon limit, so Ara did not re-center on it. The plan's altitude conditions decide what's next, or press Resume.").ConfigureAwait(false);
            case TargetRecenterOutcome.NoCenteringService:
            case TargetRecenterOutcome.Unconfigured:
            case TargetRecenterOutcome.NotAttempted:
                return await GiveUpAsync(fault, "Run stays paused — Ara cannot re-center on this rig",
                    $"The guide star has been lost for {lostMinutes} min and the guider has not found it again. "
                    + (result.Outcome == TargetRecenterOutcome.NotAttempted
                        ? "The paused run carries no coordinate target to re-center on. "
                        : "No plate solver is configured, so Ara cannot re-center the target itself. ")
                    + "Check the sky and press Resume.").ConfigureAwait(false);
            default:
                return await OnAttemptFailedAsync(guider, result.Outcome == TargetRecenterOutcome.Timeout ? "the re-center timed out" : "the plate solve failed").ConfigureAwait(false);
        }

        // 3. Guiding again on the guider's own star pick and its stored calibration.
        var guiding = RestartGuidingOverride is { } restart
            ? await restart(ct).ConfigureAwait(false)
            : await StartGuiding(forceCalibration: false, _noProgress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!guiding) {
            return await OnAttemptFailedAsync(guider, "guiding did not restart").ConfigureAwait(false);
        }

        // 4. Resume exactly the runs this episode parked — once. A guide step from the restarted
        // guider leaves the resume to us (NoteGuideStepLocked checks _recoveryStoppedGuiding).
        lock (_gate) {
            if (!_starLostReacted) {
                return null; // released by another path meanwhile
            }
            _starLostReacted = false;
            paused = _starLostPausedRuns;
            fault = _starLostFault;
            _starLostFault = null;
            _starLostPausedRuns = Array.Empty<Guid>();
            _recoveryStoppedGuiding = false;
        }
        if (fault is not null) {
            await RecordActionQuietlyAsync(fault, "recentered").ConfigureAwait(false);
            await ResolveFaultQuietlyAsync(fault, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        var resumed = await sequencer.ResumeRunsAsync(paused, CancellationToken.None).ConfigureAwait(false);
        LogStarLostRecovered(lostMinutes, paused.Count, resumed);
        await PublishFaultEventAsync(new JsonObject {
            ["action"] = "auto_resume",
            ["runs_affected"] = resumed,
            ["kind"] = "star_recentered",
            ["lost_minutes"] = lostMinutes,
        }).ConfigureAwait(false);
        await NotifyQuietlyAsync(NotificationSeverity.Info, $"Guide star lost for {lostMinutes} min — re-centered, run resumed",
            resumed > 0
                ? "The guider never found its star again after the clouds passed, so Ara re-centered the target by plate solve, restarted guiding and resumed the run."
                : "Ara re-centered the target by plate solve and restarted guiding. The run it had paused was not released by this (it had already been resumed, is stopped, or waits on you).")
            .ConfigureAwait(false);
        return null;
    }

    /// <summary>A failed attempt: the run stays paused, the guider goes back to looping so the next
    /// probe has frames, one Warning per episode, and the next probe waits out the backoff.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Putting the guider back to looping is best-effort; the next probe reports a guider that is not looping as 'not clear' and asks again.")]
    private async Task<TimeSpan?> OnAttemptFailedAsync(PHD2Guider guider, string why) {
        int failures;
        bool notify;
        EquipmentFaultEvent? fault;
        lock (_gate) {
            if (!_starLostReacted) {
                return null;
            }
            failures = ++_recoveryFailures;
            notify = !_recoveryFailureNotified;
            _recoveryFailureNotified = true;
            fault = _starLostFault;
        }
        var backoff = StarLostRetryBackoff.Count == 0
            ? StarLostProbeInterval
            : StarLostRetryBackoff[Math.Min(failures, StarLostRetryBackoff.Count) - 1];
        LogStarLostRecoveryAttemptFailed(why, failures, (int) backoff.TotalMinutes);
        try {
            await guider.LoopAsync(CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogStarLostProbeFailed(ex);
        }
        if (notify) {
            if (fault is not null) {
                await RecordActionQuietlyAsync(fault, "recenter_failed").ConfigureAwait(false);
            }
            await NotifyQuietlyAsync(NotificationSeverity.Warning, "Guide star still lost — re-center failed, Ara keeps trying",
                $"Stars are back on the guide frames but the recovery did not go through ({why}). The run stays paused; "
                + $"Ara tries again in {(int) backoff.TotalMinutes} min and keeps trying until the run resumes, you act, or the safety engine takes over.")
                .ConfigureAwait(false);
        }
        return backoff;
    }

    /// <summary>Nothing more this recovery can do for the episode: say so once and stop probing. The
    /// pause stays the guider's (a relock still resumes it) and the user's Resume works as always.</summary>
    private async Task<TimeSpan?> GiveUpAsync(EquipmentFaultEvent? fault, string title, string message) {
        lock (_gate) {
            _recoveryGaveUp = true;
        }
        LogStarLostRecoveryGaveUp(title);
        if (fault is not null) {
            await RecordActionQuietlyAsync(fault, "recenter_unavailable").ConfigureAwait(false);
        }
        await NotifyQuietlyAsync(NotificationSeverity.Warning, title, message).ConfigureAwait(false);
        return null;
    }

    [LoggerMessage(EventId = 4231, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "§42.2 star-lost probe: the guide frame is still starless — asking again next interval")]
    private partial void LogStarLostProbeStillCloudy();

    [LoggerMessage(EventId = 4232, Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "§42.2 star-lost probe RPC failed; treated as a starless frame")]
    private partial void LogStarLostProbeFailed(Exception exception);

    [LoggerMessage(EventId = 4233, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§42.2 star-lost probe deferred: the safety engine holds the rig")]
    private partial void LogStarLostRecoveryDeferredToSafety();

    [LoggerMessage(EventId = 4234, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 guide star lost for {LostMinutes} min and stars are back on the guide frame — stopping guiding, re-centering by plate solve, restarting guiding")]
    private partial void LogStarLostRecoveryStarted(int lostMinutes);

    [LoggerMessage(EventId = 4235, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§42.2 star-lost recovery: the user acted on the paused run — episode handed over")]
    private partial void LogStarLostRecoveryUserActed();

    [LoggerMessage(EventId = 4236, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§42.2 star-lost recovery done after {LostMinutes} min: runsPaused={Paused} runsResumed={Resumed}")]
    private partial void LogStarLostRecovered(int lostMinutes, int paused, int resumed);

    [LoggerMessage(EventId = 4237, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 star-lost recovery attempt failed ({Why}); attempt {Attempt}, next probe in {BackoffMin} min")]
    private partial void LogStarLostRecoveryAttemptFailed(string why, int attempt, int backoffMin);

    [LoggerMessage(EventId = 4238, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 star-lost recovery stopped: {Reason}")]
    private partial void LogStarLostRecoveryGaveUp(string reason);

    [LoggerMessage(EventId = 4239, Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "§42.2 star-lost recovery faulted; the run stays paused and a retry is scheduled")]
    private partial void LogStarLostRecoveryFailed(Exception exception);
}
