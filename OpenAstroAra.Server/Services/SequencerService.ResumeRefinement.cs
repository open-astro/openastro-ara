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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §38.10 — resume refinement: when a user resumes a run that paused on the
/// SAME target it is still on, pointing (and focus) may have drifted while the
/// rig sat idle — so a plate-solve + re-center (and, on request, an autofocus
/// sweep) run BEFORE the pause gate releases, while the engine is suspended
/// and the equipment free. Mirrors the §35 safety auto-resume's
/// <c>TryRecenterQuietlyAsync</c> semantics: bounded, best-effort, honest
/// notifications, and the gate release is NEVER starved by a solve/AF fault.
/// The §35 path itself (<see cref="ResumeRunsAsync"/>) is untouched.
/// </summary>
public sealed partial class SequencerService {

    private readonly Func<ICenteringService?>? _centeringResolver;
    private readonly Func<IAutofocusExecutor?>? _autofocusResolver;

    /// <summary>Bound on the pre-release re-center (mirrors §35's RecenterTimeout);
    /// internal-settable so tests can trip the timeout quickly.</summary>
    internal TimeSpan ResumeRecenterTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Start the §38.10 refinement when it applies. True = a background task now
    /// owns the gate release; false = the caller releases the gate as usual
    /// (options declined it, the plan moved past the paused target, no centering
    /// service is wired, or another refinement already owns the release).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The resolver probes run on the request path between the resume CAS and the gate release; any escape would fail the HTTP request with the gate never released. A probe fault degrades to plain-resume. CA1031's log-and-recover boundary applies.")]
    private bool TryBeginResumeRefinement(Guid id, RunState run, SequenceResumeRequestDto? request) {
        var recenter = request?.Recenter ?? true;
        var refocus = request?.Refocus ?? false;
        if (!recenter && !refocus) return false;

        // Consume the pause snapshot regardless of outcome — it describes THIS
        // pause episode only.
        var paused = run.PausedTarget;
        run.PausedTarget = null;
        if (paused is null) return false;
        var current = run.Root is ISequenceContainer root ? FindActiveDeepSkyTarget(root) : null;
        if (!ReferenceEquals(paused, current)) {
            // The plan moved on (or the target finished) — a re-center here
            // would slew BACKWARDS to the old target.
            return false;
        }
        // Resolver probes are guarded here too (review #873 r3): this runs
        // synchronously inside ResumeAsync AFTER the Paused→Running CAS but
        // BEFORE any Gate.Resume() — a throwing resolver (factory fault,
        // disposed provider during shutdown) escaping would 500 the request
        // with the gate never released. On a probe fault: no refinement, and
        // the caller releases the gate as usual.
        bool wantsCentering, wantsFocus;
        try {
            wantsCentering = recenter && _centeringResolver?.Invoke() is not null;
            wantsFocus = refocus && _autofocusResolver?.Invoke() is not null;
        } catch (Exception ex) {
            LogResumeRefinementProbeFailed(ex, id);
            return false;
        }
        if (!wantsCentering && !wantsFocus) return false;
        if (!run.TryClaimResumeRefinement()) return false;

        _ = Task.Run(() => ResumeRefinementAsync(id, run, paused, wantsCentering, wantsFocus), CancellationToken.None);
        return true;
    }

    // Best-effort boundary: solver/AF/driver code can throw anything; every
    // outcome must end in Gate.Resume() with an honest notification, never a
    // faulted background task. CA1031's log-and-recover boundary applies.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Pre-release refinement boundary: centering/autofocus faults must degrade to a resume-with-warning, never fault the background task or starve the gate release. CA1031's log-and-recover boundary applies.")]
    private async Task ResumeRefinementAsync(Guid id, RunState run, IDeepSkyObjectContainer target, bool recenter, bool refocus) {
        var centeringOutcome = "skipped";
        var focusOutcome = refocus ? "failed" : "skipped";
        try {
            CancellationToken runToken;
            try {
                runToken = run.Cts.Token;
            } catch (ObjectDisposedException) {
                return; // run reached terminal + disposed — nothing to refine
            }
            await EmitResumeRecenteringAsync(id, run, recenter, refocus);

            // §38.10a altitude guard — during a long pause the target may have
            // sunk below the horizon limit; re-centering (and refocusing) on it
            // would waste minutes of rig time on a target the plan's altitude
            // conditions are about to abandon. Unknown altitude (no profile,
            // math fault) proceeds with the refinement — the guard only ever
            // SKIPS work, never blocks a resume.
            var tooLow = TryGetTargetBelowHorizon(target);
            if (tooLow is { } low) {
                LogResumeTargetTooLow(id, low.AltitudeDeg, low.LimitDeg);
                await NotifyTargetTooLowAsync(low.AltitudeDeg, low.LimitDeg, recenter, refocus).ConfigureAwait(false);
                return; // finally releases the gate; the plan's conditions decide what's next
            }

            if (recenter) {
                // Resolver + coordinate reads live INSIDE the guarded region too
                // (review #873): a throw here must still reach the outcome
                // notification below, never fault the discarded Task.Run.
                ICenteringService? centering = null;
                OpenAstroAra.Astrometry.Coordinates? coords = null;
                try {
                    centering = _centeringResolver?.Invoke();
                    coords = target.Target?.InputCoordinates?.Coordinates;
                } catch (Exception ex) {
                    LogResumeRecenterFailed(ex, id);
                    centeringOutcome = "failed";
                }
                if (centeringOutcome == "failed" || centering is null || coords is null) {
                    centeringOutcome = centeringOutcome == "failed" ? "failed" : "skipped";
                } else {
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    bounded.CancelAfter(ResumeRecenterTimeout);
                    try {
                        LogResumeRecenterStarted(id);
                        var result = await centering.CenterOnTarget(coords, solveProgress: null, progress: null, bounded.Token).ConfigureAwait(false);
                        centeringOutcome = result?.Success == true ? "recentered" : "failed";
                    } catch (OperationCanceledException) when (runToken.IsCancellationRequested) {
                        return; // abort/stop — the finally still releases the gate
                    } catch (OperationCanceledException) {
                        LogResumeRecenterTimedOut(id);
                        centeringOutcome = "timeout";
                    } catch (PlateSolverConfigurationException ex) {
                        LogResumeRecenterUnconfigured(ex, id);
                        centeringOutcome = "unconfigured";
                    } catch (Exception ex) {
                        LogResumeRecenterFailed(ex, id);
                        centeringOutcome = "failed";
                    }
                }
            }

            if (refocus) {
                IAutofocusExecutor? autofocus = null;
                try {
                    autofocus = _autofocusResolver?.Invoke();
                } catch (Exception ex) {
                    LogResumeRefocusFailed(ex, id);
                }
                if (autofocus is null) {
                    focusOutcome = "skipped";
                } else {
                    try {
                        var converged = await autofocus.RunAutofocusAsync(
                            new Progress<ApplicationStatus>(_ => { }), runToken).ConfigureAwait(false);
                        focusOutcome = converged ? "focused" : "failed";
                    } catch (OperationCanceledException) when (runToken.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        LogResumeRefocusFailed(ex, id);
                        focusOutcome = "failed";
                    }
                }
            }

            await NotifyResumeRefinementAsync(centeringOutcome, focusOutcome, refocus).ConfigureAwait(false);
        } finally {
            run.ReleaseResumeRefinementClaim();
            // The one release this task owns — imaging continues no matter how
            // the refinement went.
            run.Gate.Resume();
        }
    }

    /// <summary>§38.10a — the target's current altitude vs. the profile's horizon
    /// limit at its azimuth (custom-horizon interpolation when enabled, else the
    /// flat default floor). Null = fine to proceed (above the limit, or the
    /// altitude/limit can't be determined — the guard only ever skips work).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile read or transform fault must degrade to 'unknown — proceed with the refinement', never fault the background task. Log-and-recover boundary.")]
    private (double AltitudeDeg, double LimitDeg)? TryGetTargetBelowHorizon(IDeepSkyObjectContainer target) {
        try {
            var site = _profileStore?.GetSiteSettings();
            var coords = target.Target?.InputCoordinates?.Coordinates;
            if (site is null || coords is null) return null;
            // SiteAstrometry — the server's ONE managed sky model (shared with
            // the §58.9 flip predictor and Tonight's Sky; deliberately not the
            // NOVAS-native Transform, which needs the native library and whose
            // sub-degree precision is irrelevant to a horizon floor).
            var lst = SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, site.LongitudeDeg);
            var hourAngle = (lst - coords.RADegrees % 360.0 + 360.0) % 360.0;
            var altitude = SiteAstrometry.AltitudeFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var azimuth = SiteAstrometry.AzimuthFromHourAngleDeg(coords.Dec, site.LatitudeDeg, hourAngle);
            var limit = HorizonLimitDeg(site, azimuth);
            return altitude < limit ? (altitude, limit) : null;
        } catch (Exception ex) {
            LogResumeAltitudeCheckFailed(ex);
            return null;
        }
    }

    /// <summary>The horizon altitude at <paramref name="azimuthDeg"/>: the §36
    /// custom-horizon skyline interpolated via the ONE shared implementation
    /// (<see cref="CustomHorizonValidator.AltitudeAtAzimuth"/> — review #875 r2:
    /// no second copy of the wraparound math), else the flat default floor. The
    /// stored points are re-normalized here because AltitudeAtAzimuth's contract
    /// requires the canonical sorted/de-duplicated form.</summary>
    private double HorizonLimitDeg(SiteSettingsDto site, double azimuthDeg) {
        if (!site.UseCustomHorizon) return site.DefaultHorizonAltitudeDeg;
        var (normalized, _) = CustomHorizonValidator.Normalize(_profileStore?.GetCustomHorizon());
        var points = normalized?.Points;
        if (points is null || points.Count == 0) return site.DefaultHorizonAltitudeDeg;
        return CustomHorizonValidator.AltitudeAtAzimuth(points, azimuthDeg);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never delay or mask the gate release. Log-and-recover boundary.")]
    private async Task NotifyTargetTooLowAsync(double altitudeDeg, double limitDeg, bool recenterRequested, bool refocusRequested) {
        if (_notifications is null) return;
        // Name only what was actually requested (review #875 r2 — a
        // refocus-only resume must not claim a re-center was skipped).
        var skipped = (recenterRequested, refocusRequested) switch {
            (true, true) => "the re-center and refocus were",
            (true, false) => "the re-center was",
            _ => "the refocus was",
        };
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: NotificationSeverity.Warning,
                Category: NotificationCategory.Sequence,
                Title: "Sequence resumed — target is low",
                Message: $"The paused target now sits at {altitudeDeg:F0}°, below your "
                    + $"{limitDeg:F0}° horizon limit, so {skipped} skipped. "
                    + "The plan's altitude conditions decide whether imaging continues.",
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogResumeNotifyFailed(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification store faults must never delay or mask the gate release. Log-and-recover boundary.")]
    private async Task NotifyResumeRefinementAsync(string centeringOutcome, string focusOutcome, bool refocusRequested) {
        if (_notifications is null) return;
        var pointing = centeringOutcome switch {
            "recentered" => "The target was re-centered by plate solve, so pointing is confirmed.",
            "timeout" => "The re-center timed out — verify the pointing before trusting new frames.",
            "unconfigured" => "No plate solver is configured, so the re-center was skipped — verify the pointing.",
            "failed" => "The re-center did not converge — verify the pointing before trusting new frames.",
            _ => "No re-center was attempted.",
        };
        var focus = !refocusRequested ? string.Empty
            : focusOutcome == "focused"
                ? " Autofocus converged before imaging continued."
                : " The requested autofocus did not complete — check focus on the next frames.";
        try {
            await _notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: DateTimeOffset.UtcNow,
                Severity: centeringOutcome is "recentered" or "skipped" && (focusOutcome is "focused" or "skipped")
                    ? NotificationSeverity.Info
                    : NotificationSeverity.Warning,
                Category: NotificationCategory.Sequence,
                Title: "Sequence resumed",
                Message: pointing + focus,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: null,
                RelatedEntityId: null), CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            LogResumeNotifyFailed(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "WS publish is best-effort; a broadcaster fault must not affect the refinement or the release. Same boundary as EmitAsync.")]
    private async Task EmitResumeRecenteringAsync(Guid sequenceId, RunState run, bool recenter, bool refocus) {
        if (_ws is null) return;
        try {
            var payload = new JsonObject {
                ["sequence_id"] = sequenceId.ToString(),
                ["run_id"] = run.RunId.ToString(),
                ["recenter"] = recenter,
                ["refocus"] = refocus,
            };
            using var doc = JsonDocument.Parse(payload.ToJsonString());
            await _ws.PublishAsync(WsEventCatalog.SequenceResumeRecentering, doc.RootElement.Clone(), CancellationToken.None);
        } catch (Exception) {
            // Best-effort; the refinement itself proceeds.
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10a resume refinement on run {SequenceId}: target sits at {AltitudeDeg}° — below the {LimitDeg}° horizon limit; re-center/refocus skipped")]
    private partial void LogResumeTargetTooLow(Guid sequenceId, double altitudeDeg, double limitDeg);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10a altitude check failed — proceeding with the refinement (the guard only ever skips work)")]
    private partial void LogResumeAltitudeCheckFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10 resume refinement on run {SequenceId}: a service resolver faulted during the probe — resuming without refinement")]
    private partial void LogResumeRefinementProbeFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§38.10 resume refinement on run {SequenceId}: re-centering the paused target before releasing the gate")]
    private partial void LogResumeRecenterStarted(Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10 resume refinement on run {SequenceId}: re-center timed out — resuming with a verify-pointing warning")]
    private partial void LogResumeRecenterTimedOut(Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§38.10 resume refinement on run {SequenceId}: no plate solver configured — re-center skipped")]
    private partial void LogResumeRecenterUnconfigured(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10 resume refinement on run {SequenceId}: re-center failed — resuming with a verify-pointing warning")]
    private partial void LogResumeRecenterFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10 resume refinement on run {SequenceId}: the requested autofocus failed — imaging continues at the prior focus")]
    private partial void LogResumeRefocusFailed(Exception ex, Guid sequenceId);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§38.10 failed to post the resume-refinement notification — the resume itself proceeded")]
    private partial void LogResumeNotifyFailed(Exception ex);
}
