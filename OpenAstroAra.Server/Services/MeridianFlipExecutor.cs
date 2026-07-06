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
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
// Both the server contracts and the equipment interfaces define a DeviceType; the reconnector
// speaks the server-contract one.
using DeviceType = OpenAstroAra.Server.Contracts.DeviceType;

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
    // §58.9 collaborators — nullable so the pre-§58.9 ctor (kept for the existing tests + any
    // direct construction) still works; DI always supplies them. With any of them absent the
    // corresponding safety behaviour degrades to the §58.4/§58.5 baseline.
    private readonly IProfileStore? profileStore;
    private readonly INotificationService? notifications;
    private readonly IEquipmentReconnector? reconnector;
    private readonly ICameraMediator? cameraMediator;
    private readonly IFocuserMediator? focuserMediator;

    // §58.9 timing knobs — spec values by default; internal so the unit tests can shrink them
    // (a 5 s watchdog sample would make every watchdog test take wall-clock seconds).
    internal TimeSpan WatchdogSampleInterval { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan WatchdogStallWindow { get; set; } = TimeSpan.FromSeconds(15);
    internal TimeSpan WatchdogHardCap { get; set; } = TimeSpan.FromMinutes(5);
    internal TimeSpan ReconnectPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan ReconnectWait { get; set; } = TimeSpan.FromSeconds(30);
    // Injectable clock for the Layer-1 altitude prediction (tests pin the instant).
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public MeridianFlipExecutor(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            ICenteringService centeringService,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower)
        : this(profileService, telescopeMediator, guiderMediator, centeringService, domeMediator,
            domeFollower, null, null, null, null, null) {
    }

    public MeridianFlipExecutor(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IGuiderMediator guiderMediator,
            ICenteringService centeringService,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            IProfileStore? profileStore,
            INotificationService? notifications,
            IEquipmentReconnector? reconnector,
            ICameraMediator? cameraMediator,
            IFocuserMediator? focuserMediator,
            OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor? autofocusExecutor = null) {
        this.profileService = profileService;
        this.telescopeMediator = telescopeMediator;
        this.guiderMediator = guiderMediator;
        this.centeringService = centeringService;
        this.domeMediator = domeMediator;
        this.domeFollower = domeFollower;
        this.profileStore = profileStore;
        this.notifications = notifications;
        this.reconnector = reconnector;
        this.cameraMediator = cameraMediator;
        this.focuserMediator = focuserMediator;
        this.autofocusExecutor = autofocusExecutor;
    }

    private readonly OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor? autofocusExecutor;

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

        // §58.9 Layer 1 — the pre-flip flight check runs BEFORE any state is touched: on a failed
        // check the flip never starts, the mount stays in its known-safe pre-flip state (tracking
        // sidereal, guider running) and a critical notification fires. Only active when the safety
        // config is available (server DI) and enabled in the profile.
        SafetyPoliciesDto? safety = null;
        string? failReason = null;
        try {
            safety = profileStore?.GetSafetyPolicies();
            if (safety is { FlipSafetyEnabled: true }) {
                failReason = await PreFlipFlightCheck(targetCoordinates, timeToFlip, settings, safety, token);
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            // The flight check itself must sit inside the same fail-safe boundary as everything
            // else: a throwing equipment/profile query is a FAILED check (flip never starts,
            // notification fires), never an unnotified escape.
            Logger.Error("Meridian Flip - The pre-flip flight check itself threw.", ex);
            failReason = $"the flight check itself failed ({ex.Message}).";
        }
        if (failReason is not null) {
            Logger.Error($"Meridian Flip - Pre-flip flight check failed: {failReason}. The flip will not start; the mount stays in its pre-flip state.");
            await NotifyCritical("Meridian flip aborted before it started",
                $"Pre-flip flight check failed: {failReason} The mount was left tracking in its pre-flip state and the sequence has been halted.");
            return false;
        }

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

            await DoFlip(targetCoordinates, settings.SettleTime, safety, progress, token);

            // §58.4 step 3 — re-focus is conditional, and per §58.7 a re-focus FAILURE must not abort
            // the night: the flip proceeds (recenter → guiding resume) with the focuser restored to
            // its pre-sweep position by the sweep's own failure policy, and the skip/failure logged.
            if (settings.AutoFocusAfterFlip) {
                if (autofocusExecutor is null) {
                    Logger.Warning("Meridian Flip - AutoFocusAfterFlip is enabled but no autofocus executor is wired; skipping re-focus.");
                } else {
                    var focused = await autofocusExecutor.RunAutofocusAsync(progress, token);
                    if (!focused) {
                        Logger.Warning("Meridian Flip - post-flip autofocus failed; continuing the flip recovery (per §58.7 re-focus failures do not abort the night).");
                        await Notify(NotificationSeverity.Warning, "Post-flip autofocus failed",
                            "The re-focus after the meridian flip failed (the focuser was restored to its "
                            + "pre-sweep position); imaging continues and focus may have drifted.");
                    }
                }
            }

            if (settings.Recenter) {
                await Recenter(targetCoordinates, safety, progress, token);
            }

            if (guiderConnected) {
                await SelectNewGuideStar(token);
                await ResumeAutoguider(progress, token);
            }

            // Final settle — distinct from the post-flip-slew settle inside DoFlip. That one lets the mount
            // mechanically settle before plate-solving; this one lets guiding re-converge before imaging
            // resumes. Both use the same SettleTime (matching NINA's two-settle DoMeridianFlip).
            await Settle(settings.SettleTime, progress, token);

            VerifySideOfPier(pierSideBeforeFlip, safety);

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
            // A failed flip halts the sequence (the trigger throws on our false return).
            Logger.Error("Meridian Flip - Failed.", ex);
            if (safety is { FlipSafetyEnabled: true }) {
                // §58.9 Layer 4 — safe rest: the guider stays STOPPED (PHD2 corrections on a
                // mis-aimed scope drift it further) and the mount parks (or stops tracking when it
                // can't park). The cooler keeps running and the captured frames are preserved —
                // the user may resume if conditions allow.
                var rest = await SafeRest(progress);
                await NotifyCritical("Meridian flip failed",
                    $"{ex.Message} §58.9 safe rest: {rest} The guider is stopped, the cooler keeps running, and the sequence has been halted.");
            } else {
                // Baseline (§58.4) best-effort restore: resume guiding + re-enable tracking so the
                // mount keeps the target rather than drifting. Only resume a guider we actually
                // stopped: if StopAutoguider itself threw, guiding was never stopped, so a resume
                // would be spurious (and log a misleading "resume also failed").
                var guiderResumed = !guidingStopped;
                if (guidingStopped) {
                    try {
                        await ResumeAutoguider(progress, CancellationToken.None);
                        guiderResumed = true;
                    } catch (Exception resumeEx) {
                        Logger.Error("Meridian Flip - Resuming the guider after a flip error also failed.", resumeEx);
                    }
                }
                if (trackingDisabled) {
                    TryRestoreTracking();
                }
                // §58.7 — the failure must reach the user in BOTH modes, not only under the
                // §58.9 safety layers: an unattended sequence just halted. Fold the guider
                // outcome in — "restored but unguided" and "restored" are different mornings —
                // and only claim a tracking restore that actually ran (a failure before
                // PassMeridian never touched tracking, so "restored" would overstate it).
                var trackingClause = trackingDisabled
                    ? "Tracking was restored best-effort"
                    : "Tracking was never disabled (the failure occurred before the meridian wait)";
                await NotifyCritical("Meridian flip failed",
                    $"{ex.Message} {trackingClause} and the sequence has been halted."
                    + (guiderResumed ? string.Empty : " The guider could NOT be resumed — the mount is unguided."));
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

    private async Task DoFlip(Coordinates target, int settleSeconds, SafetyPoliciesDto? safety,
            IProgress<ApplicationStatus> progress, CancellationToken token) {
        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Flipping scope" });
        Logger.Info($"Meridian Flip - Flipping to RA: {target.RAString} Dec: {target.DecString} Epoch: {target.Epoch}");
        // §58.9 Layer 2 — with the safety layers on, the flip slew runs under the watchdog (stall
        // detection + hard timeout + mid-slew fault detection); otherwise the plain awaited call.
        var flipSuccess = safety is { FlipSafetyEnabled: true }
            ? await FlipWithWatchdog(target, safety.ExpectedFlipSlewSeconds, token)
            : await telescopeMediator.MeridianFlip(target, token);
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
                if (!await domeFollower.TriggerTelescopeSync(token)) {
                    Logger.Warning("Meridian Flip - Dome synchronization did not complete successfully. Moving on.");
                }
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Dome synchronization did not complete successfully. Moving on.", ex);
        }
    }

    // §58.9 Layer 3 — the retry count and post-solve sanity bound ("solved position must be within
    // ± 2° of the intended target"; both per-spec defaults, a profile knob only if someone asks).
    private const int RecenterAttemptsWithSafety = 3;
    private const double RecenterSanityBoundDeg = 2.0;

    // Post-flip re-center via mandatory plate-solve + sync (§28 centering).
    //
    // With §58.9 safety OFF (or no server collaborators): best-effort — a failed centre logs +
    // continues (matches NINA), a stubborn solve doesn't abort an otherwise-good flip.
    //
    // With safety ON this is the Layer-3 verification gate: up to 3 attempts, and the solved
    // position must land within ± 2° of the intended target. All attempts failed (or a solve that
    // says "you're 30° off") → the flip FAILS and imaging does not resume — better to lose the
    // rest of the night than to image with a misaimed scope. NB the gate hardens the existing
    // recenter step, so it engages only when Recenter is enabled (the default): with Recenter off
    // there is no solve to verify — the §58.9 "plate-solve mandatory" presumes recenter-on.
    private async Task Recenter(Coordinates target, SafetyPoliciesDto? safety, IProgress<ApplicationStatus> progress, CancellationToken token) {
        Logger.Info("Meridian Flip - Re-centering after the flip.");
        progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Re-centering" });

        if (safety is not { FlipSafetyEnabled: true }) {
            var result = await centeringService.CenterOnTarget(target, null, progress, token);
            if (result == null || !result.Success) {
                // LOG at Warning (not Logger.Error): without the safety layers re-centering stays
                // best-effort and does not halt the flip (same log severity as the best-effort
                // dome sync). The §58.7 NOTIFICATION is deliberately a notch higher (Error): the
                // night continues on an unverified pointing, which the user should hear about
                // before the morning's subs turn out framed on nothing.
                Logger.Warning("Meridian Flip - Re-center after the flip failed. Continuing without it.");
                await Notify(NotificationSeverity.Error, "Post-flip re-center failed",
                    "The plate-solve re-center after the meridian flip failed; imaging continues on an "
                    + "unverified pointing. Enable the §58.9 flip-safety layers to make this a hard gate.");
            }
            return;
        }

        for (var attempt = 1; attempt <= RecenterAttemptsWithSafety; attempt++) {
            token.ThrowIfCancellationRequested();
            var result = await centeringService.CenterOnTarget(target, null, progress, token);
            if (result is { Success: true }) {
                // The sanity bound needs the solved position; a solver that reports success without
                // coordinates can't prove where the scope points, so it does NOT pass the gate.
                if (result.Coordinates is { } solved) {
                    var offsetDeg = (target - solved).Distance.Degree;
                    if (offsetDeg <= RecenterSanityBoundDeg) {
                        Logger.Info($"Meridian Flip - Layer-3 verification passed on attempt {attempt}: solved {offsetDeg:0.##}° from the target (bound {RecenterSanityBoundDeg}°).");
                        return;
                    }
                    Logger.Error($"Meridian Flip - Layer-3 verification: the solve says the scope is {offsetDeg:0.#}° from the intended target (bound {RecenterSanityBoundDeg}°) — not trusting it.");
                } else {
                    Logger.Error("Meridian Flip - Layer-3 verification: the centering reported success but carried no solved coordinates; cannot verify the pointing.");
                }
            } else {
                Logger.Warning($"Meridian Flip - Layer-3 re-center attempt {attempt}/{RecenterAttemptsWithSafety} failed.");
            }
        }
        throw new InvalidOperationException(
            $"post-flip verification failed: the plate-solve re-center did not confirm the pointing within {RecenterSanityBoundDeg}° after {RecenterAttemptsWithSafety} attempts. Imaging will not resume on an unverified pointing.");
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
    // before the slew. An unknown pier side (before or after) can't verify anything and always just warns.
    // An unchanged KNOWN pier side means the mount likely didn't actually flip: with the §58.9 safety layers
    // ON this is the Layer-2 HARD fail (never resume imaging on a possibly-still-on-the-wrong-side mount);
    // with them off it stays the §58.5 warn-and-continue (some Alpaca drivers lie about pier side — the
    // toggle exists exactly for those rigs).
    private void VerifySideOfPier(PierSide pierSideBeforeFlip, SafetyPoliciesDto? safety) {
        var sideOfPier = telescopeMediator.GetInfo().SideOfPier;
        if (sideOfPier == PierSide.pierUnknown || pierSideBeforeFlip == PierSide.pierUnknown) {
            Logger.Warning("Meridian Flip - The mount reports an unknown pier side; cannot verify the flip via side-of-pier. Continuing.");
        } else if (sideOfPier == pierSideBeforeFlip) {
            if (safety is { FlipSafetyEnabled: true }) {
                throw new InvalidOperationException(
                    $"The mount reports the same pier side ({sideOfPier}) after the flip as before it — the flip likely did not happen. " +
                    "Imaging will not resume on a possibly-unflipped mount (disable flip safety if this mount misreports pier side).");
            }
            Logger.Warning($"Meridian Flip - The mount reports the same pier side ({sideOfPier}) after the flip as before it; the flip may not have actually happened. Continuing (some drivers misreport pier side).");
        } else {
            Logger.Info($"Meridian Flip - Side-of-pier verified: {pierSideBeforeFlip} → {sideOfPier}.");
        }
    }

    // ─── §58.9 Layer 1 — pre-flip flight check ─────────────────────────────────────────────────

    /// <summary>The Layer-1 flight check. Returns null when every gate passes, or a human-readable
    /// reason when the flip must not start. Checks, per the §58.9 spec: the predicted post-flip
    /// target altitude clears the site horizon floor; the mount is healthy (connected, not parked,
    /// tracking, not already slewing); and the required equipment is connected (camera always;
    /// guider when re-cal after flip is configured; focuser when AutoFocusAfterFlip is set) — with
    /// one §42.3 hot-reconnect attempt before giving up on a disconnected device. The spec's
    /// "predicted slew duration sane" gate has no Alpaca API to read an estimate from, so the
    /// profile's ExpectedFlipSlewSeconds stands in for it in the Layer-2 watchdog instead.</summary>
    private async Task<string?> PreFlipFlightCheck(Coordinates target, TimeSpan timeToFlip,
            IMeridianFlipSettings settings, SafetyPoliciesDto safety, CancellationToken token) {
        Logger.Info("Meridian Flip - Running the §58.9 pre-flip flight check.");

        // Endpoint prediction: the flip re-points at the same target from the other pier side, so
        // the question is "is the target still above the hard floor AT THE FLIP" — predicted at
        // now + timeToFlip, since PassMeridian can wait out tens of minutes and a target near the
        // floor keeps sinking through the wait. The site horizon altitude is the profile's floor
        // (§35/§36 semantics).
        var site = profileStore!.GetSiteSettings();
        var predictedAlt = PredictedTargetAltitudeDeg(target, site.LatitudeDeg, site.LongitudeDeg, UtcNow() + timeToFlip);
        if (predictedAlt < site.DefaultHorizonAltitudeDeg) {
            return $"the predicted post-flip target altitude ({predictedAlt:0.#}°) is below the site's horizon floor ({site.DefaultHorizonAltitudeDeg:0.#}°) — the target should be skipped, not flipped to.";
        }

        var mount = telescopeMediator.GetInfo();
        if (!mount.Connected) {
            return "the mount is not connected.";
        }
        if (mount.AtPark) {
            return "the mount reports it is parked.";
        }
        if (mount.Slewing) {
            return "the mount reports it is already slewing.";
        }
        if (!mount.TrackingEnabled) {
            return "the mount is not tracking — a flip from an untracked state would slew to a stale position.";
        }

        // Required equipment, each with one §42.3 hot-reconnect attempt. Camera is always required
        // (imaging resumes after the flip); guider/focuser only when the flip flow will use them.
        var recalGuider = safety.MeridianRecalGuider;
        if (await EnsureConnected(DeviceType.Camera, () => cameraMediator?.GetInfo().Connected ?? false, token) is { } cameraFail) {
            return cameraFail;
        }
        if (recalGuider && await EnsureConnected(DeviceType.Guider, () => guiderMediator.GetInfo().Connected, token) is { } guiderFail) {
            return guiderFail;
        }
        if (settings.AutoFocusAfterFlip && await EnsureConnected(DeviceType.Focuser, () => focuserMediator?.GetInfo().Connected ?? false, token) is { } focuserFail) {
            return focuserFail;
        }

        Logger.Info($"Meridian Flip - Pre-flip flight check passed (predicted target altitude {predictedAlt:0.#}°).");
        return null;
    }

    /// <summary>Connected-or-reconnect gate for one required device: already connected → ok;
    /// otherwise dispatch a §42.3 hot-reconnect and poll until <see cref="ReconnectWait"/> runs
    /// out. Returns null when the device is (or comes back) connected, else the failure reason.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The reconnect dispatch is best-effort inside a pre-flight gate: a reconnector fault must yield a clean 'still disconnected' verdict (flip aborts safely), not an unhandled escape. CA1031's log-and-recover boundary applies.")]
    private async Task<string?> EnsureConnected(DeviceType type, Func<bool> isConnected, CancellationToken token) {
        if (isConnected()) {
            return null;
        }
        Logger.Warning($"Meridian Flip - Required device {type} is disconnected; attempting a §42.3 hot-reconnect before the flip.");
        try {
            var outcome = reconnector is null
                ? default
                : await reconnector.ReconnectAsync(type, token);
            if (outcome.Dispatched > 0) {
                var waited = TimeSpan.Zero;
                while (waited < ReconnectWait) {
                    await Task.Delay(ReconnectPollInterval, token);
                    waited += ReconnectPollInterval;
                    if (isConnected()) {
                        Logger.Info($"Meridian Flip - {type} reconnected after {waited.TotalSeconds:0}s.");
                        return null;
                    }
                }
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Logger.Error($"Meridian Flip - Hot-reconnect attempt for {type} failed.", ex);
        }
        return $"required device {type} is disconnected and could not be reconnected.";
    }

    /// <summary>The target's altitude (degrees) at <paramref name="atUtc"/> from the site — the
    /// Layer-1 endpoint prediction. Same spherical-trig path Tonight's Sky uses (one sky model).</summary>
    internal static double PredictedTargetAltitudeDeg(Coordinates target, double latDeg, double lonDeg, DateTimeOffset atUtc) {
        var lst = TonightSkyService.LocalSiderealTimeDeg(atUtc, lonDeg);
        var hourAngle = (lst - target.RADegrees % 360.0 + 360.0) % 360.0;
        return TonightSkyService.AltitudeFromHourAngleDeg(target.Dec, latDeg, hourAngle);
    }

    // ─── §58.9 Layer 2 — in-slew watchdog ──────────────────────────────────────────────────────

    /// <summary>Runs the flip slew under the Layer-2 watchdog: samples mount state every
    /// <see cref="WatchdogSampleInterval"/> while the slew is in flight, and aborts the slew
    /// (StopSlew + cancel) on a position stall (&#8805; <see cref="WatchdogStallWindow"/> with no
    /// RA/Dec movement), a mid-slew fault (mount drops Connected), or the hard timeout
    /// (min(3 × expected, <see cref="WatchdogHardCap"/>)). The pier-side-changed assertion runs
    /// separately in <see cref="VerifySideOfPier"/> after the settle.</summary>
    private async Task<bool> FlipWithWatchdog(Coordinates target, int expectedSlewSeconds, CancellationToken token) {
        var expected = TimeSpan.FromSeconds(Math.Max(1, expectedSlewSeconds));
        var hardTimeout = TimeSpan.FromTicks(Math.Min(expected.Ticks * 3, WatchdogHardCap.Ticks));
        Logger.Info($"Meridian Flip - Watchdog armed: sample {WatchdogSampleInterval.TotalSeconds:0}s, stall window {WatchdogStallWindow.TotalSeconds:0}s, hard timeout {hardTimeout.TotalSeconds:0}s.");

        using var slewCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var flipTask = telescopeMediator.MeridianFlip(target, slewCts.Token);

        var elapsed = Stopwatch.StartNew();
        var lastMovementAt = TimeSpan.Zero;
        double lastRa = double.NaN, lastDec = double.NaN;

        while (!flipTask.IsCompleted) {
            var finished = await Task.WhenAny(flipTask, Task.Delay(WatchdogSampleInterval, token));
            if (finished == flipTask) {
                break;
            }
            token.ThrowIfCancellationRequested();

            var info = telescopeMediator.GetInfo();
            if (!info.Connected) {
                return await AbortWatchedSlew(flipTask, slewCts, "the mount dropped its connection mid-slew");
            }
            // Position progressing? Any RA/Dec change counts (the epsilon absorbs driver jitter in
            // the last decimals of an unmoving report).
            var moved = double.IsNaN(lastRa)
                || Math.Abs(info.RightAscension - lastRa) > 1e-4
                || Math.Abs(info.Declination - lastDec) > 1e-4;
            if (moved) {
                lastRa = info.RightAscension;
                lastDec = info.Declination;
                lastMovementAt = elapsed.Elapsed;
            } else if (elapsed.Elapsed - lastMovementAt >= WatchdogStallWindow) {
                return await AbortWatchedSlew(flipTask, slewCts,
                    $"the mount position has not changed for {WatchdogStallWindow.TotalSeconds:0}s (stalled slew)");
            }
            if (elapsed.Elapsed >= hardTimeout) {
                return await AbortWatchedSlew(flipTask, slewCts,
                    $"the flip slew exceeded its hard timeout of {hardTimeout.TotalSeconds:0}s");
            }
        }

        return await flipTask;
    }

    /// <summary>Watchdog abort path: stop the slew, cancel the flip call, observe its outcome so
    /// nothing goes unobserved, then throw the watchdog's reason (the flip failure).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Draining the cancelled flip task: it may end with OCE or any driver exception — all superseded by the watchdog's own diagnosis, which is thrown instead. CA1031's fail-safe boundary applies.")]
    private async Task<bool> AbortWatchedSlew(Task<bool> flipTask, CancellationTokenSource slewCts, string reason) {
        Logger.Error($"Meridian Flip - Watchdog abort: {reason}. Issuing StopSlew.");
        try {
            telescopeMediator.StopSlew();
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - StopSlew during the watchdog abort also failed.", ex);
        }
        await slewCts.CancelAsync();
        try {
            await flipTask;
        } catch (Exception) {
            // Expected: the cancelled/aborted flip call unwinds however the driver ends it. The
            // watchdog's diagnosis below is the truth worth reporting.
        }
        throw new InvalidOperationException($"Meridian flip aborted by the §58.9 watchdog: {reason}.");
    }

    // §58.9 Layer 4 — bound the park wait so a wedged park can't hang the failure path forever.
    private const int ParkTimeoutSeconds = 90;

    /// <summary>§58.9 Layer 4 — put the mount in the safest reachable state after a flip failure:
    /// park when <c>CanPark</c> (the safest possible position, bounded by
    /// <see cref="ParkTimeoutSeconds"/>), else stop tracking where the abort caught it. Every step
    /// is best-effort — this runs from the failure path and must never throw. Returns a one-line
    /// description of what was actually done, for the notification.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The safe-rest path runs from the failure handler: a park/tracking-stop fault must degrade to the next-safest action and a truthful message, never mask the original flip fault. CA1031's fail-safe boundary applies.")]
    private async Task<string> SafeRest(IProgress<ApplicationStatus> progress) {
        // Stop the guider FIRST, unconditionally — the failure may have landed after the resume
        // step (the pier-side hard fail does exactly that), and PHD2 corrections on a mount that
        // may not have actually flipped drift it further. Spec Layer 4 lists "guider stopped" as
        // part of the rest state, so this must hold for every path into it.
        try {
            if (guiderMediator.GetInfo().Connected) {
                Logger.Info("Meridian Flip - Safe rest: stopping the guider.");
                await guiderMediator.StopGuiding(CancellationToken.None);
            }
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Safe rest: stopping the guider failed.", ex);
        }

        TelescopeInfo mount;
        try {
            mount = telescopeMediator.GetInfo();
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Safe rest: reading the mount state failed; no park/tracking action taken.", ex);
            return "the mount state could not be read — no park or tracking change was possible.";
        }

        // Distinguish "this mount has no park capability" from "the park ATTEMPT failed" — the
        // returned line lands in the Critical notification an operator reads to diagnose an
        // unattended incident, so it must say which one actually happened.
        var parkAttemptFailed = false;
        if (mount.CanPark && !mount.AtPark) {
            try {
                Logger.Info("Meridian Flip - Safe rest: parking the mount.");
                progress.Report(new ApplicationStatus { Source = "MeridianFlip", Status = "Parking (safe rest)" });
                using var parkCts = new CancellationTokenSource(TimeSpan.FromSeconds(ParkTimeoutSeconds));
                if (await telescopeMediator.ParkTelescope(progress, parkCts.Token)) {
                    return "the mount was parked.";
                }
                parkAttemptFailed = true;
                Logger.Error("Meridian Flip - Safe rest: the park command reported failure; stopping tracking instead.");
            } catch (Exception ex) {
                parkAttemptFailed = true;
                Logger.Error("Meridian Flip - Safe rest: parking failed; stopping tracking instead.", ex);
            }
        }

        try {
            telescopeMediator.SetTrackingEnabled(false);
            if (mount.AtPark) {
                return "the mount was already parked.";
            }
            return parkAttemptFailed
                ? "the park attempt failed — tracking was stopped instead; check the mount."
                : "the mount cannot park — tracking was stopped where the abort caught it.";
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Safe rest: stopping tracking also failed.", ex);
            return "parking and stopping tracking both failed — check the mount.";
        }
    }

    /// <summary>Best-effort critical notification (§58.9 → §35.5 alarm on connected WILMA
    /// devices). Swallows its own failure — alerting must never mask the flip fault itself.</summary>
    private Task NotifyCritical(string title, string message) =>
        Notify(NotificationSeverity.Critical, title, message);

    /// <summary>§58.7 — best-effort flip notification at any severity (Critical = the §35.5
    /// alarm-worthy failures; Error = the "continuing degraded" paths an unattended user must
    /// hear about come morning). Swallows its own failure — alerting must never mask the flip
    /// fault itself.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Notification publish is best-effort from failure paths: a notification-store fault must not mask the flip failure being reported. CA1031's log-and-recover boundary applies.")]
    private async Task Notify(NotificationSeverity severity, string title, string message) {
        if (notifications is null) {
            return;
        }
        try {
            await notifications.CreateAsync(new NotificationDto(
                Id: Guid.NewGuid(),
                PostedUtc: UtcNow(),
                Severity: severity,
                Category: NotificationCategory.Safety,
                Title: title,
                Message: message,
                Read: false,
                Dismissed: false,
                DismissedUtc: null,
                Payload: null,
                RelatedEntityType: "meridian_flip",
                RelatedEntityId: null), CancellationToken.None);
        } catch (Exception ex) {
            Logger.Error("Meridian Flip - Publishing the flip notification failed.", ex);
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
