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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §45 — the polar-alignment routine (the engine slice, replacing the lifecycle-only skeleton).
    /// ARA owns the whole routine (guider POLAR_ALIGNMENT_DESIGN.md §4): the guider is a capture +
    /// lease source (<c>capture_single_frame</c> under the PA-session camera lease), ARA runs the
    /// plate solver (<see cref="IPolarAlignFrameSolver"/>, guide optics), the mount slews
    /// (<see cref="ITelescopeMediator"/>), and the <see cref="PolarAlignGeometry"/> math.
    ///
    /// <para><b>Stages</b> (<see cref="PolarAlignStages"/>, the partial in PolarAlignService.Stages.cs):
    /// <c>coarse</c> (the seed-and-adjust routine below on the main camera — slower frames, for the big moves),
    /// <c>fine</c> (the same routine on the guide camera), <c>check</c> (three guide-camera solves with two RA turns, an
    /// exact three-point fit, state <c>checked</c>). The user's Next moves from one to the next.</para>
    ///
    /// <para><b>Start</b> (#2048, NINA's Three Point Polar Alignment): the routine starts from where the user pointed the
    /// mount (tube near the pole, counterweights down). It never sends the mount Home, never slews it and never unparks
    /// it: a parked mount is refused, and the mount moves only after the user confirmed the start and the RA turn
    /// direction (<see cref="GetStartPlanAsync"/>). Every RA turn of the run stays inside the band the user confirmed:
    /// from the start pose up to <see cref="PolarAlignStartPlanDto.MaxSweepDeg"/> away from the meridian.</para>
    ///
    /// <para><b>Fine-stage state machine</b> (the <c>state</c> string in <see cref="PolarAlignStateDto"/>):
    /// <c>idle</c> → Start → <c>seeding</c> (frame A → RA-motor turn Δ away from the meridian → frame B →
    /// another turn → frame C → three-point axis fit) → <c>adjusting</c> (tracking stopped; continuous capture→solve, each
    /// solve's alt/az delta from the seed pointing is the user's knob adjustment seen from the camera — §45.8
    /// reverse-projection, no re-slewing) → Stop → <c>stopped</c>. Five consecutive failed solves
    /// park the loop in <c>paused</c> (it keeps retrying at a slower cadence and resumes on the next
    /// good solve); a fatal error (seed solve failure, mount fault) lands in <c>failed</c>.
    /// Every exit path stops the mount and confirms it stopped (PolarAlignService.MountStop.cs, #2048), then turns tracking
    /// off — or, with <c>stop_tracking_when_done</c> off and a run that finished, gives back the tracking it found.</para>
    /// </summary>
    public sealed partial class PolarAlignService : IPolarAlignService, IDisposable {

        private readonly IGuideCapture _guider;
        private readonly ILogger<PolarAlignService> _logger;
        private readonly IWsBroadcaster? _ws;
        private readonly IPolarAlignFrameSolver _solver;
        private readonly ITelescopeMediator _mount;
        private readonly IProfileStore _profileStore;
        private readonly object _gate = new();

        // Serializes the Start/Stop lifecycle (including the lease RPCs) — see the skeleton-slice
        // rationale: a retried Start racing a Stop must not interleave set_pa_session calls.
        private readonly SemaphoreSlim _opLock = new(1, 1);

        // §45.12 defaults (profile-configurable settings are the next slice — these are the playbook
        // values). The PA-session lease (timeout, renew interval) belongs to GuideCameraCustody.
        internal const int MaxConsecutiveSolveFailures = 5;
        private const int SeedSolveAttempts = 3;

        // Instance (not const) so tests — InternalsVisibleTo — shrink the cadences: the real values
        // would make a paused-then-resume loop test take tens of seconds of wall clock. The §45.12
        // user-facing knobs (exposure, binning, seed Δ, cadence, settle) come from the profile's
        // PolarAlignSettingsDto, read once at Start.
        /// <summary>How long a start waits for a looping Guide Camera to stop (#1749).</summary>
        internal TimeSpan LoopStopTimeout { get; set; } = TimeSpan.FromSeconds(10);
        internal TimeSpan PausedRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan CaptureCompleteTimeout { get; set; } = TimeSpan.FromSeconds(31);
        internal TimeSpan RunUnwindGrace { get; set; } = TimeSpan.FromSeconds(15);
        internal TimeSpan CaptureRequestPoll { get; set; } = TimeSpan.FromMilliseconds(200);
        internal TimeSpan TrackingConfirmTimeout { get; set; } = TimeSpan.FromSeconds(10);
        internal TimeSpan TrackingConfirmPoll { get; set; } = TimeSpan.FromMilliseconds(250);

        private bool _active;
        private string _state = "idle";
        private int _framesCaptured;
        private string? _lastFrameId;
        private double? _altErrorArcmin;
        private double? _azErrorArcmin;
        // #1565: in memory only, so a daemon restart (the mount may have moved) forgets it. Guarded by _gate.
        private PolarAlignLastResultDto? _lastResult;
        private int _liveIterations;
        // Every live frame of the run, failed ones included — the frame-id counter across the stages.
        private int _liveFrames;
        private DateTimeOffset _startedAt;
        private bool _captureRequested;
        private bool _waitingForCapture;
        private PolarAlignLastFrameDto? _lastFrame;

        // Stages (PolarAlignStages): which one runs, and the hand-offs between them. _advanceRequested
        // is the user's Next; _fineFinalAlt/AzArcmin = the fine stage's last readout, compared with the check's result.
        private string? _stage;
        private bool _advanceRequested;
        private int _checkSolvesDone;
        private PolarAlignCheckResultDto? _checkResult;
        // The fine stage's last readout, both components; the check derives its total and the distance to its own axis.
        private double? _fineFinalAltArcmin;
        private double? _fineFinalAzArcmin;
        // Written by the run task only: whether this run changed tracking (then the exit restores it).
        private bool _trackingTouched;
        // The mount's tracking-write count right after this run's own last write; the exit hands tracking
        // back only while the count still equals it (no other caller wrote since, even the same value).
        private long _ownTrackingWrites;
        private readonly IPolarAlignMainCameraSolver? _mainCamera;

        // §45.4 — the guide camera's frame size as the daemon reported it at this run's start; null
        // when it could not (auto binning then stays at 1×1). Written by the run task only.
        private (int Width, int Height)? _runFrameSize;

        // Run-generation token: Stop abandons a run task that won't unwind within the grace period
        // (a hung guider RPC), and a new Start may then launch a fresh run while the zombie is still
        // draining. Every state write / WS publish from a run checks its generation first, so a
        // superseded run's late effects are no-ops (its unique work dir keeps the files apart too).
        private int _generation;

        /// <summary>True while the routine runs, and while a run's exit still stops the mount (#2048): a failed run is
        /// <c>failed</c> at once but sends its stop commands and its tracking write for up to 8 s more. A sequence start
        /// or Emergency Stop then ends it and waits for that exit, so a resent AbortSlew or the tracking-off write never
        /// lands on the slew or the tracking that comes after it.</summary>
        public bool IsActive {
            get {
                lock (_gate) {
                    return _active || _exitsInFlight > 0;
                }
            }
        }

        // Run exits stopping the mount right now (ExitMountOnceAsync; a failure's exit counts from the moment it clears
        // _active, in FailRoutineAsync). Guarded by _gate.
        private int _exitsInFlight;

        private bool IsCurrent(int gen) {
            lock (_gate) {
                return _generation == gen;
            }
        }

        // Start-sequence counter, bumped ONLY by StartAsync (unlike _generation, which Stop also
        // bumps). The exit's fence (#2048): a run stops the mount and sets tracking unless a NEWER RUN
        // has started — a clean Stop still gets its exit (its generation is already stale by design),
        // but a zombie unblocking after a successor Start must not cut the successor's moves short or
        // stomp its tracking (§45.8 depends on tracking staying off mid-adjust).
        private int _startSeq;

        private bool NoNewerRunStarted(int startSeq) {
            lock (_gate) {
                return _startSeq == startSeq;
            }
        }

        private CancellationTokenSource? _runCts;
        private Task? _runTask;

        private readonly IPolarAlignmentLog? _log;
        // This routine's hold on the Guide Camera (ADR 0016); null when it holds none. Guarded by _gate.
        private GuideCameraCustody.Claim? _claim;

        internal PolarAlignService(
                IGuideCapture guider,
                ILogger<PolarAlignService> logger,
                IPolarAlignFrameSolver solver,
                ITelescopeMediator mount,
                IProfileStore profileStore,
                IWsBroadcaster? ws = null,
                IPolarAlignmentLog? log = null,
                IPolarAlignFrameFetcher? frameFetcher = null,
                IPolarAlignMainCameraSolver? mainCamera = null,
                IPolarAlignerSource? aligner = null) {
            _guider = guider;
            _alignerSource = aligner;
            _mainCamera = mainCamera;
            _logger = logger;
            _solver = solver;
            _mount = mount;
            _profileStore = profileStore;
            _ws = ws;
            _log = log;
            _ownsFetcher = frameFetcher is null; // the DI-provided fetcher is container-owned
            _frameFetcher = frameFetcher ?? new HttpPolarAlignFrameFetcher();
        }

        public Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct) {
            lock (_gate) {
                var total = _altErrorArcmin is double alt && _azErrorArcmin is double az
                    ? (double?)Math.Sqrt(alt * alt + az * az)
                    : null;
                return Task.FromResult(new PolarAlignStateDto(
                    State: _state,
                    CurrentErrorArcmin: total,
                    AzimuthAdjustmentArcmin: _azErrorArcmin,
                    AltitudeAdjustmentArcmin: _altErrorArcmin,
                    FramesCaptured: _framesCaptured,
                    LastFrameId: _lastFrameId,
                    WaitingForCapture: _waitingForCapture,
                    LastFrame: _lastFrame,
                    Stage: _stage,
                    WaitingForHome: false,
                    CheckSolvesDone: _checkSolvesDone,
                    CheckResult: _checkResult,
                    LastResult: _lastResult,
                    Correction: _aligner,
                    // #2072 — the caution describes a live readout: shown only while one runs.
                    ReadoutCaution: _state is "adjusting" or "paused" ? _readoutCaution : null));
            }
        }

        // Caller holds _gate. Records the run's final error when it has one (a stop before the first solve has none).
        // A Stop in the rough stage records nothing: its readout is not an alignment; only a Done there (which
        // CompleteAsync lets through inside the tolerance) is.
        private void RecordLastResultLocked(bool complete) {
            if ((complete || _stage != PolarAlignStages.Coarse) && _altErrorArcmin is double alt && _azErrorArcmin is double az) {
                _lastResult = new PolarAlignLastResultDto(Math.Sqrt(alt * alt + az * az), DateTimeOffset.UtcNow);
            }
        }

        /// <summary>Proxies the guider daemon's live-frame JPEG so a client on any network sees the
        /// frames polar align is solving (the daemon may sit on an address only this host reaches).
        /// Null when no guider is connected or the fetch fails — a missing preview is never an error.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The preview is best-effort; any fetch failure just means no image this time.")]
        public async Task<byte[]?> GetLiveFrameJpegAsync(CancellationToken ct) {
            IGuideCaptureLink guiderClient;
            try {
                guiderClient = _guider.ConnectedLink();
            } catch (InvalidOperationException ex) {
                LogLiveFrameUnavailable(ex.Message);
                return null;
            }
            var host = guiderClient.Host;
            if (string.IsNullOrEmpty(host)) {
                LogLiveFrameUnavailable("guider connection endpoint unknown");
                return null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try {
                return await _frameFetcher.FetchLiveFrameJpegAsync(host, guiderClient.RpcPort, timeout.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
                // The guider has no frame yet (the coarse stage shoots the main camera; a run that has
                // not captured): the normal answer, not a fault worth a stack trace.
                LogLiveFrameUnavailable("the guider has no frame to show yet");
                return null;
            } catch (Exception ex) {
                LogLiveFrameFetchFailed(ex);
                return null;
            }
        }

        /// <summary>Single-frame mode: release the parked adjust loop for one frame. Accepted while
        /// adjusting or paused (a no-op in loop mode, which re-shoots on its own).</summary>
        public Task<OperationAcceptedDto> RequestCaptureAsync(string? idempotencyKey, CancellationToken ct) {
            lock (_gate) {
                if (!_active || _state is not ("adjusting" or "paused")) {
                    throw new InvalidOperationException("polar alignment is not in the adjust phase");
                }
                _captureRequested = true;
            }
            return Task.FromResult(Accepted("polar-align.capture", idempotencyKey));
        }

        /// <summary>Next: release a routine parked on the user in a live loop (coarse → fine, fine → check). 409
        /// otherwise.</summary>
        public async Task<OperationAcceptedDto> NextAsync(string? idempotencyKey, CancellationToken ct) {
            CancellationTokenSource? inFlight;
            lock (_gate) {
                var waitable = _active && _state is ("adjusting" or "paused");
                if (!waitable) {
                    throw new InvalidOperationException("polar alignment has nothing to advance — it is not waiting on you");
                }
                _advanceRequested = true;
                inFlight = _advanceCts;
            }
            // A capture (and its blind solve, tens of seconds on the main camera) in flight is moot now:
            // cancel it so the next stage starts at once. Outside the lock — cancellation runs callbacks.
            if (inFlight is not null) {
                try {
                    await inFlight.CancelAsync().ConfigureAwait(false);
                } catch (ObjectDisposedException) {
                    // The capture finished and the loop disposed its source between our read and this call.
                }
            }
            return Accepted("polar-align.next", idempotencyKey);
        }

        // The live loop's in-flight capture, cancellable by Next; null between captures. Guarded by _gate.
        // Owned by the loop iteration that created it (a `using`); Dispose only sweeps a reference a
        // torn-down service might still hold.
        private CancellationTokenSource? _advanceCts;

        /// <summary>Consume the user's Next for this run (false when none is pending).</summary>
        private bool TakeAdvanceRequest(int gen) {
            lock (_gate) {
                if (_generation != gen || !_advanceRequested) {
                    return false;
                }
                _advanceRequested = false;
                return true;
            }
        }

        /// <summary>The parked refusal's sentence (#2048): Polar Align never unparks the mount.</summary>
        internal const string MountParkedMessage = "Mount is parked — unpark it and point near the pole first";

        /// <summary>#2048 — the mount-side checks every start makes before anything moves, the confirm step's plan
        /// included: a connected mount, not parked (refused, never unparked: NINA refuses too, PolarAlignment.cs
        /// 1108-1110), a configured site latitude.</summary>
        private SiteSettingsDto MountPreflight() {
            var mountInfo = _mount.GetInfo();
            if (mountInfo?.Connected != true) {
                throw new InvalidOperationException("mount is not connected");
            }
            if (mountInfo.AtPark) {
                throw new RefusalException(RefusalCatalog.PolarAlignMountParked, MountParkedMessage);
            }
            var site = _profileStore.GetSiteSettings();
            if (!(double.IsFinite(site.LatitudeDeg) && Math.Abs(site.LatitudeDeg) > 0 && Math.Abs(site.LatitudeDeg) <= 90)) {
                throw new InvalidOperationException(
                    "site latitude must be configured (non-zero) before polar alignment — the routine measures against your site's celestial pole");
            }
            return site;
        }

        /// <summary>#2048 — the run's RA band: the direction away from the meridian at the start, and the start pose the
        /// later stages measure their offset from (<see cref="RaOffsetFromStartDeg"/>).</summary>
        private sealed record RunBand(double AwaySign, double StartRaDeg, long StartedAt, double MaxSweepDeg);

        /// <summary>The plan of a start at <paramref name="stage"/> from the mount's pointing read now, with the band the
        /// run keeps to and its place against the session's sweep origin (#2089, <see cref="PlanSweep"/>). At a new origin
        /// the first turn goes away from the meridian (<see cref="AwayFromMeridianSign"/>), unchanged; a later start turns
        /// back toward the origin, and one whose turns would go past the limit is refused.</summary>
        private async Task<(PolarAlignStartPlanDto Plan, RunBand Band, SweepPlan Sweep)> PlanStartAsync(
                string? stage, PolarAlignSettingsDto settings, CancellationToken ct) {
            // From the driver, never the cache: the direction the user confirms and the band every later turn keeps to
            // are both read here.
            var (raDeg, decDeg) = await DriverPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the mount did not report where it points — check its connection, then start again");
            var startedAt = Stopwatch.GetTimestamp();
            var checkOnly = stage == PolarAlignStages.Check;
            var stepDeg = checkOnly ? settings.CheckStepDeg : settings.SeedRotationDeg;
            // Every stage the user can move on to (coarse → fine → check) turns twice by its own step.
            var maxSweepDeg = 2.0 * (checkOnly ? settings.CheckStepDeg : Math.Max(settings.SeedRotationDeg, settings.CheckStepDeg));
            var sweep = PlanSweep(raDeg, decDeg, maxSweepDeg);
            var awaySign = sweep.DirSign;
            var plan = new PolarAlignStartPlanDto(stage, awaySign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West,
                stepDeg, Turns: 2, maxSweepDeg, sweep.NewOrigin, Math.Round(sweep.OffsetDeg, 1), sweep.LimitDeg);
            return (plan, new RunBand(awaySign, raDeg, startedAt, maxSweepDeg), sweep);
        }

        /// <inheritdoc />
        public async Task<PolarAlignStartPlanDto> GetStartPlanAsync(string? stage, CancellationToken ct) {
            if (stage is not null && !PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
            }
            MountPreflight();
            var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
            return (await PlanStartAsync(stage, settings, ct).ConfigureAwait(false)).Plan;
        }

        /// <summary>
        /// Begin the routine at <paramref name="stage"/>: <c>coarse</c> (default) → the main-camera seed and live loop;
        /// <c>fine</c> → the guide-camera seed + live loop; <c>check</c> → the three-point measurement only. Every stage
        /// starts from the current pointing (#2048: no Home, no GoTo). Preflight (connected guider, connected and unparked
        /// mount, a configured site latitude, the user's <paramref name="confirmation"/> of the turn direction), then the
        /// guide-camera PA-session lease, then the run on a background task. Idempotent — Start on an already-running
        /// routine is a no-op accept. Throws <see cref="InvalidOperationException"/> on a failed preflight (409 by the
        /// endpoint; a <see cref="RefusalException"/> carries its cause). The lease is taken through the custody module
        /// before the guider's capture idles and its loop stops; when either fails, the lease is given back.
        /// </summary>
        public async Task<OperationAcceptedDto> StartAsync(
                string? stage, PolarAlignStartConfirmation? confirmation, string? idempotencyKey, CancellationToken ct) {
            var explicitStage = stage is not null;
            stage ??= PolarAlignStages.Coarse;
            if (!PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
            }
            // #2048 — an end request from here on (a Stop while this Start waits for the lock or takes the camera)
            // means no run is launched.
            long endRequestsAtCall;
            lock (_gate) {
                endRequestsAtCall = _endRequests;
            }
            // #2066 — the run's one reading of the mount's stop count, taken at the call like a REST GoTo's: a Stop Mount,
            // Emergency Stop, park or Find Home from here on ends the run before it moves (ThrowIfStoppedFromOutside).
            var abortEpochAtCall = (_mount as ITelescopeService)?.AbortEpoch ?? 0;
            var ownAbortsAtCall = Interlocked.Read(ref _ownAborts);
            // The reading the run keeps (_abortWatch). Re-read at the launch when only Polar Align's own stops moved the
            // count since the call: a Start pressed right after a failed turn must not inherit the old run's resent
            // AbortSlew, or the telescope service would refuse the new run's first motor command.
            (long Epoch, long OwnAborts) RunAbortBaseline() {
                var epochNow = (_mount as ITelescopeService)?.AbortEpoch ?? 0;
                var ownNow = Interlocked.Read(ref _ownAborts);
                return epochNow - abortEpochAtCall <= ownNow - ownAbortsAtCall
                    ? (epochNow, ownNow)
                    : (abortEpochAtCall, ownAbortsAtCall);
            }
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                lock (_gate) {
                    if (_active) {
                        return Accepted("polar-align.start", idempotencyKey);
                    }
                }
                // Drain any prior run FIRST (a self-terminated failure flips _active synchronously,
                // but its task is still publishing the error event, releasing the lease, and
                // restoring tracking). Acquiring the new lease before that release lands would race
                // the two set_pa_session calls on the wire — the old release could drop the lease
                // the new routine believes it holds. Awaiting here (under _opLock, like Stop does)
                // guarantees release-then-acquire ordering; a wedged zombie still gets abandoned
                // after the grace and is fenced by the generation bump below.
                var (priorCts, priorRun) = (_runCts, _runTask);
                _runCts = null;
                _runTask = null;
                lock (_gate) {
                    _liveRun = null; // that run ended by itself (failed or checked): its exit stopped the mount
                }
                if (priorRun is not null) {
                    await AwaitRunUnwindAsync(priorRun).ConfigureAwait(false);
                }
                priorCts?.Dispose();
                var guiderClient = _guider.ConnectedLink();
                var site = MountPreflight();
                // #2070 — a false zero after a power loss made the routine slew below the pole (RC51,
                // 2026-11-19): refused before anything moves. The Find Home, slew and motor moves are
                // guarded again in the mount, so a reconnect mid-run cannot home or turn by the model.
                if (_mount is ITelescopeService pose && await pose.PositionUnverifiedReasonAsync(ct).ConfigureAwait(false) is { } unverified) {
                    throw TelescopeService.PositionUnverifiedRefusal(unverified);
                }
                var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
                // #2048 — nothing moves without the user's confirmation of this start and its RA turn direction, read from
                // the mount now: a direction that changed since the user saw it (the hour angle crossed the meridian)
                // is asked again. NINA asks nothing, but it starts from a GoTo of its own; Ara starts where the user
                // pointed the mount, so the user has to see where it will turn.
                var (plan, band, sweep) = await PlanStartAsync(stage, settings, ct).ConfigureAwait(false);
                // #2089 — the user also confirmed whether this pose is a new start (the mount was moved by other means, or
                // first start since it connected) or a continued one: a plan that changed between them since is asked again.
                if (confirmation is null || !string.Equals(confirmation.Direction, plan.Direction, StringComparison.Ordinal)
                        || confirmation.NewOrigin != plan.NewOrigin) {
                    LogStartNotConfirmed(confirmation?.Direction ?? "none", plan.Direction);
                    throw new RefusalException(RefusalCatalog.PolarAlignUnconfirmed,
                        confirmation is null
                            ? $"confirm the start first: tube near the pole, counterweights down; RA will turn ~{plan.StepDeg:F0}° twice toward the {plan.Direction}; keep a hand on the mount's power"
                            : confirmation.NewOrigin != plan.NewOrigin
                                ? "the mount moved since you confirmed the start, or Polar Align's start pose changed — confirm the start again"
                                : $"the RA turn direction changed since you confirmed it (it is {plan.Direction} now) — confirm the start again",
                        new Dictionary<string, object?> { ["direction"] = plan.Direction });
                }
                var mainCamera = _mainCamera;
                if (stage == PolarAlignStages.Coarse) {
                    // Before the mount moves: a camera that is off would otherwise home, slew, and then
                    // fail the seed as a sky problem.
                    var coarseUnavailable = mainCamera is null
                        ? "the coarse stage needs the main camera's plate solver, which this daemon has not wired up — start at the fine stage instead"
                        : await mainCamera.UnavailableReasonAsync(ct).ConfigureAwait(false);
                    if (coarseUnavailable is not null) {
                        if (explicitStage) {
                            throw new InvalidOperationException(coarseUnavailable);
                        }
                        // A Start that named no stage (Simple Mode, the Preflight pilot) wants the routine,
                        // not the rough step in particular: a guide-camera-only session still polar-aligns.
                        LogCoarseSkipped(coarseUnavailable);
                        stage = PolarAlignStages.Fine;
                    }
                }
                // Every stage opens in its own first state: the run starts from where the mount points (#2048).
                var initialState = stage == PolarAlignStages.Check ? "checking" : "seeding";
                // ADR 0016: the custody module refuses while the Guider is busy, stops guide focus or the live
                // view if one holds the camera, and takes the guider's lease (a new loop cannot start under it).
                // Guiding taking the camera ends the run like a Stop, but hands back the run's tracking (#2048).
                var claim = await _guider.TakeCameraAsync(CameraUse.PolarAlign,
                    by => EndRoutineAsync("polar-align.stop", outcome: "aborted", null, CancellationToken.None,
                        endRequest: by == CameraUse.Guiding ? HandedToGuiding : null), ct).ConfigureAwait(false);
                try {
                    // #1711: a capture the previous run abandoned may still be exposing; the guider refuses a new one until it ends.
                    await guiderClient.WaitForCaptureIdleAsync(ct).ConfigureAwait(false);
                    lock (_gate) {
                        _abandonedCaptureDebts.Clear(); // idle: that capture's event has been delivered, nothing is owed
                    }
                    // #1749: the guider refuses a single frame while it loops.
                    if (!await guiderClient.StopLoopForCaptureAsync(LoopStopTimeout, ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("the Guide Camera loop did not stop — stop looping in the Guider, then start Polar Align again");
                    }
                } catch {
                    await claim.ReleaseAsync().ConfigureAwait(false);
                    throw;
                }
                lock (_gate) {
                    _claim = claim;
                }
                int gen = 0;
                int startSeq = 0;
                var runCts = new CancellationTokenSource();
                var runEnd = new RunEnd();
                bool endedWhileStarting;
                lock (_gate) {
                    // Checked with the run's publication in one critical section: an end request either lands first
                    // (seen here, no run) or finds the live run below and stops it.
                    endedWhileStarting = _endRequests != endRequestsAtCall;
                    if (endedWhileStarting) {
                        _claim = null;
                    } else {
                        _liveRun = (runCts, runEnd);
                        _active = true;
                        _state = initialState;
                        _stage = stage;
                        _advanceRequested = false;
                        _checkSolvesDone = 0;
                        _checkResult = null;
                        _fineFinalAltArcmin = null;
                        _fineFinalAzArcmin = null;
                        _readoutCaution = null;
                        _framesCaptured = 0;
                        _lastFrameId = null;
                        _altErrorArcmin = null;
                        _azErrorArcmin = null;
                        _liveIterations = 0;
                        _liveFrames = 0;
                        _startedAt = DateTimeOffset.UtcNow;
                        _captureRequested = false;
                        _waitingForCapture = false;
                        _lastFrame = null;
                        _runFrameSize = null; // read afresh per run — the guide camera may have changed since the last one
                        gen = ++_generation;
                        var (baselineEpoch, baselineOwn) = RunAbortBaseline();
                        _abortWatch = (gen, baselineEpoch, baselineOwn);
                        // #2066 — the last run is over (its exit ran above): a motion mark it left (a takeover, which confirms
                        // no stop; a stop that was never confirmed) is not this run's, which has moved nothing yet, and must
                        // not keep the telescope endpoints refusing Park and GoTo (IsMovingMount) while it only solves.
                        EndMotion();
                        _lastCaptureFault = null; // fresh run — stale capture faults must not color its failures
                        // _abandonedCaptureDebts was cleared above, after the guider reported idle: a capture
                        // abandoned by the just-stopped run has delivered its event by now.
                        startSeq = ++_startSeq;
                        AdoptSweepPlanLocked(sweep); // #2089 — a new origin once confirmed, and where Polar Align saw the mount
                    }
                }
                if (endedWhileStarting) {
                    runCts.Dispose();
                    await claim.ReleaseAsync().ConfigureAwait(false);
                    LogStartStoppedBeforeRun();
                    throw new InvalidOperationException("polar alignment was stopped while it was starting — the mount was not moved");
                }
                _runCts = runCts;
                var runToken = runCts.Token;
                LogStarted(stage);
                LogStartConfirmed(plan.Direction, plan.StepDeg, plan.MaxSweepDeg);
                // `started` goes out BEFORE the run task exists: the run's first stage event is only a directory
                // create and a few mount reads away, and a client must fold `started` first.
                await PublishAsync(WsEventCatalog.PolarAlignStarted, new JsonObject { ["state"] = initialState, ["stage"] = stage }).ConfigureAwait(false);
                _runTask = Task.Run(() => RunRoutineAsync(guiderClient, mainCamera, site, settings, stage, band, gen, startSeq, runEnd, runToken), CancellationToken.None);
                return Accepted("polar-align.start", idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        /// <summary>
        /// Stop the routine. First, before any lock or wait (#2048): fence the run, send the mount AbortSlew (when a
        /// motor move may run) and MoveAxis 0 on both axes, and cancel the state machine. Then wait
        /// for it to unwind (its exit sends the stop again, confirms the mount stopped, and turns tracking off), release
        /// the PA-session lease (best-effort — it auto-expires regardless), and report <c>stopped</c> — or
        /// <c>failed</c> with <c>mount_did_not_stop</c> when the stop could not be confirmed. Idempotent; the caller's
        /// token never cancels it. §45 step 10: the mount stops where it is — no slew home.
        /// </summary>
        public Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) =>
            EndRoutineAsync("polar-align.stop", outcome: "aborted", idempotencyKey, ct);

        /// <inheritdoc />
        public Task<OperationAcceptedDto> StopForSequenceAsync(CancellationToken ct) =>
            EndRoutineAsync("polar-align.stop", outcome: EndedBySequence, null, ct);

        // Outcome of a run a sequence start ended: stopped like an abort, but with no measured result stored.
        private const string EndedBySequence = "ended_by_sequence";

        /// <summary>
        /// §45 step 9 — the user is satisfied: same unwind as Stop, but the §45.13 session row records
        /// <c>complete</c> with the achieved error. Accepted (without a log row) when no routine is
        /// active, mirroring Stop's idempotency. In the rough step it is accepted once the readout is inside
        /// the profile's tolerance (#1875): a rough alignment that already good needs no fine step.
        /// </summary>
        public Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct) {
            var tolerance = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings()).TargetToleranceArcmin;
            lock (_gate) {
                // A rough readout outside the tolerance is not an alignment to record as "complete" (a stage-unaware
                // client's Done would otherwise write the §45.13 row the guiding and "last night" readouts trust).
                if (_active && _stage == PolarAlignStages.Coarse
                    && !(_altErrorArcmin is double alt && _azErrorArcmin is double az && Math.Sqrt(alt * alt + az * az) <= tolerance)) {
                    throw new InvalidOperationException("Done is not available in the rough step until the error is inside your tolerance — tap Next: fine to refine it, or Stop");
                }
            }
            return EndRoutineAsync("polar-align.complete", outcome: "complete", idempotencyKey, ct);
        }

        /// <param name="endRequest">What the run's exit is told ended it, when not <paramref name="outcome"/>
        /// (<see cref="HandedToGuiding"/>).</param>
        private async Task<OperationAcceptedDto> EndRoutineAsync(
                string operationType, string outcome, string? idempotencyKey, CancellationToken ct, string? endRequest = null) {
            // #2048 — the caller's token never cancels an end: a client that gave up (its 5 s timeout, a dropped link)
            // must not leave the mount moving or the routine half-ended.
            _ = ct;
            // The mount first, before _opLock: a Start or an earlier Stop holding the lock must not hold the mount's stop back.
            await StopRunFirstAsync(endRequest ?? outcome).ConfigureAwait(false);
            await _opLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try {
                var (cts, run) = (_runCts, _runTask);
                _runCts = null;
                _runTask = null;
                RunEnd? end;
                // Supersede the run BEFORE cancelling: if it outlives the 15s unwind grace (a
                // guider/mount call that ignores cancellation for a while), its late catch blocks
                // must see a stale generation — otherwise a delayed failure would clobber the
                // terminal "stopped" state with "failed" + a spurious error event. The generation
                // guard protects a deliberate Stop exactly like it protects a successor Start.
                // (StopRunFirstAsync bumped it already; a run a Start launched since is fenced here.)
                lock (_gate) {
                    _generation++;
                    end = _liveRun?.End;
                    _liveRun = null;
                }
                end?.Request(endRequest ?? outcome);
                if (cts is not null) {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                var unwound = run is null || await AwaitRunUnwindAsync(run).ConfigureAwait(false);
                cts?.Dispose();
                if (!unwound && end is { MountStopped: null } && endRequest != TakenOver) {
                    // The run is stuck in a call that ignores cancellation and never reached its exit: confirm the stop here,
                    // and turn tracking off now (it may never unwind; a mount left tracking runs on toward the pier). An end
                    // that hands tracking back leaves it as it is: the run gives it back when it unwinds.
                    var stopped = await StopMountMotionAsync(outcome).ConfigureAwait(false);
                    end.MountStopped = stopped;
                    if (!stopped || !HandBackWanted(finished: outcome == "complete", handedToGuiding: endRequest == HandedToGuiding)) {
                        TrackingOffBestEffort(outcome);
                    }
                }
                var mountNotStopped = end?.MountStopped == false;
                bool becameStopped;
                bool reportNotStopped = false;
                bool wasActive;
                lock (_gate) {
                    wasActive = _active;
                    _active = false;
                    if (wasActive && outcome != EndedBySequence) {
                        RecordLastResultLocked(outcome == "complete");
                    }
                    // The run's own gen-fenced resets are no-ops once the generation moved on above.
                    _waitingForCapture = false;
                    // A terminal "failed" or "checked" is a real outcome a polling client must still
                    // see — a late/stray Stop must not clobber it back to "stopped" (both already
                    // published their event, wrote their row and released the lease). The user's own
                    // Done on the checked verdict (Complete) does end it: the client leaves the
                    // verdict on the stopped event, and the check already wrote the row.
                    becameStopped = _state is not "failed" && (_state != "checked" || outcome == "complete");
                    if (becameStopped && mountNotStopped) {
                        // Never "stopped" for a mount that may still move: the client shows the failure and its sentence.
                        _state = "failed";
                        becameStopped = false;
                        reportNotStopped = true;
                    } else if (becameStopped) {
                        _state = "stopped";
                    }
                }
                if (wasActive && outcome != EndedBySequence) {
                    // Only a routine this call actually ended gets a session row — a failed routine
                    // already wrote its own, and a repeated Stop must not duplicate.
                    await WriteSessionRowBestEffortAsync(outcome, CancellationToken.None).ConfigureAwait(false);
                }
                await ReleaseLeaseBestEffortAsync().ConfigureAwait(false);
                LogStopped();
                if (reportNotStopped) {
                    await PublishErrorAsync(MountDidNotStop, CutPowerSentence).ConfigureAwait(false);
                } else if (becameStopped) {
                    await PublishStateEventAsync(WsEventCatalog.PolarAlignStopped, "stopped").ConfigureAwait(false);
                }
                end?.MarkReported();
                return Accepted(operationType, idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        public void Dispose() {
            // #2048 — daemon shutdown: a running routine stops the mount and turns tracking off first.
            StopRunForShutdown();
            _runCts?.Cancel();
            _runCts?.Dispose();
            _advanceCts?.Dispose();
            _opLock.Dispose();
            if (_ownsFetcher && _frameFetcher is IDisposable d) {
                d.Dispose(); // only the self-constructed default — a DI-injected fetcher is container-owned
            }
        }

        // ── the routine ──────────────────────────────────────────────────────────────────────

        private const string TrackingOffMessage =
            "the mount is not tracking and would not start — turn tracking on from the mount controls, then start again";

        /// <summary>A §42.3 fault recovery (a mount reconnect) can drop tracking between the routine's
        /// switch-on and its slew (CM4, 2026-11-05: six seconds later). Checked just before a slew, not
        /// during one: the iOptron reports tracking off while it slews.</summary>
        private async Task KeepTrackingOnForSlewAsync(int gen, CancellationToken ct) {
            ThrowIfStoppedFromOutside(gen); // #2066 — a stop from outside since the Start: no switch-on, no turn
            if (_mount.GetInfo().TrackingEnabled) {
                return;
            }
            LogTrackingDropped();
            await TurnTrackingOnOrFailAsync(gen, ct).ConfigureAwait(false);
        }

        private async Task TurnTrackingOnOrFailAsync(int gen, CancellationToken ct) {
            if (!await TurnTrackingOnAsync(gen, ct).ConfigureAwait(false)) {
                throw new RoutineFailedException("tracking_off", TrackingOffMessage);
            }
        }

        /// <summary>Set tracking and wait for the mount to report it (a driver can take a moment, and a
        /// state that never flips means the mount refused). True once it reports tracking. A superseded
        /// run (Stop, or a newer Start) writes nothing: the mount is the current run's now.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A mount fault while enabling tracking becomes the routine's tracking_off failure.")]
        private async Task<bool> TurnTrackingOnAsync(int gen, CancellationToken ct) {
            // Outside the try: the catch below would otherwise log this as a mount failure.
            if (!TryMarkTrackingTouched(gen)) {
                throw new OperationCanceledException("the run was superseded before it turned tracking on");
            }
            // #2066 — Stop Mount or Emergency Stop (its first step) landed before this switch-on: tracking stays as the stop left it.
            ThrowIfStoppedFromOutside(gen);
            try {
                bool accepted;
                try {
                    accepted = _mount.SetTrackingEnabled(true);
                } finally {
                    _ownTrackingWrites = _mount.TrackingWriteCount; // a refused or thrown write is still ours
                }
                if (!accepted) {
                    return false;
                }
            } catch (Exception ex) {
                LogTrackingEnableFailed(ex);
                return false;
            }
            var deadline = DateTimeOffset.UtcNow + TrackingConfirmTimeout;
            while (!_mount.GetInfo().TrackingEnabled) {
                if (DateTimeOffset.UtcNow >= deadline) {
                    return false;
                }
                await Task.Delay(TrackingConfirmPoll, ct).ConfigureAwait(false);
            }
            return true;
        }


        /// <summary>A fatal routine failure with a machine-readable reason for the WS error event
        /// (<c>seed_solve_failed</c>, <c>slew_failed</c>, …). Anything else that escapes the run
        /// task is reported as reason <c>internal_error</c>.</summary>
        [SuppressMessage("Design", "CA1032:Implement standard exception constructors",
            Justification = "Private, never serialized or thrown without a reason code; the standard ctors would allow constructing it without one.")]
        private sealed class RoutineFailedException(string reason, string message, bool mountTakenOver = false) : Exception(message) {
            public string Reason { get; } = reason;
            /// <summary>#2066 — the stop that ended the run handed the mount to its sender (Emergency Stop, a park, a Find
            /// Home): the exit leaves the mount to it.</summary>
            public bool MountTakenOver { get; } = mountTakenOver;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The run task is the routine's top-level boundary: any escaped exception must land in the 'failed' state + WS error event, never fault an unobserved background task.")]
        private async Task RunRoutineAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, SiteSettingsDto site, PolarAlignSettingsDto settings, string stage,
                RunBand band, int gen, int startSeq, RunEnd end, CancellationToken ct) {
            // Per-run unique dir: an abandoned (zombie) run and its successor must never share
            // files — this run's cleanup deletes only its own directory.
            var workDir = Path.Combine(Path.GetTempPath(), "ara-polar-align", Guid.NewGuid().ToString("N"));
            // The user's tracking setting, handed back on every exit path once this run has touched it.
            bool? priorTracking = null;
            lock (_gate) {
                _trackingTouched = false;
            }
            var alignerRun = BeginAlignerRun(site.LatitudeDeg);
            lock (_gate) {
                _raMotorSign = 1.0; // a new run learns the RA motor's sign afresh
            }
            // #2048 — the exit every path takes once: stop the mount and confirm it, then tracking. Before a failure or the
            // check's verdict is reported (the mount is safe by the time the client hears of the end), else in the finally.
            var exited = false;
            bool? exitStopped = null;
            async Task<bool?> ExitMountOnceAsync(bool finished, string why, bool handedToGuiding = false, bool takenOver = false) {
                if (exited) {
                    return exitStopped;
                }
                exited = true;
                if (!NoNewerRunStarted(startSeq)) {
                    // A newer run owns the mount (and has its own exit): a stop from here would cut its moves short.
                    LogExitLeftToSuccessor();
                    return null;
                }
                if (takenOver || end.Requested == TakenOver) {
                    // #2066 — Emergency Stop, the safety park, a park or a Find Home owns the mount now: it stops and checks
                    // the mount itself (Emergency Stop's second pass), or moves it on purpose. A stop round from here would
                    // abort the park, and the confirmation (up to 8 s) would hold Emergency Stop's rung past its bound.
                    LogExitLeftToTakeover(why);
                    return null;
                }
                lock (_gate) {
                    _exitsInFlight++;
                }
                try {
                    exitStopped = await StopMountMotionAsync(why).ConfigureAwait(false);
                    end.MountStopped = exitStopped;
                    // #2089 — where this run left the mount: the next start tells a continued pose from a moved one by it.
                    await NoteMountLeftAfterStopAsync().ConfigureAwait(false);
                    // Read again after the stop: a Stop or a sequence start that landed during it turns a finished run into
                    // a stopped one (tracking off), and Guiding taking the camera meanwhile still gets its hand-back.
                    var requested = end.Requested;
                    ApplyExitTracking(priorTracking,
                        finished: finished && requested is null or "complete",
                        handedToGuiding: handedToGuiding || requested == HandedToGuiding,
                        exitStopped, why);
                } finally {
                    lock (_gate) {
                        _exitsInFlight--;
                    }
                }
                if (exitStopped == false && end.Reported) {
                    // The stop path already told the client this run ended: the finding is this run's to report.
                    await PublishErrorAsync(MountDidNotStop, CutPowerSentence).ConfigureAwait(false);
                }
                return exitStopped;
            }
            try {
                Directory.CreateDirectory(workDir);

                // #1665 — never capture with another profile's Guide Camera.
                if (_guider.OutOfStepReason is { } outOfStep) {
                    throw new RoutineFailedException("guider_out_of_step", outOfStep);
                }

                // #2048 — Polar Align never unparks (NINA refuses a parked mount too). StartAsync refused one; a park
                // that landed since then fails the run before anything moves.
                var mountInfo = _mount.GetInfo();
                if (mountInfo.AtPark) {
                    throw new RoutineFailedException("mount_parked", MountParkedMessage);
                }
                priorTracking = mountInfo.TrackingEnabled;
                // One PA-session lease renewer for the whole run: the stages share its clock, so a
                // hand-off never renews twice back to back.
                Func<Task> renew = () => RenewLeaseAsync(ct);

                // The stages run in order from the one the user started at; each live loop returns
                // when the user taps Next, and the check ends the run itself (state `checked`). Each
                // stage starts where the last one left the mount: no Home, no GoTo back (#2048); its
                // turns keep to the band the user confirmed (TurnDirectionForStageAsync).
                if (stage == PolarAlignStages.Coarse) {
                    ThrowIfStoppedFromOutside(gen); // #2066 — between the stages too
                    // StartAsync refused a coarse start without the main camera's solver.
                    await RunCoarseStageAsync(
                        mainCamera ?? throw new InvalidOperationException("the coarse stage started without a main-camera solver"),
                        site, settings, band, renew, gen, ct).ConfigureAwait(false);
                    stage = PolarAlignStages.Fine;
                }
                if (stage == PolarAlignStages.Fine) {
                    ThrowIfStoppedFromOutside(gen);
                    await RunFineStageAsync(guiderClient, site, settings, band, workDir, renew, gen, ct).ConfigureAwait(false);
                    stage = PolarAlignStages.Check;
                }
                ThrowIfStoppedFromOutside(gen);
                var verdict = await RunCheckStageAsync(guiderClient, workDir, site, settings, band, gen, ct).ConfigureAwait(false);
                // The check's verdict is a finished run: the mount is stopped and confirmed before the client hears of it.
                if (await ExitMountOnceAsync(finished: true, "checked").ConfigureAwait(false) == false) {
                    throw new RoutineFailedException(MountDidNotStop, CutPowerSentence);
                }
                await FinishCheckedAsync(gen, verdict).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested || !IsCurrent(gen)) {
                // Stop — the stopping path owns the state + events. Any other cancellation (a solver or
                // HTTP timeout surfacing as TaskCanceledException) is a failure like any other below: left
                // here it would end the run silently with the state still "adjusting" and the lease held.
            } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException ex) when (IsCurrent(gen)) {
                // Solver setup problems (guide optics unset, ASTAP path wrong) are user-fixable —
                // surface the solver's own actionable message, not internal_error.
                await FailRoutineAsync("solver_configuration", ex, gen, () => ExitMountOnceAsync(finished: false, "failed")).ConfigureAwait(false);
            } catch (RoutineFailedException ex) when (IsCurrent(gen)) {
                await FailRoutineAsync(ex.Reason, ex, gen, () => ExitMountOnceAsync(finished: false, "failed", takenOver: ex.MountTakenOver)).ConfigureAwait(false);
            } catch (Exception ex) when (IsCurrent(gen)) {
                await FailRoutineAsync("internal_error", ex, gen, () => ExitMountOnceAsync(finished: false, "failed")).ConfigureAwait(false);
            } catch (Exception ex) {
                // A superseded (zombie) run: its failure must not touch state, events, the lease, or
                // the session log the successor run now owns — log and drain quietly.
                LogRoutineFailed("superseded", ex);
            } finally {
                EndAlignerRun(alignerRun);
                // Every exit stops the mount, a clean Stop included (it bumps the generation but not the start
                // sequence); only a run that a NEWER RUN superseded leaves the mount to that run. Tracking goes off,
                // or back to how the run found it after Done with stop_tracking_when_done off (ApplyExitTracking).
                if (!exited) {
                    await ExitMountOnceAsync(finished: end.Requested == "complete", end.Requested ?? "ended",
                        handedToGuiding: end.Requested == HandedToGuiding).ConfigureAwait(false);
                }
                TryDeleteWorkDir(workDir);
            }
        }

        /// <summary>Record that the CURRENT run is about to write tracking; false for a superseded
        /// run (a Stop or a newer Start bumped the generation), whose late write would land on a mount
        /// the successor now owns — and whose flag would make the successor hand back tracking it
        /// never touched. Guarded by <c>_gate</c> like every other run-task field.</summary>
        private bool TryMarkTrackingTouched(int gen) {
            lock (_gate) {
                if (_generation != gen) {
                    return false;
                }
                _trackingTouched = true;
                return true;
            }
        }

        /// <summary>§45.4 — the guide camera's frame size from the daemon, or null when it has no
        /// answer (older daemon, refused RPC, dropped connection). Never fails the routine.</summary>
        private async Task<(int Width, int Height)?> ReadGuideFrameSizeAsync(IGuideCaptureLink guiderClient, CancellationToken ct) {
            try {
                var size = await guiderClient.GetCameraFrameSizeAsync(ct).ConfigureAwait(false);
                if (size is null) {
                    LogFrameSizeUnavailable("the RPC returned no size");
                }
                return size;
            } catch (OperationCanceledException) {
                throw;
            } catch (InvalidOperationException ex) {
                // Not connected. A refused RPC never throws: SendMessage turns it into an error
                // response and GetCameraFrameSizeAsync into null (handled above).
                LogFrameSizeUnavailable(ex.Message);
                return null;
            }
        }

        /// <summary>One capture-and-solve of a live loop: the frame id, the hint (the last good solve,
        /// or the mount's pointing at first), the settings read for this frame, and the timing to fill.</summary>
        private delegate Task<PolarAlignSolveOutcome> LiveCapture(
            string frameId, (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, FrameTiming timing, CancellationToken ct);

        /// <summary>The live loop shared by the coarse stage (main camera) and the fine stage (guide camera),
        /// both reading each solve as a §45.8 reverse-projection from the seed. Captures and solves until
        /// Stop cancels it, or the user taps Next — then it returns normally. <paramref name="toError"/>
        /// turns a good solve (and its time) into the alt/az error.</summary>
        private async Task SolveLoopAsync(
                LiveCapture capture,
                Func<PolarAlignSolveOutcome, DateTimeOffset, (double AltErr, double AzErr)> toError,
                (double RaDeg, double DecDeg)? initialHint, Func<Task> renewLeaseIfDue,
                int gen, CancellationToken ct) {
            // The count runs on across the stages: frame ids stay unique within a run (failed frames
            // included — a counter of solves alone would hand the next stage a used id) and the §45.13 row
            // counts every live frame, not the last stage's.
            int iteration;
            lock (_gate) {
                iteration = _liveFrames;
            }
            var consecutiveFailures = 0;
            var hint = initialHint;

            while (!ct.IsCancellationRequested) {
                // #2066 — Stop Mount or Emergency Stop while the user adjusts ends the run too.
                ThrowIfStoppedFromOutside(gen);
                if (TakeAdvanceRequest(gen)) {
                    return; // the user moves the routine on — the caller owns the next stage
                }
                // Re-read every frame: exposure, binning, cadence and loop mode apply mid-run.
                var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
                if (settings.LoopMode == PolarAlignLoopModes.SingleFrame) {
                    var requested = await WaitForCaptureRequestAsync(gen, renewLeaseIfDue, ct).ConfigureAwait(false);
                    if (requested is null) {
                        return; // Next arrived while parked
                    }
                    settings = requested;
                } else {
                    lock (_gate) {
                        _captureRequested = false; // loop mode re-shoots anyway
                    }
                }
                var cadence = TimeSpan.FromMilliseconds(settings.LoopCadenceMs);
                var iterationStart = DateTimeOffset.UtcNow;
                await renewLeaseIfDue().ConfigureAwait(false);
                iteration++;
                lock (_gate) {
                    if (_generation == gen) {
                        _liveFrames = iteration;
                    }
                }
                var frameId = "live-" + iteration.ToString(CultureInfo.InvariantCulture);
                var timing = new FrameTiming();
                PolarAlignSolveOutcome s;
                using (var advance = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    lock (_gate) {
                        if (_generation == gen) {
                            _advanceCts = advance;
                        }
                    }
                    try {
                        s = await capture(frameId, hint, settings, timing, advance.Token).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (!ct.IsCancellationRequested && advance.IsCancellationRequested) {
                        // Next arrived mid-capture: the frame is moot and the caller owns the next stage.
                        TakeAdvanceRequest(gen);
                        return;
                    } finally {
                        lock (_gate) {
                            if (ReferenceEquals(_advanceCts, advance)) {
                                _advanceCts = null;
                            }
                        }
                    }
                }
                // #2072 — the readout below assumes a camera fixed in alt/az: every frame, solved or not, ends the run
                // when the mount reads tracking on.
                var tracking = RequireTrackingOff(frameId);
                if (!s.Success) {
                    consecutiveFailures++;
                    await CompleteFrameAsync(gen, frameId, s, timing, consecutiveFailures).ConfigureAwait(false);
                    if (consecutiveFailures == MaxConsecutiveSolveFailures) {
                        // Park in paused (§45.11: "no solve — check sky") but KEEP retrying at a
                        // slower cadence; the next good solve resumes adjusting automatically.
                        lock (_gate) {
                            if (_generation == gen) {
                                _state = "paused";
                            }
                        }
                        LogLoopPaused(consecutiveFailures);
                        if (IsCurrent(gen)) {
                            await PublishStateEventAsync(WsEventCatalog.PolarAlignPaused, "paused").ConfigureAwait(false);
                        }
                    }
                    if (settings.LoopMode != PolarAlignLoopModes.SingleFrame) {
                        await Task.Delay(consecutiveFailures >= MaxConsecutiveSolveFailures ? PausedRetryDelay : cadence, ct).ConfigureAwait(false);
                    }
                    continue;
                }
                consecutiveFailures = 0;
                hint = (s.RaDegJnow, s.DecDegJnow);
                var (altErr, azErr) = toError(s, DateTimeOffset.UtcNow);

                SetErrors(gen, altErr, azErr, "adjusting", frameId);
                LogLiveReadout(frameId, altErr, azErr, s.RaDegJnow, s.DecDegJnow, tracking);
                lock (_gate) {
                    if (_generation == gen) {
                        _liveIterations = iteration;
                    }
                }
                await CompleteFrameAsync(gen, frameId, s, timing, consecutiveSolveFailures: 0).ConfigureAwait(false);
                await PublishProgressAsync(gen, iteration, altErr, azErr, solved: true).ConfigureAwait(false);
                // #1971: the motors' turn. The frame after it measures what they did.
                await CorrectAsync(gen, altErr, azErr, settings, ct).ConfigureAwait(false);

                var elapsed = DateTimeOffset.UtcNow - iterationStart;
                if (settings.LoopMode != PolarAlignLoopModes.SingleFrame && elapsed < cadence) {
                    await Task.Delay(cadence - elapsed, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>The guider PA-session lease renewer for a run: the custody module renews it, at most once
        /// per its interval.</summary>
        private Task RenewLeaseAsync(CancellationToken ct) {
            GuideCameraCustody.Claim? claim;
            lock (_gate) {
                claim = _claim;
            }
            return claim?.RenewIfDueAsync(ct) ?? Task.CompletedTask;
        }

        /// <summary>Single-frame mode: park until the client asks for a frame (or switches back to
        /// loop mode), keeping the guider lease alive meanwhile. Returns the settings to shoot with,
        /// or null when the user's Next arrived instead.</summary>
        private async Task<PolarAlignSettingsDto?> WaitForCaptureRequestAsync(
                int gen, Func<Task> renewLeaseIfDue, CancellationToken ct) {
            SetWaitingForCapture(gen, true);
            try {
                while (true) {
                    if (TakeAdvanceRequest(gen)) {
                        return null;
                    }
                    lock (_gate) {
                        if (_captureRequested) {
                            _captureRequested = false;
                            return PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
                        }
                    }
                    var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
                    if (settings.LoopMode != PolarAlignLoopModes.SingleFrame) {
                        return settings;
                    }
                    await renewLeaseIfDue().ConfigureAwait(false);
                    await Task.Delay(CaptureRequestPoll, ct).ConfigureAwait(false);
                }
            } finally {
                SetWaitingForCapture(gen, false);
            }
        }

        private void SetWaitingForCapture(int gen, bool waiting) {
            lock (_gate) {
                if (_generation == gen) {
                    _waitingForCapture = waiting;
                }
            }
        }

        /// <summary>Per-frame timings for the client's frame readout: capture = exposure + the
        /// guider's download + our fetch; solve = the plate solve alone (null when the frame never
        /// reached the solver).</summary>
        private sealed class FrameTiming {
            public double ExposureSeconds { get; set; }
            public TimeSpan Capture { get; set; }
            public TimeSpan? Solve { get; set; }
            /// <summary>Ask CaptureAndSolveAsync to keep the frame when it fails to solve (the seed's last attempt).</summary>
            public bool KeepUnsolvedFrame { get; set; }
            /// <summary>The unsolved frame kept for the focal-length measurement; the caller deletes it.</summary>
            public string? KeptFramePath { get; set; }
        }

        // ── capture + solve plumbing ─────────────────────────────────────────────────────────

        /// <summary>Seed frames must solve — retry a few times, then fail the routine (a seed that
        /// can't solve means clouds/focus problems the live loop can't fix either).</summary>
        private async Task<PolarAlignSolveOutcome> SolveSeedFrameAsync(
                LiveCapture capture, string frameId,
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var anyFrameReachedSolver = false;
            string? keptFrame = null;
            for (var attempt = 1; attempt <= SeedSolveAttempts; attempt++) {
                var timing = new FrameTiming { KeepUnsolvedFrame = attempt == SeedSolveAttempts };
                // One id per attempt, on frame_started and frame_complete alike.
                var attemptId = frameId + "-" + attempt.ToString(CultureInfo.InvariantCulture);
                var s = await capture(attemptId, hint, settings, timing, ct).ConfigureAwait(false);
                await CompleteFrameAsync(gen, attemptId, s, timing, s.Success ? 0 : attempt).ConfigureAwait(false);
                // #2066 — a stop during the exposure or the solve: no further attempt, and no turn after it.
                ThrowIfStoppedFromOutside(gen);
                keptFrame = timing.KeptFramePath;
                if (s.Success) {
                    return s;
                }
                // _lastCaptureFault reflects THIS attempt (set on any capture-layer fault, cleared
                // when the fetch delivers a frame to the solver) — a null here means the attempt
                // failed in the SOLVER, i.e. a real focus/sky problem exists regardless of what the
                // capture layer does on later attempts (review r4: classifying by the LAST attempt
                // alone let one trailing transient RPC fault relabel a genuine solve problem as
                // capture_rejected, pointing the operator at the wrong layer).
                lock (_gate) {
                    if (_generation == gen && _lastCaptureFault is null) {
                        anyFrameReachedSolver = true;
                    }
                }
            }
            string? captureFault;
            lock (_gate) {
                captureFault = _generation == gen ? _lastCaptureFault : null;
            }
            // A capture-layer fault (daemon rejected the capture, NO frame ever reached the solver)
            // is NOT a sky/focus problem — blame the right layer, with the daemon's own message.
            if (captureFault is not null && !anyFrameReachedSolver) {
                throw new RoutineFailedException("capture_rejected",
                    $"the {frameId} seed frame could not be captured after {SeedSolveAttempts} attempts: {captureFault}");
            }
            var measured = keptFrame is null ? null : await MeasureAndDiscardAsync(keptFrame, settings, hint, ct).ConfigureAwait(false);
            throw new RoutineFailedException("seed_solve_failed",
                $"the {frameId} seed frame failed to solve after {SeedSolveAttempts} attempts — check focus, sky conditions, and the optics configuration of the camera in use"
                + (measured is { } mm ? $". With no scale hint the frame solves at a scale that implies a guide focal length of about {mm:0} mm: set it in Settings → Guiding if that is your guide scope" : ""));
        }

        /// <summary>#1715 — one solve with no scale hint on the last unsolved seed frame; null when it does not solve
        /// or when the scale it found matches the profile's focal length (the optics are not the problem).</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A best-effort diagnostic on a failing routine: its own fault must not hide the seed failure it explains.")]
        private async Task<double?> MeasureAndDiscardAsync(
                string framePath, PolarAlignSettingsDto settings, (double RaDeg, double DecDeg)? hint, CancellationToken ct) {
            try {
                var binning = PolarAlignBinning.Resolve(settings.Binning, _runFrameSize);
                var measured = await _solver.MeasureFocalLengthAsync(framePath, binning, hint?.RaDeg, hint?.DecDeg, ct).ConfigureAwait(false);
                var configured = _profileStore.GetPhd2Settings().GuideFocalLength;
                return measured is { } mm && !(configured > 0 && Math.Abs(mm - configured) / configured <= 0.1) ? mm : null;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                return null;
            } finally {
                TryDeleteFile(framePath);
            }
        }

        /// <summary>One loop body: ask the guider for a saved solver frame, await its
        /// <c>SingleFrameComplete</c> event (the RPC only acks — the saved-FITS path arrives on the
        /// event), then hand the file to the solver. The FITS is deleted afterwards (§45.5 — PA
        /// frames never pollute the catalogue). A failed capture or solve returns
        /// <c>Success=false</c>; only transport-level faults throw.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A corrupt/truncated guider FITS or solver fault is one failed solve, not a routine abort (§45.11); configuration exceptions are deliberately excluded and fail the routine with an actionable message.")]
        private async Task<PolarAlignSolveOutcome> CaptureAndSolveAsync(
                IGuideCaptureLink guiderClient, string workDir, string frameId,
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, FrameTiming timing, CancellationToken ct) {
            var localPath = Path.Combine(workDir, frameId + ".fits");
            var binning = PolarAlignBinning.Resolve(settings.Binning, _runFrameSize);
            var captureStart = Stopwatch.GetTimestamp();
            timing.ExposureSeconds = settings.ExposureSeconds;
            await PublishFrameStartedAsync(gen, frameId, settings.ExposureSeconds).ConfigureAwait(false);
            var tcs = new TaskCompletionSource<SingleFrameCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            // §45 capture-fetch: the capture saves DAEMON-SIDE (save:true, no path — the daemon
            // sandboxes saves to its own directory and rejects foreign paths, the first-real-rig
            // failure), so completions can no longer be correlated by requested path. Captures are
            // strictly serialized per run (the PA lease is single-client and this method is the only
            // capture site), so the first completion after our RPC is ours — EXCEPT the event a
            // previously TIMED-OUT capture still owes. Accepting that stale event would silently
            // fetch/solve the PREVIOUS frame and feed seconds-old RA/Dec into the seed/adjust
            // geometry (review r1). The timeout path below records the debt (_abandonedCaptures);
            // this handler pays it by SWALLOWING exactly that many events — the stale capture then
            // surfaces as a clean timed-out/failed solve, never as misattributed data.
            // Set when this wait swallowed an event against a debt. Events can't be correlated to
            // captures (the daemon broadcast carries no request id), so the swallow is a guess: if
            // the abandoned capture never delivers (daemon hung — the common timeout cause), the
            // swallowed event was actually THIS capture's own completion. When that happens this
            // capture times out too — and it must NOT enqueue a fresh debt (review r3: doing so
            // re-arms the mistake against the NEXT capture, cascading one lost event through the
            // whole seed budget with each misfire refreshing the TTL). One swallow pays one debt,
            // rightly or wrongly; the cost of a wrong guess is capped at one retried capture.
            int swallowedThisWait = 0;
            void OnComplete(object? sender, SingleFrameCompleteEventArgs e) {
                if (TryConsumeStaleDebt()) {
                    Interlocked.Exchange(ref swallowedThisWait, 1);
                    LogCaptureFailed(frameId, "swallowed a stale SingleFrameComplete owed by an abandoned capture");
                    return; // keep waiting for THIS capture's own event
                }
                tcs.TrySetResult(e);
            }
            var subscription = guiderClient.OnFrameComplete(e => OnComplete(guiderClient, e));
            try {
                // The capture RPC gets the same one-failed-solve treatment as the solver call
                // below: a daemon-side rejection (camera busy, guiding running) or a dropped
                // guider is transient — it must feed the consecutive-failure/pause path (and the
                // seed retry loop), not hard-fail the routine (§45.11). The rejection text is kept
                // so an exhausted seed loop can say "the daemon refused the capture" instead of
                // blaming focus/sky (capture_rejected).
                try {
                    var exposureMs = Math.Max(1, (int)Math.Round(settings.ExposureSeconds * 1000.0));
                    await guiderClient.CaptureFrameAsync(exposureMs, binning > 1 ? binning : null, ct).ConfigureAwait(false);
                } catch (GuiderRpcException ex) {
                    RecordCaptureFault(gen, frameId, ex.Message);
                    return new PolarAlignSolveOutcome(false, 0, 0);
                } catch (InvalidOperationException ex) {
                    RecordCaptureFault(gen, frameId, ex.Message);
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
                SingleFrameCompleteEventArgs completed;
                try {
                    completed = await tcs.Task.WaitAsync(CaptureCompleteTimeout, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Stop/restart cancelled the wait AFTER the RPC ack — the daemon still owes this
                    // capture's event, and without the debt the NEXT run would adopt it as fresh
                    // data (review r2: the common abandonment path, not just the timeout). Unless
                    // this wait already swallowed one (see swallowedThisWait) — then the books are
                    // square and a second debt would cascade (review r3).
                    if (Volatile.Read(ref swallowedThisWait) == 0) {
                        RecordAbandonedCapture();
                    }
                    throw;
                }
                lock (_gate) {
                    if (_generation == gen) {
                        _framesCaptured++;
                        _lastFrameId = frameId;
                    }
                }
                var filename = completed.Filename ?? (completed.Path is { Length: > 0 } p ? Path.GetFileName(p) : null);
                if (!completed.Success || string.IsNullOrEmpty(filename)) {
                    RecordCaptureFault(gen, frameId, completed.Error ?? "no saved filename on SingleFrameComplete");
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
                // Fetch the daemon-saved FITS over the guider#77 HTTP capture endpoint into the run's
                // work dir — the only frame transport that works when the daemon runs on another
                // machine (and equally fine same-host). A failed download is one failed solve.
                var host = guiderClient.Host;
                if (string.IsNullOrEmpty(host)) {
                    RecordCaptureFault(gen, frameId, "guider connection endpoint unknown — cannot fetch the saved frame");
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
                try {
                    await _frameFetcher.FetchAsync(
                        host, guiderClient.RpcPort, filename, localPath, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    RecordCaptureFault(gen, frameId,
                        $"fetching the saved frame '{filename}' from the guider failed: {ex.Message} " +
                        "(daemon builds before openastro-guider#77 have no capture endpoint)");
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
                lock (_gate) {
                    if (_generation == gen) {
                        _lastCaptureFault = null; // the capture layer delivered — any failure now is a solve failure
                    }
                }
                timing.Capture = Stopwatch.GetElapsedTime(captureStart);
                var solveStart = Stopwatch.GetTimestamp();
                try {
                    var outcome = await _solver.SolveAsync(localPath, binning, hint?.RaDeg, hint?.DecDeg, ct).ConfigureAwait(false);
                    if (!outcome.Success && timing.KeepUnsolvedFrame) {
                        timing.KeptFramePath = localPath;
                    }
                    return outcome;
                } catch (OperationCanceledException) {
                    throw;
                } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException) {
                    throw; // user-fixable setup problem — fail the routine with the solver's message
                } catch (Exception ex) {
                    // A torn/partial FITS write or a solver crash on one frame is ONE failed solve.
                    LogCaptureFailed(frameId, ex.Message);
                    return new PolarAlignSolveOutcome(false, 0, 0);
                } finally {
                    timing.Solve = Stopwatch.GetElapsedTime(solveStart);
                }
            } catch (TimeoutException) {
                // The daemon may still deliver this capture's event later — the next capture's
                // handler must swallow it rather than adopt its (stale) frame. But if THIS wait
                // already swallowed an event, the likeliest story is that the swallow ate our own
                // completion (the earlier abandoned capture never delivered): the old debt is paid,
                // and enqueueing a new one would hand the mistake to the next capture in a chain
                // (review r3). No swallow → genuinely lost event → record the debt.
                if (Volatile.Read(ref swallowedThisWait) == 0) {
                    RecordAbandonedCapture();
                }
                RecordCaptureFault(gen, frameId, "SingleFrameComplete timed out");
                return new PolarAlignSolveOutcome(false, 0, 0);
            } finally {
                subscription.Dispose();
                if (timing.KeptFramePath != localPath) {
                    TryDeleteFile(localPath);
                }
                if (timing.Capture == TimeSpan.Zero) {
                    timing.Capture = Stopwatch.GetElapsedTime(captureStart); // never reached the solver
                }
            }
        }

        // §45 capture-fetch — downloads daemon-saved frames over the guider's HTTP capture endpoint.
        private readonly IPolarAlignFrameFetcher _frameFetcher;
        private readonly bool _ownsFetcher;

        // §45 capture-fetch — timestamps of ABANDONED captures (timed out OR cancelled by a
        // Stop/restart after the RPC ack) that still owe a SingleFrameComplete. Guarded by _gate.
        // Debts survive run boundaries by design (review r2: a Stop→Start abandons a capture whose
        // late event would otherwise be adopted by the NEW run as fresh data) and EXPIRE after
        // StaleDebtTtl so a daemon that never delivers can't starve future captures — worst case
        // one swallowed-then-retried capture per anomaly.
        private readonly Queue<DateTimeOffset> _abandonedCaptureDebts = new();
        internal TimeSpan StaleDebtTtl { get; set; } = TimeSpan.FromSeconds(60);

        private void RecordAbandonedCapture() {
            lock (_gate) {
                _abandonedCaptureDebts.Enqueue(DateTimeOffset.UtcNow);
            }
        }

        private bool TryConsumeStaleDebt() {
            lock (_gate) {
                var cutoff = DateTimeOffset.UtcNow - StaleDebtTtl;
                while (_abandonedCaptureDebts.Count > 0 && _abandonedCaptureDebts.Peek() < cutoff) {
                    _abandonedCaptureDebts.Dequeue(); // expired — the daemon evidently never delivered
                }
                if (_abandonedCaptureDebts.Count == 0) {
                    return false;
                }
                _abandonedCaptureDebts.Dequeue();
                return true;
            }
        }

        // §45 — the most recent CAPTURE-layer fault (RPC rejection, missing filename, failed fetch,
        // completion timeout) for the current run; null once a capture gets as far as the solver.
        // Lets an exhausted seed loop report capture_rejected with the daemon's actual message
        // instead of blaming focus/sky for a frame that was never taken.
        private string? _lastCaptureFault;

        private void RecordCaptureFault(int gen, string frameId, string message) {
            LogCaptureFailed(frameId, message);
            lock (_gate) {
                if (_generation == gen) {
                    _lastCaptureFault = message;
                }
            }
        }

        /// <summary>The mount's pointing read from the driver now (the daemon's telescope service), in JNOW and
        /// folded like <see cref="CurrentMountPointingJnow"/>; the cached reading when no fresh read is available. Never
        /// for a motor move's readings (<see cref="MoveAxisUntilAsync"/> reads the driver alone, #2089).</summary>
        private async Task<(double RaDeg, double DecDeg)?> FreshMountPointingJnowAsync(CancellationToken ct) =>
            await DriverPointingJnowAsync(ct).ConfigureAwait(false) ?? CurrentMountPointingJnow();

        /// <summary>#2048 — the mount's pointing from the driver now, never the daemon's cache: null when the driver gives
        /// no answer. The cache falls back to the (0, 0) placeholder after one failed status poll (an AlpacaBridge hiccup
        /// fails the poll and the fresh read together), and a band anchored there, or a stage's offset read from it, turns
        /// the mount a way the user never confirmed. The mediator's own reading in tests (no telescope service).</summary>
        private async Task<(double RaDeg, double DecDeg)?> DriverPointingJnowAsync(CancellationToken ct) {
            if (_mount is not ITelescopeService service) {
                return CurrentMountPointingJnow();
            }
            if (await service.ReadPointingNowAsync(ct).ConfigureAwait(false) is not { } now) {
                return null;
            }
            var frame = _mount.GetCurrentPosition()?.Epoch ?? Epoch.JNOW;
            return Folded(new Coordinates(Angle.ByHours(now.RaHours), Angle.ByDegree(now.DecDegrees), frame).Transform(Epoch.JNOW));
        }

        private (double RaDeg, double DecDeg)? CurrentMountPointingJnow() {
            var pos = _mount.GetCurrentPosition()?.Transform(Epoch.JNOW);
            if (pos is null) {
                return null;
            }
            return Folded(pos);
        }

        private static (double RaDeg, double DecDeg) Folded(Coordinates pos) {
            // #1536 — a synced mount can report Dec past ±90° (the Dec axis is over the pole). The same sky
            // point is (RA+180°, 180°−Dec); folding gives one reading per direction, so the seed's turns and
            // the mount-turn cross-check compare like with like.
            return Math.Abs(pos.Dec) > 90.0
                ? ((pos.RADegrees + 180.0) % 360.0, Math.CopySign(180.0, pos.Dec) - pos.Dec)
                : (pos.RADegrees, pos.Dec);
        }

        /// <param name="frameId">The frame these errors came from; null keeps the last captured frame's id.</param>
        private void SetErrors(int gen, double altErrArcmin, double azErrArcmin, string state, string? frameId) {
            lock (_gate) {
                if (_generation != gen) {
                    return; // a superseded run's late write is a no-op
                }
                _altErrorArcmin = altErrArcmin;
                _azErrorArcmin = azErrArcmin;
                _state = state;
                _lastFrameId = frameId ?? _lastFrameId;
            }
        }

        // The current-generation failure path: state → failed, WS error event, §45.13 session row,
        // lease released. The generation is re-checked ATOMICALLY with the write (the
        // `when (IsCurrent(gen))` filter alone leaves a TOCTOU window against a Start that bumps
        // the generation between the filter and this write — same discipline as SetErrors).
        // #2048: the state turns failed at once (a Stop meanwhile must not relabel it), then the mount is stopped and
        // confirmed (exitMount), and only then is the failure reported — with the cut-power sentence when the stop was not
        // confirmed.
        private async Task FailRoutineAsync(string reason, Exception ex, int gen, Func<Task<bool?>> exitMount) {
            LogRoutineFailed(reason, ex);
            bool claimed;
            lock (_gate) {
                claimed = _generation == gen; // superseded between the catch filter and here — the successor owns state
                if (claimed) {
                    _state = "failed";
                    _active = false;
                    // In the same section that clears _active: a sequence start reading IsActive between the two would
                    // otherwise see no run and go on while the exit below still sends its stop rounds and tracking write.
                    _exitsInFlight++;
                }
            }
            bool? mountStopped;
            try {
                mountStopped = await exitMount().ConfigureAwait(false);
            } finally {
                if (claimed) {
                    lock (_gate) {
                        _exitsInFlight--;
                    }
                }
            }
            if (!claimed) {
                return;
            }
            var message = mountStopped == false && reason != MountDidNotStop ? ex.Message + " " + CutPowerSentence : ex.Message;
            await PublishErrorAsync(reason, message).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("failed", CancellationToken.None).ConfigureAwait(false);
            await ReleaseLeaseBestEffortAsync().ConfigureAwait(false);
        }

        private static double Wrap180(double deg) => ((deg + 540.0) % 360.0) - 180.0;

        // §45.13 — one row per finished routine. Best-effort: a DB fault must not fail the unwind.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Session logging is best-effort record-keeping; the routine outcome must not depend on it. Log-and-recover boundary.")]
        private async Task WriteSessionRowBestEffortAsync(string outcome, CancellationToken ct) {
            if (_log is null) {
                return;
            }
            PolarAlignmentRecord record;
            lock (_gate) {
                // The last readout of any stage is a measured axis error: the coarse stage fits the axis
                // from its own seed turns (#1727), so a run stopped there records a real measurement too,
                // only one the user had not finished adjusting.
                var total = _altErrorArcmin is double alt && _azErrorArcmin is double az
                    ? (double?)Math.Sqrt(alt * alt + az * az)
                    : null;
                record = new PolarAlignmentRecord(
                    StartedAt: _startedAt,
                    EndedAt: DateTimeOffset.UtcNow,
                    FinalErrorArcmin: total,
                    FinalAltErrorArcmin: _altErrorArcmin,
                    FinalAzErrorArcmin: _azErrorArcmin,
                    Iterations: _liveIterations,
                    Outcome: outcome,
                    AlignerMoves: _alignerMoves);
            }
            LogFinalError(outcome, record.FinalErrorArcmin, record.FinalAltErrorArcmin, record.FinalAzErrorArcmin, record.Iterations);
            try {
                await _log.InsertAsync(record, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogSessionRowFailed(ex);
            }
        }

        // Stop cancelled the loop; the unwind is bounded by the loop's own timeouts, but Stop must
        // not hang forever behind a wedged capture — after a grace period the task is abandoned
        // (its finally still runs whenever the underlying call returns).
        /// <returns>False when the run did not unwind within the grace and was abandoned.</returns>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Stop must always complete; the run task's own boundary already logged any failure.")]
        private async Task<bool> AwaitRunUnwindAsync(Task run) {
            try {
                await run.WaitAsync(RunUnwindGrace).ConfigureAwait(false);
            } catch (TimeoutException) {
                LogRunUnwindTimedOut();
                return false;
            } catch (Exception) {
                // Already handled + logged at the run task's boundary.
            }
            return true;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Hand-back is best-effort on every exit path; a mount fault here must not mask the routine's outcome. Log-and-recover boundary.")]
        private void RestoreTrackingBestEffort(bool? priorTracking) {
            if (priorTracking is not bool tracking) {
                return;
            }
            if (_mount.TrackingWriteCount != _ownTrackingWrites) {
                LogTrackingLeftToOtherCaller();
                return;
            }
            try {
                _mount.SetTrackingEnabled(tracking);
            } catch (Exception ex) {
                LogTrackingRestoreFailed(ex);
            }
        }

        // Releasing the camera must not fail Stop: the custody module logs a release fault (the guider may
        // already be disconnected, and the lease auto-expires).
        private async Task ReleaseLeaseBestEffortAsync() {
            GuideCameraCustody.Claim? claim;
            lock (_gate) {
                claim = _claim;
                _claim = null;
            }
            if (claim is not null) {
                await claim.ReleaseAsync().ConfigureAwait(false);
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Temp-file cleanup is best-effort; the OS temp dir is reclaimed eventually regardless.")]
        private static void TryDeleteFile(string path) {
            try {
                File.Delete(path);
            } catch (Exception) {
                // best-effort
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Temp-dir cleanup is best-effort; the OS temp dir is reclaimed eventually regardless.")]
        private static void TryDeleteWorkDir(string workDir) {
            try {
                if (Directory.Exists(workDir)) {
                    Directory.Delete(workDir, recursive: true);
                }
            } catch (Exception) {
                // best-effort
            }
        }

        // ── WS events ────────────────────────────────────────────────────────────────────────

        private Task PublishStateEventAsync(WsEvent eventType, string state) =>
            PublishAsync(eventType, new JsonObject { ["state"] = state });

        // The in-loop publishes are generation-fenced like the state writes: a superseded run's
        // last iteration completing must not emit a stray progress/frame event after the client
        // was already told the routine stopped.
        private Task PublishProgressAsync(int gen, int iteration, double altErrArcmin, double azErrArcmin, bool solved) {
            if (!IsCurrent(gen)) {
                return Task.CompletedTask;
            }
            var total = Math.Sqrt(altErrArcmin * altErrArcmin + azErrArcmin * azErrArcmin);
            string? stage;
            string? caution;
            lock (_gate) {
                stage = _stage;
                caution = _readoutCaution;
            }
            return PublishAsync(WsEventCatalog.PolarAlignProgress, new JsonObject {
                ["stage"] = stage,
                ["readout_caution"] = caution,
                ["iteration"] = iteration,
                ["altitude_error_arcmin"] = Math.Round(altErrArcmin, 2),
                ["azimuth_error_arcmin"] = Math.Round(azErrArcmin, 2),
                ["total_error_arcmin"] = Math.Round(total, 2),
                ["zone"] = total < 10.0 ? "green" : total < 60.0 ? "yellow" : "red",
                ["solved"] = solved,
            });
        }

        private Task PublishFrameStartedAsync(int gen, string frameId, double exposureSeconds) =>
            !IsCurrent(gen) ? Task.CompletedTask :
            PublishAsync(WsEventCatalog.PolarAlignFrameStarted, new JsonObject {
                ["frame_id"] = frameId,
                ["exposure_seconds"] = exposureSeconds,
            });

        // Records the frame for the status snapshot and publishes frame_complete with its timings
        // and (when solved) the solved pointing.
        private Task CompleteFrameAsync(
                int gen, string frameId, PolarAlignSolveOutcome s, FrameTiming timing, int consecutiveSolveFailures) {
            var frame = new PolarAlignLastFrameDto(
                FrameId: frameId,
                Solved: s.Success,
                ExposureSeconds: timing.ExposureSeconds,
                CaptureMs: (int)Math.Round(timing.Capture.TotalMilliseconds),
                SolveMs: timing.Solve is TimeSpan solve ? (int)Math.Round(solve.TotalMilliseconds) : null,
                RaDeg: s.Success ? s.RaDegJnow : null,
                DecDeg: s.Success ? s.DecDegJnow : null,
                CompletedAt: DateTimeOffset.UtcNow);
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                _lastFrame = frame;
            }
            var payload = new JsonObject {
                ["frame_id"] = frameId,
                ["solved"] = s.Success,
                ["consecutive_solve_failures"] = consecutiveSolveFailures,
                ["exposure_seconds"] = frame.ExposureSeconds,
                ["capture_ms"] = frame.CaptureMs,
            };
            if (frame.SolveMs is int solveMs) {
                payload["solve_ms"] = solveMs;
            }
            if (s.Success) {
                payload["ra_deg"] = Math.Round(s.RaDegJnow, 5);
                payload["dec_deg"] = Math.Round(s.DecDegJnow, 5);
            }
            return PublishAsync(WsEventCatalog.PolarAlignFrameComplete, payload);
        }

        private Task PublishErrorAsync(string reason, string message) =>
            PublishAsync(WsEventCatalog.PolarAlignError, new JsonObject {
                ["reason"] = reason,
                ["message"] = message,
            });

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "WS publish is best-effort; a broadcaster fault must not abort the routine. Log-and-recover boundary.")]
        private async Task PublishAsync(WsEvent eventType, JsonObject payload) {
            if (_ws is null) {
                return;
            }
            try {
                using var doc = JsonDocument.Parse(payload.ToJsonString());
                await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogWsPublishFailed(ex);
            }
        }

        private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
            new(OperationId: Guid.NewGuid(),
                OperationType: operationType,
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey);

        [LoggerMessage(EventId = 4527, Level = LogLevel.Information, Message = "§45 polar-align {Frame} solved at RA {RaDeg:F4}° Dec {DecDeg:F4}° (JNOW)")]
        private partial void LogSeedSolve(string frame, double raDeg, double decDeg);

        [LoggerMessage(EventId = 4528, Level = LogLevel.Information, Message = "§45 polar-align seed fit: mount RA turn {RotationDeg:F3}° -> axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; seed error alt {AltArcmin:F2}′ az {AzArcmin:F2}′")]
        private partial void LogSeedFit(double rotationDeg, double axisRaDeg, double axisDecDeg, double altArcmin, double azArcmin);

        [LoggerMessage(EventId = 4529, Level = LogLevel.Information, Message = "§45 polar-align live readout {FrameId}: alt {AltArcmin:F2}′ az {AzArcmin:F2}′ (solve JNOW RA {RaDeg:F5}° Dec {DecDeg:F5}°, tracking {Tracking})")]
        private partial void LogLiveReadout(string? frameId, double altArcmin, double azArcmin, double raDeg, double decDeg, bool tracking);

        [LoggerMessage(EventId = 4530, Level = LogLevel.Information, Message = "§45 polar-align finished ({Outcome}): final error {TotalArcmin}′ (alt {AltArcmin}′ az {AzArcmin}′) after {Iterations} live iterations")]
        private partial void LogFinalError(string outcome, double? totalArcmin, double? altArcmin, double? azArcmin, int iterations);

        [LoggerMessage(EventId = 4510, Level = LogLevel.Information, Message = "§45 polar-align routine started at the {Stage} stage (PA-session lease acquired)")]
        private partial void LogStarted(string stage);

        [LoggerMessage(EventId = 4511, Level = LogLevel.Information, Message = "§45 polar-align routine stopped (PA-session lease released)")]
        private partial void LogStopped();


        [LoggerMessage(EventId = 4513, Level = LogLevel.Warning, Message = "§45 WS publish of a polar-align event failed (best-effort)")]
        private partial void LogWsPublishFailed(Exception exception);

        [LoggerMessage(EventId = 4514, Level = LogLevel.Error, Message = "§45 polar-align routine failed: {Reason}")]
        private partial void LogRoutineFailed(string reason, Exception exception);

        [LoggerMessage(EventId = 4541, Level = LogLevel.Information, Message = "§45 polar-align: a start that named no stage begins at the fine stage — the coarse stage cannot run: {Reason}")]
        private partial void LogCoarseSkipped(string reason);

        [LoggerMessage(EventId = 4515, Level = LogLevel.Warning, Message = "§45 polar-align capture {FrameId} failed: {Error}")]
        private partial void LogCaptureFailed(string frameId, string error);

        [LoggerMessage(EventId = 4516, Level = LogLevel.Warning, Message = "§45 polar-align live loop paused after {Failures} consecutive failed solves (still retrying)")]
        private partial void LogLoopPaused(int failures);

        [LoggerMessage(EventId = 4517, Level = LogLevel.Warning, Message = "§45 polar-align tracking restore failed on hand-back (best-effort)")]
        private partial void LogTrackingRestoreFailed(Exception exception);

        [LoggerMessage(EventId = 4542, Level = LogLevel.Information, Message = "§45 polar-align left tracking as another caller set it (no hand-back)")]
        private partial void LogTrackingLeftToOtherCaller();

        [LoggerMessage(EventId = 4518, Level = LogLevel.Warning, Message = "§45 polar-align run task did not unwind within the stop grace period; abandoning (its cleanup still runs on return)")]
        private partial void LogRunUnwindTimedOut();

        [LoggerMessage(EventId = 4519, Level = LogLevel.Warning, Message = "§45.13 polar-alignment session row insert failed (best-effort)")]
        private partial void LogSessionRowFailed(Exception exception);


        [LoggerMessage(EventId = 4521, Level = LogLevel.Information, Message = "§45 live frame unavailable: {Reason}")]
        private partial void LogLiveFrameUnavailable(string reason);

        [LoggerMessage(EventId = 4522, Level = LogLevel.Information, Message = "§45 live frame fetch from the guider failed")]
        private partial void LogLiveFrameFetchFailed(Exception exception);

        [LoggerMessage(EventId = 4524, Level = LogLevel.Information, Message = "§45.4 polar-align binning {Binning}×{Binning} ({Source}; guide camera frame {Width}×{Height})")]
        private partial void LogBinningChosen(int binning, string source, int width, int height);

        [LoggerMessage(EventId = 4525, Level = LogLevel.Information, Message = "§45.4 the guider did not report the guide camera's frame size — auto binning stays at 1×1: {Reason}")]
        private partial void LogFrameSizeUnavailable(string reason);

        [LoggerMessage(EventId = 4531, Level = LogLevel.Information, Message = "§45 polar-align turned tracking on for the seed turns (restored on exit)")]
        private partial void LogTrackingEnabled();

        [LoggerMessage(EventId = 4532, Level = LogLevel.Warning, Message = "§45 polar-align could not turn tracking on")]
        private partial void LogTrackingEnableFailed(Exception exception);

        [LoggerMessage(EventId = 4533, Level = LogLevel.Warning, Message = "§45 polar-align tracking dropped before a seed turn — turning it back on")]
        private partial void LogTrackingDropped();

        [LoggerMessage(EventId = 4568, Level = LogLevel.Warning, Message = "§45 polar-align start refused: not confirmed (confirmed direction {Confirmed}, the run would turn {Direction})")]
        private partial void LogStartNotConfirmed(string confirmed, string direction);

        [LoggerMessage(EventId = 4569, Level = LogLevel.Information, Message = "§45 polar-align start confirmed by the user: from the current pointing, RA turns {StepDeg:F0}° twice toward the {Direction}, at most {MaxSweepDeg:F0}° from the start (no Home, no GoTo)")]
        private partial void LogStartConfirmed(string direction, double stepDeg, double maxSweepDeg);
    }
}