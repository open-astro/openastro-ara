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
        var wantsCentering = recenter && _centeringResolver?.Invoke() is not null;
        var wantsFocus = refocus && _autofocusResolver?.Invoke() is not null;
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

            if (recenter) {
                var centering = _centeringResolver?.Invoke();
                var coords = target.Target?.InputCoordinates?.Coordinates;
                if (centering is null || coords is null) {
                    centeringOutcome = "skipped";
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
                var autofocus = _autofocusResolver?.Invoke();
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
