#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §58.4 meridian-flip orchestration — the real <see cref="IMeridianFlipExecutor"/> that runs once
/// <see cref="OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger"/> has decided a flip is due.
/// Replaces the throwing <c>PlaceholderMeridianFlipExecutor</c> and the deleted WPF <c>MeridianFlipVM</c>:
/// the trigger owns the <em>decision</em> (when), this owns the <em>orchestration</em> (the post-flip
/// recovery sequence, in order).
///
/// The §58.4 recovery sequence, faithfully ported from NINA's <c>MeridianFlipVM.DoMeridianFlip</c>:
/// <list type="number">
///   <item>Stop guiding (if a guider is connected).</item>
///   <item>Pass meridian — stop tracking, wait out <c>timeToFlip</c>, resume tracking.</item>
///   <item>Flip slew — <see cref="ITelescopeMediator.MeridianFlip"/> → settle → best-effort dome sync.</item>
///   <item>Re-focus — <em>deferred</em>: the live focuser V-curve AF sweep is unbuilt (focuser-gated). When
///         <c>AutoFocusAfterFlip</c> is set we log that it was skipped rather than fail the flip (re-focus is
///         best-effort per §58.7).</item>
///   <item>Re-center — mandatory plate-solve + sync via <see cref="ICenteringService"/> (§28). Best-effort:
///         a failed centre logs + continues (matches NINA), it does not fail the flip.</item>
///   <item>Resume guiding — auto-select a new guide star, then start guiding (if a guider is connected).</item>
///   <item>Settle.</item>
///   <item>§58.5 side-of-pier verification — log a warning if the mount didn't report a pier-side change.</item>
/// </list>
///
/// Mount-gated to live-validate (it slews/flips the mount); the orchestration is unit-testable with mocked
/// mediators + a mocked centering service. §58.9's four-layer unattended-safety pipeline (pre-flight flight
/// check, in-slew watchdog, park-on-failure safe-rest state, looping alarm) is a separate follow-up tracked
/// in PORT_TODO — this is the core §58.4 recovery sequence.
/// </summary>
public sealed class MeridianFlipExecutor : IMeridianFlipExecutor {
    private readonly IProfileService profileService;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly IGuiderMediator guiderMediator;
    private readonly ICenteringService centeringService;
    private readonly IDomeMediator domeMediator;
    private readonly IDomeFollower domeFollower;

    public MeridianFlipExecutor(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            ICenteringService centeringService,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower) {
        this.profileService = profileService;
        this.telescopeMediator = telescopeMediator;
        this.guiderMediator = guiderMediator;
        this.centeringService = centeringService;
        this.domeMediator = domeMediator;
        this.domeFollower = domeFollower;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Flip-recovery boundary: any step (mount slew/flip, tracking toggle, plate-solve recenter, guider resume) can throw arbitrary driver/HTTP/IO exceptions. A flip MUST fail safe on ANY of them — restore tracking + resume the guider, then halt the sequence (return false) — never let an escape leave the OTA mid-flip or fault the engine. CA1031's fail-safe boundary applies.")]
    public async Task<bool> MeridianFlip(Coordinates targetCoordinates, TimeSpan timeToFlip,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(targetCoordinates);

        var settings = profileService.ActiveProfile.MeridianFlipSettings;
        // A guider is only relevant when one is actually connected; with no guider (the common camera-only
        // rig) the stop/select/resume steps are skipped entirely. ARA's guider is PHD2-backed, so there is no
        // NINA "DirectGuider" exclusion to make here.
        var guiderConnected = guiderMediator.GetInfo().Connected;

        Logger.Info(
            $"Meridian Flip - Initializing. Target RA: {targetCoordinates.RAString} Dec: {targetCoordinates.DecString} Epoch: {targetCoordinates.Epoch}. " +
            $"Remaining wait: {timeToFlip}. Recenter: {settings.Recenter}. AutoFocusAfterFlip: {settings.AutoFocusAfterFlip}. " +
            $"SettleTime: {settings.SettleTime}s. GuiderConnected: {guiderConnected}.");

        // Track what we actually touched so the catch blocks only undo real state changes: don't resume a
        // guider we never stopped, don't "restore" tracking we never disabled (either would log a misleading
        // state change).
        var guidingStopped = false;
        var trackingDisabled = false;

        try {
            if (guiderConnected) {
                await StopAutoguider(token);
                guidingStopped = true;
            }

            // The flag is set from inside PassMeridian, only after the SetTrackingEnabled(false) call returns —
            // so a throw from that call doesn't leave us "restoring" tracking that was never disabled.
            await PassMeridian(timeToFlip, () => trackingDisabled = true, progress, token);

            // Snapshot the pier side BEFORE the flip so §58.5 can verify it actually changed afterward — a
            // mount that reports the same (non-unknown) pier side after the slew likely didn't flip.
            var pierSideBeforeFlip = telescopeMediator.GetInfo().SideOfPier;

            await DoFlip(targetCoordinates, settings.SettleTime, progress, token);

            // §58.4 step 3 — re-focus is conditional. The live AF V-curve sweep is focuser-gated and unbuilt,
            // so honour the policy by logging the skip rather than aborting (re-focus failures must not abort
            // the night — §58.7).
            if (settings.AutoFocusAfterFlip) {
                Logger.Warning("Meridian Flip - AutoFocusAfterFlip is enabled but the live autofocus sweep is not implemented yet; skipping re-focus.");
            }

            if (settings.Recenter) {
                await Recenter(targetCoordinates, progress, token);
            }

            if (guiderConnected) {
                await SelectNewGuideStar(token);
                await ResumeAutoguider(progress, token);
            }

            // Final settle — distinct from the post-flip-slew settle inside DoFlip. That one lets the mount
            // mechanically settle before plate-solving; this one lets guiding re-converge before imaging
            // resumes. Both use the same SettleTime (matching NINA's two-settle DoMeridianFlip).
            await Settle(settings.SettleTime, progress, token);

            VerifySideOfPier(pierSideBeforeFlip);

            Logger.Info("Meridian Flip - Completed successfully.");
            return true;
        } catch (OperationCanceledException) {
            // Cancellation is not a flip failure — restore a safe tracking state, then let it propagate so the
            // sequence engine records CANCELLED (not FAILED). Only restore tracking if we actually disabled it
            // (cancellation can fire during StopAutoguider, before PassMeridian).
            //
            // Deliberately do NOT resume guiding here (unlike the error path below): a cancel is the user
            // stopping the session, so re-starting PHD2 would fight that intent — leaving the guider stopped is
            // the expected outcome of a Stop (matches NINA's OCE path, which resumes nothing). Tracking is still
            // restored because a mount sitting with tracking off is a worse rest state than one tracking sidereal.
            Logger.Info("Meridian Flip - Cancelled. Restoring tracking (leaving the guider stopped) before propagating cancellation.");
            if (trackingDisabled) {
                TryRestoreTracking();
            }
            throw;
        } catch (Exception ex) {
            // A failed flip halts the sequence (the trigger throws on our false return). Best-effort restore:
            // resume guiding + re-enable tracking so the mount keeps the target rather than drifting. §58.9's
            // park-on-failure safe-rest state is the enhanced unattended layer (follow-up, PORT_TODO).
            Logger.Error("Meridian Flip - Failed.", ex);
            // Only resume a guider we actually stopped: if StopAutoguider itself threw, guiding was never
            // stopped, so a resume would be spurious (and log a misleading "resume also failed").
            if (guidingStopped) {
                try {
                    await ResumeAutoguider(progress, CancellationToken.None);
                } catch (Exception resumeEx) {
                    Logger.Error("Meridian Flip - Resuming the guider after a flip error also failed.", resumeEx);
                }
            }
            if (trackingDisabled) {
                TryRestoreTracking();
            }
            return false;
        } finally {
            progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = string.Empty });
        }
    }

    private async Task StopAutoguider(CancellationToken token) {
        Logger.Info("Meridian Flip - Stopping the guider.");
        await guiderMediator.StopGuiding(token);
    }

    // Stop tracking, wait out the remaining time to the flip, then resume tracking — so the mount actually
    // crosses the meridian before the flip slew. timeToFlip is 0 when the meridian was already passed by
    // pause_after, in which case this returns immediately after toggling tracking. onTrackingDisabled fires
    // only after the disable call returns, so the caller's restore-tracking guard reflects the real state.
    private async Task PassMeridian(TimeSpan timeToFlip, Action onTrackingDisabled, IProgress<ApplicationStatus> progress, CancellationToken token) {
        Logger.Info("Meridian Flip - Stopping tracking to pass the meridian.");
        telescopeMediator.SetTrackingEnabled(false);
        onTrackingDisabled();

        var remaining = timeToFlip;
        while (remaining.TotalSeconds >= 1) {
            progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = $"Passing meridian — {remaining:hh\\:mm\\:ss}" });
            var delta = await CoreUtil.Delay(1000, token);
            remaining -= delta;
        }

        Logger.Info("Meridian Flip - Resuming tracking after passing the meridian.");
        telescopeMediator.SetTrackingEnabled(true);
    }

    private async Task DoFlip(Coordinates target, int settleSeconds, IProgress<ApplicationStatus> progress, CancellationToken token) {
        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Flipping scope" });
        Logger.Info($"Meridian Flip - Flipping to RA: {target.RAString} Dec: {target.DecString} Epoch: {target.Epoch}");
        var flipSuccess = await telescopeMediator.MeridianFlip(target, token);
        if (!flipSuccess) {
            // The mount itself reported the flip slew did not succeed — fail loudly; resuming imaging on a
            // possibly-still-on-the-wrong-side mount risks an OTA collision (§58.9 Layer 2 intent).
            throw new InvalidOperationException("Meridian flip aborted: the mount reported the flip slew did not succeed.");
        }

        // Post-flip-slew settle — let the mount mechanically settle before the recenter plate-solve. Distinct
        // from the final settle in the main body (which waits for guiding to re-converge); both use SettleTime.
        await Settle(settleSeconds, progress, token);

        await SynchronizeDome(progress, token);
    }

    // Best-effort dome sync after the flip slew (§58.4). No-ops when no dome is connected (the common case);
    // a sync failure logs a warning and continues rather than failing the flip.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort post-flip dome sync: a dome driver fault must degrade to a warning + continue (the imaging payload doesn't depend on the dome being perfectly aligned this instant), not abort the flip. Cancellation is rethrown. CA1031's log-and-recover boundary applies.")]
    private async Task SynchronizeDome(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var domeInfo = domeMediator.GetInfo();
        if (!domeInfo.Connected || !domeInfo.CanSetAzimuth) {
            return;
        }

        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Synchronizing dome" });
        try {
            if (domeFollower.IsFollowing) {
                Logger.Info("Meridian Flip - Waiting for the dome to synchronize to the scope.");
                await domeFollower.WaitForDomeSynchronization(token);
            } else {
                Logger.Info("Meridian Flip - Synchronizing the dome to the scope (following is disabled).");
                if (!await domeFollower.TriggerTelescopeSync()) {
                    Logger.Warning("Meridian Flip - Dome synchronization did not complete successfully. Moving on.");
                }
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Dome synchronization did not complete successfully. Moving on.", ex);
        }
    }

    // Mandatory plate-solve + sync to recover the exact framing after the flip (§28 centering). Best-effort:
    // a failed centre logs + continues (matches NINA) so a stubborn solve doesn't abort an otherwise-good flip;
    // §58.9 Layer 3's "no imaging resumes on solve failure" is the enhanced unattended gate (follow-up).
    private async Task Recenter(Coordinates target, IProgress<ApplicationStatus> progress, CancellationToken token) {
        Logger.Info("Meridian Flip - Re-centering after the flip.");
        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Re-centering" });
        var result = await centeringService.CenterOnTarget(target, null, progress, token);
        if (result == null || !result.Success) {
            Logger.Error("Meridian Flip - Re-center after the flip failed. Continuing without it.");
        }
    }

    private async Task SelectNewGuideStar(CancellationToken token) {
        Logger.Info("Meridian Flip - Selecting a new guide star.");
        await guiderMediator.AutoSelectGuideStar(token);
    }

    private async Task ResumeAutoguider(IProgress<ApplicationStatus> progress, CancellationToken token) {
        Logger.Info("Meridian Flip - Resuming the guider.");
        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Resuming guiding" });
        await guiderMediator.StartGuiding(false, progress, token);
    }

    private static async Task Settle(int settleSeconds, IProgress<ApplicationStatus> progress, CancellationToken token) {
        if (settleSeconds <= 0) {
            return;
        }
        Logger.Info($"Meridian Flip - Settling the scope for {settleSeconds}s.");
        var remaining = TimeSpan.FromSeconds(settleSeconds);
        while (remaining.TotalSeconds >= 1) {
            progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = $"Settling — {remaining:hh\\:mm\\:ss}" });
            var delta = await CoreUtil.Delay(1000, token);
            remaining -= delta;
        }
    }

    // §58.5 — verify the flip via side-of-pier by comparing the post-flip pier side against the snapshot taken
    // before the slew. An unchanged (but known) pier side means the mount likely didn't actually flip; an
    // unknown pier side can't be used to verify. Both cases warn but CONTINUE — some Alpaca drivers lie about
    // pier side (no brand-quirk database, per §52 Alpaca-only). The HARD fail-on-unchanged is §58.9 Layer 2
    // (deferred — see PORT_TODO).
    private void VerifySideOfPier(PierSide pierSideBeforeFlip) {
        var sideOfPier = telescopeMediator.GetInfo().SideOfPier;
        if (sideOfPier == PierSide.pierUnknown) {
            Logger.Warning("Meridian Flip - The mount reports an unknown pier side after the flip; cannot verify the flip via side-of-pier. Continuing.");
        } else if (sideOfPier == pierSideBeforeFlip) {
            Logger.Warning($"Meridian Flip - The mount reports the same pier side ({sideOfPier}) after the flip as before it; the flip may not have actually happened. Continuing (some drivers misreport pier side).");
        } else {
            Logger.Info($"Meridian Flip - Side-of-pier verified: {pierSideBeforeFlip} → {sideOfPier}.");
        }
    }

    // Re-enable tracking on a failure/cancellation so the mount keeps its target instead of drifting. Swallows
    // its own failure — this runs from a catch block and must not mask the original fault.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Cleanup boundary called from catch blocks: re-enabling tracking can itself throw a driver/HTTP exception, which must be logged and swallowed so it doesn't mask the original flip fault being handled. CA1031's log-and-recover boundary applies.")]
    private void TryRestoreTracking() {
        try {
            telescopeMediator.SetTrackingEnabled(true);
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Re-enabling tracking after a flip error also failed.", ex);
        }
    }
}
