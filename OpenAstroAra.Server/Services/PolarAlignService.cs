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
using System.Linq;
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
    /// <para><b>Stages</b> (<see cref="PolarAlignStages"/>, the partial in PolarAlignService.Stages.cs): one measurement
    /// per Start (#2082) — <c>fine</c> (the default: the seed-and-adjust routine below on the guide camera), <c>coarse</c>
    /// (the same routine on the main camera — slower frames, for the big moves), <c>check</c> (three guide-camera solves
    /// with two RA turns, an exact three-point fit, state <c>checked</c>). A measurement never leads into another: Next
    /// is retired (409), and a check is its own confirmed Start (from the adjust loop it ends the loop first).</para>
    ///
    /// <para><b>Start</b> (#2048, NINA's Three Point Polar Alignment): the routine starts from where the user pointed the
    /// mount (tube near the pole, counterweights down). It never sends the mount Home, never slews it and never unparks
    /// it: a parked mount is refused, and the mount moves only after the user confirmed the start and the RA turn
    /// direction (<see cref="GetStartPlanAsync"/>). Every RA turn of the run stays inside the band the user confirmed:
    /// from the start pose up to <see cref="PolarAlignStartPlanDto.MaxSweepDeg"/> the way the confirm step showed (#2081:
    /// <c>polar_align.east_direction</c> at a new sweep origin, back toward it after).</para>
    ///
    /// <para><b>Fine-stage state machine</b> (the <c>state</c> string in <see cref="PolarAlignStateDto"/>):
    /// <c>idle</c> → Start → <c>seeding</c> (the 0.5° RA motor-direction test when none is saved for the mount's driver,
    /// #2081 → frame A → RA-motor turn Δ the confirmed way → frame B →
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
        // #2066 (ARA-5) — the seam every motor move, abort, stop count and fresh reading goes through (the daemon's
        // telescope service): the routine never casts its mount to the service, and never plans or moves from the cache.
        private readonly IMountMotionControl _motionControl;
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
        /// <summary>The clock <see cref="RunUnwindGrace"/> runs on: a test ends the grace when its scenario is in place.</summary>
        internal TimeProvider RunUnwindClock { get; set; } = TimeProvider.System;
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

        // Stages (PolarAlignStages): which one this run measures (#2082: one per Start).
        private string? _stage;
        // #2082 — the camera this run measures with (PolarAlignCameras), and whether a point of its measurement stayed
        // scattered after its retake. Guarded by _gate.
        private string? _camera;
        private bool _measurementNoisy;
        private int _checkSolvesDone;
        private PolarAlignCheckResultDto? _checkResult;
        // #2082 — the last run's readout (any measurement that ended with one: Stop, Done, a failure, a check's verdict), both
        // components; a check derives the total it compares with and the distance to its own axis. In memory only, like
        // _lastResult. Guarded by _gate.
        private double? _previousRunAltArcmin;
        private double? _previousRunAzArcmin;
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
        // #2071 — the routine homes and slews against the mount's sidereal time: refuse to start while its clock is not verified.
        private readonly IMountClockGate? _mountClock;
        // This routine's hold on the Guide Camera (ADR 0016); null when it holds none. Guarded by _gate.
        private GuideCameraCustody.Claim? _claim;

        internal PolarAlignService(
                IGuideCapture guider,
                ILogger<PolarAlignService> logger,
                IPolarAlignFrameSolver solver,
                ITelescopeMediator mount,
                IMountMotionControl motion,
                IProfileStore profileStore,
                IWsBroadcaster? ws = null,
                IPolarAlignmentLog? log = null,
                IPolarAlignFrameFetcher? frameFetcher = null,
                IPolarAlignMainCameraSolver? mainCamera = null,
                IPolarAlignerSource? aligner = null,
                IMountClockGate? mountClock = null) {
            _guider = guider;
            _mountClock = mountClock;
            _alignerSource = aligner;
            _mainCamera = mainCamera;
            _logger = logger;
            _solver = solver;
            _mount = mount;
            _motionControl = motion;
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
                    // #2081 — the seed's Dec-spread warning describes the live readout too: shown only while one runs.
                    DecSpreadArcsec: _state is "adjusting" or "paused" ? _decSpreadArcsec : null,
                    Stage: _stage,
                    WaitingForHome: false,
                    CheckSolvesDone: _checkSolvesDone,
                    CheckResult: _checkResult,
                    LastResult: _lastResult,
                    Correction: _aligner,
                    // #2072 — the caution describes a live readout: shown only while one runs.
                    ReadoutCaution: _state is "adjusting" or "paused" ? _readoutCaution : null,
                    Camera: _camera,
                    MeasurementNoisy: _measurementNoisy,
                    // #2072 — the readout's 1σ, beside each error, at the 0.01′ polar_align.progress gives it.
                    CurrentSigmaArcmin: total is null ? null : RoundedSigma(_fitSigma?.TotalSigmaArcmin),
                    AzimuthSigmaArcmin: total is null ? null : RoundedSigma(_fitSigma?.AzSigmaArcmin),
                    AltitudeSigmaArcmin: total is null ? null : RoundedSigma(_fitSigma?.AltSigmaArcmin)));
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

        // Caller holds _gate. #2082 — the readout the next check compares with: this run's last one, whatever ended it.
        private void RememberRunReadoutLocked() {
            if (_altErrorArcmin is double alt && _azErrorArcmin is double az) {
                _previousRunAltArcmin = alt;
                _previousRunAzArcmin = az;
            }
        }

        /// <summary>Proxies the guider daemon's live-frame JPEG so a client on any network sees the
        /// frames polar align is solving (the daemon may sit on an address only this host reaches).
        /// Null when no guider is connected or the fetch fails — a missing preview is never an error.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The preview is best-effort; any fetch failure just means no image this time.")]
        public async Task<byte[]?> GetLiveFrameJpegAsync(CancellationToken ct) {
            lock (_gate) {
                if (_active && _camera == PolarAlignCameras.Main) {
                    // #2082 — this run shoots the main camera: the guider's last frame is an older one, not this run's.
                    return null;
                }
            }
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

        /// <summary>#2082 — Next is retired: a Start makes one measurement and never leads into another, so Next always
        /// answers 409. Check is its own confirmed Start (<see cref="StartAsync"/> with <c>check</c>).</summary>
        internal const string NextRetiredMessage =
            "Polar Align makes one measurement per Start and has no next step — tap Check for a new measurement, or Start again";

        /// <inheritdoc />
        public Task<OperationAcceptedDto> NextAsync(string? idempotencyKey, CancellationToken ct) =>
            Task.FromException<OperationAcceptedDto>(new InvalidOperationException(NextRetiredMessage));

        /// <summary>The parked refusal's sentence (#2048): Polar Align never unparks the mount.</summary>
        internal const string MountParkedMessage = "Mount is parked — unpark it and point near the pole first";

        /// <summary>#2048 — the run's RA turns: the way the user confirmed (+1 east, −1 west in RA-reading terms) and how far
        /// they take the axis (this measurement's two turns), always with the mount's motor (#2170).
        /// <para>#2081 — <c>RaMotorSign</c>: the saved RA motor direction for the mount's <c>Driver</c>, or null when none
        /// stands for it and the run tests it first (<see cref="TestRaMotorDirectionIfOwedAsync"/>).</para></summary>
        /// <para><c>ProfileId</c>: the profile active when the run was planned; the test's sign is saved only while it still
        /// is (a profile switch swaps the store, so the sign would land on a profile the test never ran for).</para>
        private sealed record RunBand(double AwaySign, double MaxSweepDeg, double? RaMotorSign = null,
            MountDriverKey? Driver = null, Guid? ProfileId = null);

        /// <summary>The plan of a start at <paramref name="stage"/> from the mount's pointing read now, with the turns the
        /// run makes and their place against the session's sweep origin (#2089, <see cref="PlanSweep"/>). At a new origin
        /// the first turn goes the profile's <c>polar_align.east_direction</c> (#2081); a later start turns back toward the
        /// origin, and one whose turns would go past the limit is refused. A mount with no saved RA motor direction for its
        /// driver gets the 0.5° direction test first (<see cref="PolarAlignStartPlanDto.DirectionTestDeg"/>). A pointing within 3° of the
        /// pole is refused (#2082, <c>polar-align-on-axis</c>): nothing tilts the camera off the axis any more.</summary>
        private async Task<(PolarAlignStartPlanDto Plan, RunBand Band, SweepPlan Sweep)> PlanStartAsync(
                string? stage, PolarAlignSettingsDto settings, CancellationToken ct) {
            // From the driver, never the cache: the direction the user confirms and the band every later turn keeps to
            // are both read here. #2066 — a reading that cannot be trusted (none, the placeholder, too old) or one past the
            // pole is refused before anything moves.
            var (raDeg, decDeg, session, side, slewing) = await StartPointingJnowAsync(ct).ConfigureAwait(false);
            // #2066 (ARA-2) — a mount that slews, or a guider that guides, would move beside Polar Align's turns.
            ThrowIfBusyAtStart(slewing);
            if (Math.Abs(decDeg) > OnAxisDecDeg) {
                throw new RefusalException(RefusalCatalog.PolarAlignOnAxis, OnAxisMessage);
            }
            // #2082 — one measurement per Start: its own two turns, nothing after them.
            var stepDeg = stage == PolarAlignStages.Check ? settings.CheckStepDeg : settings.SeedRotationDeg;
            var maxSweepDeg = 2.0 * stepDeg;
            // #2081 — the saved RA motor direction for this driver, or the 0.5° test that learns it (inside the sweep limit).
            var driver = ReadMountDriver();
            var savedSign = SavedRaMotorSign(driver);
            var testDeg = savedSign is null ? DirectionTestDeg : 0.0;
            var newOriginSign = PolarAlignEastDirection.Sign(settings.EastDirection, _profileStore.GetSiteSettings().LatitudeDeg);
            var sweep = PlanSweep(raDeg, decDeg, maxSweepDeg, session, side, newOriginSign, testDeg);
            var awaySign = sweep.DirSign;
            var plan = new PolarAlignStartPlanDto(stage, awaySign > 0 ? PolarAlignDirections.East : PolarAlignDirections.West,
                stepDeg, Turns: 2, maxSweepDeg, sweep.NewOrigin, Math.Round(sweep.OffsetDeg, 1), sweep.LimitDeg,
                CameraFor(stage ?? PolarAlignStages.Fine, settings), DirectionTestDeg: testDeg);
            return (plan, new RunBand(awaySign, maxSweepDeg, RaMotorSign: savedSign, Driver: driver, ProfileId: ActiveProfileId?.Invoke()), sweep);
        }

        /// <summary>#2082 — the camera a start at <paramref name="stage"/> measures with: the main camera for the coarse stage
        /// ("Rough first (main camera)", offered after the guide camera could not solve the pole field), else the
        /// profile's <c>polar_align.camera</c> — for the fine measurement, its live loop and the check alike.</summary>
        internal static string CameraFor(string stage, PolarAlignSettingsDto settings) =>
            stage == PolarAlignStages.Coarse ? PolarAlignCameras.Main : settings.Camera;

        /// <inheritdoc />
        public async Task<PolarAlignStartPlanDto> GetStartPlanAsync(string? stage, CancellationToken ct) {
            if (stage is not null && !PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
            }
            // #2068 — the same pre-flight list as Start, refused on its first failing check. #2066: an unverified position
            // comes before the start pointing, so a false Home at the pole after a power cycle names the unconfirmed stop,
            // not the on-axis refusal (#2082). A start that names no stage is the fine measurement; the plan echoes it.
            // #2071 — like Start, the plan waits (up to about 10 s) for a mount clock push in flight: the app reads the
            // plan before Start, so a Start tapped just after connect is not refused by the push it would have waited for.
            if (_mountClock is { } clockGate) {
                await clockGate.MountClockRefusalAfterPushAsync(ct).ConfigureAwait(false);
            }
            var preflight = await EvaluatePreflightAsync(stage ?? PolarAlignStages.Fine, ct).ConfigureAwait(false);
            ThrowOnFirstFailure(preflight);
            return StartPlanOf(preflight).Plan with { Stage = stage };
        }

        /// <summary>
        /// Begin the routine at <paramref name="stage"/>: <c>fine</c> (default) → the guide-camera seed and live loop;
        /// <c>coarse</c> → the main-camera seed and live loop; <c>check</c> → the three-point measurement only. One
        /// measurement per Start (#2082); each starts from the current pointing (#2048: no Home, no GoTo). Preflight
        /// (connected guider, connected and unparked mount, a configured site latitude, a pointing 3° or more from the
        /// pole, the user's <paramref name="confirmation"/> of the turn direction), then the guide-camera PA-session lease,
        /// then the run on a background task. Idempotent — Start on an already-running routine is a no-op accept, except a
        /// <c>check</c> while the routine adjusts: the Check button of the adjust screen, which checks the mount's clock and
        /// plans and confirms the check first (those refusals leave the loop running), then ends the loop like Stop and
        /// starts the check. A mount clock push in flight is waited for first, as a GoTo waits (#2071). Throws
        /// <see cref="InvalidOperationException"/> on a failed preflight (409 by the endpoint; a <see cref="RefusalException"/>
        /// carries its cause). The lease is taken through the custody module before the guider's capture idles and its loop
        /// stops; when either fails, the lease is given back.
        /// </summary>
        public async Task<OperationAcceptedDto> StartAsync(
                string? stage, PolarAlignStartConfirmation? confirmation, string? idempotencyKey, CancellationToken ct) {
            stage ??= PolarAlignStages.Fine;
            if (!PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
            }
            if (stage != PolarAlignStages.Check) {
                return await StartRunAsync(stage, confirmation, idempotencyKey, handOver: false, ct).ConfigureAwait(false);
            }
            // #2082 — one Check takes the adjust loop's hand-over. A second one while it is under way (a double tap) is a
            // no-op accept, as a Start on a running routine is: a second end of the loop would fail both.
            bool handOver;
            lock (_gate) {
                if (_checkHandOver) {
                    return Accepted("polar-align.start", idempotencyKey);
                }
                handOver = _checkHandOver = IsAdjusting();
            }
            if (!handOver) {
                return await StartRunAsync(stage, confirmation, idempotencyKey, handOver: false, ct).ConfigureAwait(false);
            }
            try {
                return await StartRunAsync(stage, confirmation, idempotencyKey, handOver: true, ct).ConfigureAwait(false);
            } finally {
                lock (_gate) {
                    _checkHandOver = false;
                }
            }
        }

        /// <summary>A Check takes the adjust loop's hand-over (<see cref="EndAdjustForCheckAsync"/>); guarded by
        /// <c>_gate</c>.</summary>
        private bool _checkHandOver;

        /// <param name="handOver">This Start is the Check that ends the adjust loop first.</param>
        private async Task<OperationAcceptedDto> StartRunAsync(
                string stage, PolarAlignStartConfirmation? confirmation, string? idempotencyKey, bool handOver, CancellationToken ct) {
            // #2066 — the run's one reading of the mount's stop count, taken at the call like a REST GoTo's: a Stop Mount,
            // Emergency Stop, park or Find Home from here on ends the run before it moves (ThrowIfStoppedFromOutside).
            var abortEpochAtCall = _motionControl.AbortEpoch;
            var ownAbortsAtCall = Interlocked.Read(ref _ownAborts);
            // #2048 — an end request from here on (a Stop while this Start waits for the lock or takes the camera, or
            // while a Check ends the adjust loop) means no run is launched. Read at the call, before that end.
            long endRequestsAtCall;
            lock (_gate) {
                endRequestsAtCall = _endRequests;
            }
            // #2071 — like a GoTo, a Start just after the app connects waits (up to about 10 s) for the mount clock push in
            // flight; the gate is read again below, after the preflight. Before the lock: a Stop must not wait behind it.
            if (_mountClock is { } clockGate) {
                await clockGate.MountClockRefusalAfterPushAsync(ct).ConfigureAwait(false);
            }
            if (handOver) {
                var adjustWatch = await EndAdjustForCheckAsync(confirmation, ct).ConfigureAwait(false);
                // The loop's end is the one end request this Start makes itself; any other since the call still counts.
                endRequestsAtCall++;
                if (adjustWatch is { } watch) {
                    // The check from the adjust loop keeps the loop's reading, taken at its Start: a stop from outside that
                    // the loop had not seen yet (Emergency Stop latched while it solved a frame) ends the check before it
                    // moves, as it ended the stage that Next used to lead into.
                    (abortEpochAtCall, ownAbortsAtCall) = watch;
                }
            }
            // The reading the run keeps (_abortWatch). Re-read at the launch when only Polar Align's own stops moved the
            // count since the call: a Start pressed right after a failed turn must not inherit the old run's resent
            // AbortSlew, or the telescope service would refuse the new run's first motor command.
            (long Epoch, long OwnAborts) RunAbortBaseline() {
                var epochNow = _motionControl.AbortEpoch;
                var ownNow = Interlocked.Read(ref _ownAborts);
                return epochNow - abortEpochAtCall <= ownNow - ownAbortsAtCall
                    ? (epochNow, ownNow)
                    : (abortEpochAtCall, ownAbortsAtCall);
            }
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            IDisposable? startHold = null;
            try {
                lock (_gate) {
                    if (_active) {
                        // A retry of the same start is a no-op accept.
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
                RunEnd? priorEnd;
                lock (_gate) {
                    priorEnd = _liveRun?.End;
                    _liveRun = null; // that run ended by itself (failed or checked): its exit stopped the mount
                }
                if (priorRun is not null) {
                    await AwaitRunUnwindAsync(priorRun).ConfigureAwait(false);
                }
                priorEnd?.ReleaseHold(); // #2066 — a run that never unwound gives the mount's motion owner back here
                priorCts?.Dispose();
                var guiderClient = _guider.ConnectedLink();
                // #2066 (ARA-2) — the mount's motion owner for the whole run, the adjust loop included: a GoTo, Center,
                // Park or Home is refused from here until the run's exit has stopped the mount (Joey, 2026-11-20 on #2048).
                // Taken before the start reading below (the pre-flight list's plan): a GoTo the daemon sent before is seen on
                // it (slewing, or where it ended), and none is sent after it, so the turns and the sweep limit start from the
                // pose read here. Given back (the catch below) when the start fails before the run takes it over. Its refusal
                // waits for the list: the checks before the clearance refuse first (ThrowOnFirstFailure).
                RefusalException? holdRefusal = null;
                try {
                    startHold = AcquireMountForRun();
                } catch (RefusalException ex) {
                    holdRefusal = ex;
                }
                // #2068 (ARA-18) — the pre-flight list the Start card shows, refused on its first failing check: the
                // mount connected and not parked, the site, #2071's mount clock, #2070's position (a false zero after a
                // power loss made the routine slew below the pole, RC51 2026-11-19; the Find Home, slew and motor moves
                // are guarded again in the mount, so a reconnect mid-run cannot home or turn by the model), the motion
                // owner (#2066), the mount's clearance (no motion until it is confirmed), and the start pointing.
                var preflight = await EvaluatePreflightAsync(stage, ct).ConfigureAwait(false);
                ThrowOnFirstFailure(preflight, holdRefusal);
                var site = _profileStore.GetSiteSettings();
                var settings = preflight.Settings;
                // #2048 — nothing moves without the user's confirmation of this start and its RA turn direction, read from
                // the mount now: a direction that changed since the user saw it (the hour angle crossed the meridian)
                // is asked again. NINA asks nothing, but it starts from a GoTo of its own; Ara starts where the user
                // pointed the mount, so the user has to see where it will turn.
                var (plan, band, sweep) = StartPlanOf(preflight);
                ThrowIfNotConfirmed(confirmation, plan);
                var mainCamera = _mainCamera;
                var camera = plan.Camera;
                if (camera == PolarAlignCameras.Main) {
                    // Before the mount moves: a camera that is off would otherwise turn the mount and then
                    // fail the seed as a sky problem. The rough step, and every measurement with polar_align.camera = main.
                    var mainUnavailable = mainCamera is null
                        ? "a main-camera measurement needs the main camera's plate solver, which this daemon has not wired up — measure with the guide camera instead"
                        : await mainCamera.UnavailableReasonAsync(ct).ConfigureAwait(false);
                    if (mainUnavailable is not null) {
                        throw new InvalidOperationException(mainUnavailable);
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
                runEnd.Hold(startHold!); // taken above: a hold that could not be taken refused the start in ThrowOnFirstFailure
                startHold = null; // the run's now: its exit (or the end that drains it) gives it back
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
                        _camera = camera;
                        _measurementNoisy = false;
                        _checkSolvesDone = 0;
                        _checkResult = null;
                        _readoutCaution = null;
                        _fitSigma = null;
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
                        runEnd.Fence = StopFence; // #2066 — before the run is published; a fence from here on is this run's too
                        _runFence = (gen, runEnd.Fence);
                        var (baselineEpoch, baselineOwn) = RunAbortBaseline();
                        runEnd.MountSession = _motionControl.SessionEpoch; // #2066 — a change while the run runs leaves the pose unverified
                        _abortWatch = (gen, baselineEpoch, baselineOwn, runEnd);
                        _runSession = (gen, sweep.SessionEpoch); // #2066 — a reading of another mount session fails the run
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
                    runEnd.ReleaseHold();
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
                await PublishAsync(WsEventCatalog.PolarAlignStarted, new JsonObject {
                    ["state"] = initialState, ["stage"] = stage, ["camera"] = camera,
                }).ConfigureAwait(false);
                _runTask = Task.Run(() => RunRoutineAsync(guiderClient, mainCamera, site, settings, stage, camera, band, gen, startSeq, runEnd, runToken), CancellationToken.None);
                return Accepted("polar-align.start", idempotencyKey);
            } catch {
                startHold?.Dispose(); // #2066 — a start that failed before its run took the mount gives it back
                throw;
            } finally {
                _opLock.Release();
            }
        }

        /// <summary>#2048, #2089 — nothing moves without the user's confirmation of this start and its RA turn direction,
        /// read from the mount now: a direction that changed since the user saw it (the hour angle crossed the meridian)
        /// is asked again, and so is a pose that changed between a new start and a continued one. #2081 — a motor start
        /// whose plan has the 0.5° motor-direction test the user did not see (or no longer has the one they saw) too: every
        /// move a run makes is shown before it moves.</summary>
        private void ThrowIfNotConfirmed(PolarAlignStartConfirmation? confirmation, PolarAlignStartPlanDto plan) {
            var testOwed = plan.DirectionTestDeg > 0;
            var testSeen = confirmation is null || confirmation.DirectionTest == testOwed;
            if (confirmation is not null && string.Equals(confirmation.Direction, plan.Direction, StringComparison.Ordinal)
                    && confirmation.NewOrigin == plan.NewOrigin && testSeen) {
                return;
            }
            LogStartNotConfirmed(confirmation?.Direction ?? "none", plan.Direction);
            var test = testOwed
                ? string.Create(CultureInfo.InvariantCulture, $"; first it turns ~{plan.DirectionTestDeg:F1}° to learn the RA motor's direction")
                : string.Empty;
            throw new RefusalException(RefusalCatalog.PolarAlignUnconfirmed,
                confirmation is null
                    ? $"confirm the start first: tube near the pole, counterweights down; RA will turn ~{plan.StepDeg:F0}° twice toward the {plan.Direction}{test}; keep a hand on the mount's power"
                    : confirmation.NewOrigin != plan.NewOrigin
                        ? "the mount moved since you confirmed the start, or Polar Align's start pose changed — confirm the start again"
                        : !string.Equals(confirmation.Direction, plan.Direction, StringComparison.Ordinal)
                            ? $"the RA turn direction changed since you confirmed it (it is {plan.Direction} now) — confirm the start again"
                            : testOwed
                                ? string.Create(CultureInfo.InvariantCulture, $"this start first turns the mount ~{plan.DirectionTestDeg:F1}° to learn its RA motor's direction, which the step you confirmed did not show — confirm the start again")
                                : "the RA motor's direction is saved now, so this start makes no direction test — confirm the start again",
                new Dictionary<string, object?> { ["direction"] = plan.Direction });
        }

        /// <summary>The run is in its live adjust loop (adjusting, or paused on failed solves).</summary>
        private bool IsAdjusting() {
            lock (_gate) {
                return _active && _state is "adjusting" or "paused";
            }
        }

        /// <summary>#2082 — the Check button of the adjust screen: a confirmed Start(check) through the session's sweep plan
        /// (<see cref="PlanSweep"/>), so the 30° limit holds across repeated Checks. The mount's clock is checked, the check
        /// planned and its confirmation verified while the loop still runs (any of those refusals leaves it running), then
        /// the loop ends like Stop (the mount stopped and confirmed, its readout kept as the last run's) before the check
        /// starts. It makes exactly one end request (<see cref="StartAsync"/> allows that one and no other).</summary>
        /// <returns>The loop's reading of the mount's stop count (<c>_abortWatch</c>), for the check to keep; null when the
        /// loop had none (a mount that keeps no count).</returns>
        private async Task<(long Epoch, long OwnAborts)?> EndAdjustForCheckAsync(PolarAlignStartConfirmation? confirmation, CancellationToken ct) {
            // #2068 — the check's pre-flight list (#2071's mount clock among it): a refused Check leaves the loop running,
            // the mount untouched.
            var preflight = await EvaluatePreflightAsync(PolarAlignStages.Check, ct).ConfigureAwait(false);
            ThrowOnFirstFailure(preflight);
            var (plan, _, _) = StartPlanOf(preflight);
            ThrowIfNotConfirmed(confirmation, plan);
            (long Epoch, long OwnAborts)? watch;
            lock (_gate) {
                watch = _abortWatch is { } w && w.Gen == _generation ? (w.Epoch, w.OwnAborts) : null;
            }
            LogCheckEndsAdjust();
            await EndRoutineAsync("polar-align.stop", outcome: "aborted", null, CancellationToken.None).ConfigureAwait(false);
            lock (_gate) {
                if (_state == "failed") {
                    // The loop's end failed (its stop was not confirmed, or it failed meanwhile): the client has the reason.
                    throw new InvalidOperationException("the adjust loop ended with a failure, so the check was not started — see the error, then start again");
                }
            }
            return watch;
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
                    throw new InvalidOperationException("Done is not available in the rough step until the error is inside your tolerance — Stop, then Start to refine it with the guide camera");
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
                    // and set tracking now as the exit would (it may never unwind; a mount left tracking runs on toward the
                    // pier). #2066 — an end that hands tracking back does the hand-back here too: this end fences the run
                    // below, so its exit writes nothing when it unwinds. A takeover that gave up waiting fenced the run's
                    // stops meanwhile (null): the mount is its own, nothing is written.
                    var stopped = await StopMountMotionAsync(outcome, end.Fence).ConfigureAwait(false);
                    if (stopped is not null) {
                        NotePoseUnverifiedAfterRun(end, stopped);
                    }
                    lock (_gate) {
                        // #2066 — a finding the run's own exit made meanwhile stands: a "not confirmed" is never overwritten.
                        end.MountStopped ??= stopped;
                    }
                    if (stopped is not null && !StopsFenced(end.Fence)) {
                        ApplyExitTracking(end.PriorTracking, finished: outcome == "complete",
                            handedToGuiding: endRequest == HandedToGuiding, stopped, outcome);
                    }
                }
                if (!unwound && end is not null && !StopsFenced(end.Fence)) {
                    // #2066 — the stuck run may unwind at any later time; its exit would then stop the mount and turn tracking
                    // off in the middle of whatever the mount does by then (a park, a GoTo, the user's imaging). This end
                    // answered for the mount: fence the run's stops.
                    FenceStops();
                    LogAbandonedRunFenced();
                }
                // #2066 (ARA-2) — the run is over: the mount's motion owner goes back (a run that unwound gave it back in its
                // exit already; a stuck one gives it back here, after the stop above).
                end?.ReleaseHold();
                bool mountNotStopped;
                bool alignerNotStopped;
                bool becameStopped;
                bool reportMountNotStopped = false;
                bool reportAlignerNotStopped = false;
                bool wasActive;
                lock (_gate) {
                    // #2066 — read under the lock the run's exit records its finding in (ExitMountOnceAsync): a finding that
                    // lands after this read is told below, as this end marks the run reported, or by the run after the mark.
                    mountNotStopped = end?.MountStopped == false;
                    // #2069 — the run's end could not confirm the Polar Aligner stopped: told like a mount that did not stop.
                    alignerNotStopped = AlignerNotStoppedLocked(end);
                    wasActive = _active;
                    _active = false;
                    if (wasActive && outcome != EndedBySequence) {
                        RecordLastResultLocked(outcome == "complete");
                    }
                    if (wasActive) {
                        RememberRunReadoutLocked();
                    }
                    // The run's own gen-fenced resets are no-ops once the generation moved on above.
                    _waitingForCapture = false;
                    // A terminal "failed" or "checked" is a real outcome a polling client must still
                    // see — a late/stray Stop must not clobber it back to "stopped" (both already
                    // published their event, wrote their row and released the lease). The user's own
                    // Done on the checked verdict (Complete) does end it: the client leaves the
                    // verdict on the stopped event, and the check already wrote the row.
                    becameStopped = _state is not "failed" && (_state != "checked" || outcome == "complete");
                    if (becameStopped && (mountNotStopped || alignerNotStopped)) {
                        // Never "stopped" for a mount that may still move: the client shows the failure and its sentence.
                        _state = "failed";
                        becameStopped = false;
                        // #2066 — the mount's finding is claimed once (the run may have told it); #2069 — the aligner's
                        // read above is told here, and AlignerNotStoppedUntoldLocked below then leaves it alone.
                        reportMountNotStopped = mountNotStopped && end is not null && ClaimNotStoppedReportLocked(end);
                        reportAlignerNotStopped = alignerNotStopped;
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
                if (reportMountNotStopped) {
                    await PublishErrorAsync(MountDidNotStop,
                        reportAlignerNotStopped ? CutPowerSentence + " " + AlignerCutPowerSentence : CutPowerSentence).ConfigureAwait(false);
                } else if (reportAlignerNotStopped) {
                    await PublishErrorAsync(AlignerDidNotStop, AlignerCutPowerSentence).ConfigureAwait(false);
                } else if (becameStopped) {
                    await PublishStateEventAsync(WsEventCatalog.PolarAlignStopped, "stopped").ConfigureAwait(false);
                }
                bool notStoppedLate = false;
                bool alignerNotStoppedLate;
                lock (_gate) {
                    // #2066 — marked under the lock the run's exit records its finding in: a "not confirmed" that landed after
                    // the read above is told here, one after this mark by the run, never by neither and never twice.
                    // #2069 — the same for the run's aligner end (NoteAlignerEndAsync).
                    if (end is not null) {
                        end.MarkReported();
                        notStoppedLate = ClaimNotStoppedReportLocked(end);
                        if (notStoppedLate && _state == "stopped") {
                            _state = "failed";
                        }
                    }
                    alignerNotStoppedLate = AlignerNotStoppedUntoldLocked(end, alreadyRead: alignerNotStopped);
                }
                if (notStoppedLate) {
                    await PublishErrorAsync(MountDidNotStop, CutPowerSentence).ConfigureAwait(false);
                }
                if (alignerNotStoppedLate) {
                    await PublishErrorAsync(AlignerDidNotStop, AlignerCutPowerSentence).ConfigureAwait(false);
                }
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
        private sealed class RoutineFailedException(string reason, string message, bool mountTakenOver = false, string? stoppedBy = null) : Exception(message) {
            public string Reason { get; } = reason;
            /// <summary>#2066 — the stop that ended the run handed the mount to its sender (Emergency Stop, a park, a Find
            /// Home): the exit leaves the mount to it.</summary>
            public bool MountTakenOver { get; } = mountTakenOver;
            /// <summary>#2107 — for <see cref="SlewAbortedExternally"/>: the reason the stop from outside was counted with
            /// (<see cref="MountStopReasons"/>), sent as the error event's <c>stopped_by</c>; null when not known.</summary>
            public string? StoppedBy { get; } = stoppedBy;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The run task is the routine's top-level boundary: any escaped exception must land in the 'failed' state + WS error event, never fault an unobserved background task.")]
        private async Task RunRoutineAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, SiteSettingsDto site, PolarAlignSettingsDto settings, string stage,
                string camera, RunBand band, int gen, int startSeq, RunEnd end, CancellationToken ct) {
            // Per-run unique dir: an abandoned (zombie) run and its successor must never share
            // files — this run's cleanup deletes only its own directory.
            var workDir = Path.Combine(Path.GetTempPath(), "ara-polar-align", Guid.NewGuid().ToString("N"));
            // The user's tracking setting, handed back on every exit path once this run has touched it.
            bool? priorTracking = null;
            lock (_gate) {
                _trackingTouched = false;
            }
            var alignerRun = BeginAlignerRun(site.LatitudeDeg);
            // #2069 — the Polar Aligner's end stop, once: started beside the mount's exit (its up to 3 s of polling never
            // holds the mount's stop back), before a failure or the check's verdict is reported, else in the finally.
            Task<bool>? alignerEnd = null;
            Task<bool> EndAlignerOnceAsync() => alignerEnd ??= EndAlignerRunAsync(alignerRun);
            var alignerEndReported = false;
            lock (_gate) {
                // #2081 — the saved sign for the mount's driver; a run with none tests it before its first turn.
                _raMotorSign = band.RaMotorSign ?? 1.0;
            }
            // #2048 — the exit every path takes once: stop the mount and confirm it, then tracking. Before a failure or the
            // check's verdict is reported (the mount is safe by the time the client hears of the end), else in the finally.
            var exited = false;
            bool? exitStopped = null;
            var tellNotStopped = false;
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
                if (takenOver || end.Requested == TakenOver || StopsFenced(end.Fence)) {
                    // #2066 — Emergency Stop, the safety park, a park or a Find Home owns the mount now: it stops and checks
                    // the mount itself (Emergency Stop's second pass), or moves it on purpose. A stop round from here would
                    // abort the park, and the confirmation (up to 8 s) would hold Emergency Stop's rung past its bound. A
                    // fenced run (a takeover gave up waiting for it, or an end abandoned it) is in the same place.
                    LogExitLeftToTakeover(why);
                    return null;
                }
                lock (_gate) {
                    _exitsInFlight++;
                }
                try {
                    exitStopped = await StopMountMotionAsync(why, end.Fence).ConfigureAwait(false);
                    if (exitStopped is null) {
                        // Fenced while it stopped the mount: the mount is a takeover's now, no tracking write.
                        LogExitLeftToTakeover(why);
                        return null;
                    }
                    // #2066 — an unconfirmed stop (or a mount that reconnected while the run ran) leaves the pose unverified.
                    NotePoseUnverifiedAfterRun(end, exitStopped);
                    lock (_gate) {
                        // #2066 — recorded under the lock the stop path reads it and marks the run reported in
                        // (EndRoutineAsync): once the stop path has told the client the run ended, the finding is this run's
                        // to tell; before, the stop path tells it. Never by neither, never twice.
                        end.MountStopped = exitStopped;
                        tellNotStopped = end.Reported && ClaimNotStoppedReportLocked(end);
                        if (tellNotStopped && _state == "stopped" && _startSeq == startSeq) {
                            _state = "failed"; // never "stopped" for a mount that may still move (no newer run owns the state)
                        }
                    }
                    // #2089 — where this run left the mount: the next start tells a continued pose from a moved one by it.
                    await NoteMountLeftAfterStopAsync().ConfigureAwait(false);
                    if (StopsFenced(end.Fence)) {
                        // #2066 — fenced during that read (it may hang): the mount is a takeover's now, its park may be under
                        // way, and a tracking write would land between the park's tracking on and its Park.
                        LogExitLeftToTakeover(why);
                        if (tellNotStopped) {
                            await PublishErrorAsync(MountDidNotStop, CutPowerSentence).ConfigureAwait(false);
                        }
                        return exitStopped;
                    }
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
                if (tellNotStopped) {
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
                end.PriorTracking = priorTracking;
                // One PA-session lease renewer for the whole run: the stages share its clock, so a
                // hand-off never renews twice back to back.
                Func<Task> renew = () => RenewLeaseAsync(ct);

                // #2082 — the one measurement the user started. The coarse and fine ones run their live loop until Stop or
                // Done; the check ends the run itself (state `checked`). It starts where the mount points: no Home, no GoTo
                // back (#2048); its turns go the way the user confirmed.
                ThrowIfStoppedFromOutside(gen); // #2066 — a stop from outside since the Start
                // #2082 — the camera the plan named: StartAsync refused a main-camera start without the main camera's solver.
                var main = camera == PolarAlignCameras.Main
                    ? mainCamera ?? throw new InvalidOperationException("a main-camera measurement started without a main-camera solver")
                    : null;
                if (stage != PolarAlignStages.Check) {
                    if (main is not null) {
                        await RunMainCameraStageAsync(stage, main, site, settings, band, renew, gen, ct).ConfigureAwait(false);
                    } else {
                        await RunFineStageAsync(guiderClient, site, settings, band, workDir, renew, gen, ct).ConfigureAwait(false);
                    }
                } else {
                    var verdict = await RunCheckStageAsync(guiderClient, main, workDir, site, settings, band, gen, ct).ConfigureAwait(false);
                    // The check's verdict is a finished run: the mount is stopped and confirmed before the client hears of it,
                    // and so is the Polar Aligner (#2069).
                    var alignerStopping = EndAlignerOnceAsync();
                    if (await ExitMountOnceAsync(finished: true, "checked").ConfigureAwait(false) == false) {
                        throw new RoutineFailedException(MountDidNotStop, CutPowerSentence);
                    }
                    if (!await alignerStopping.ConfigureAwait(false)) {
                        throw new RoutineFailedException(AlignerDidNotStop, AlignerCutPowerSentence);
                    }
                    await FinishCheckedAsync(gen, verdict).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested || !IsCurrent(gen)) {
                // Stop — the stopping path owns the state + events. Any other cancellation (a solver or
                // HTTP timeout surfacing as TaskCanceledException) is a failure like any other below: left
                // here it would end the run silently with the state still "adjusting" and the lease held.
            } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException ex) when (IsCurrent(gen)) {
                // Solver setup problems (guide optics unset, ASTAP path wrong) are user-fixable —
                // surface the solver's own actionable message, not internal_error.
                alignerEndReported = true;
                await FailRoutineAsync("solver_configuration", ex, gen, end, () => ExitMountOnceAsync(finished: false, "failed"), EndAlignerOnceAsync()).ConfigureAwait(false);
            } catch (RoutineFailedException ex) when (IsCurrent(gen)) {
                alignerEndReported = true;
                await FailRoutineAsync(ex.Reason, ex, gen, end, () => ExitMountOnceAsync(finished: false, "failed", takenOver: ex.MountTakenOver),
                    EndAlignerOnceAsync(), stoppedBy: ex.StoppedBy).ConfigureAwait(false);
            } catch (Exception ex) when (IsCurrent(gen)) {
                alignerEndReported = true;
                await FailRoutineAsync("internal_error", ex, gen, end, () => ExitMountOnceAsync(finished: false, "failed"), EndAlignerOnceAsync()).ConfigureAwait(false);
            } catch (Exception ex) {
                // A superseded (zombie) run: its failure must not touch state, events, the lease, or
                // the session log the successor run now owns — log and drain quietly.
                LogRoutineFailed("superseded", ex);
            } finally {
                // #2069 — the Polar Aligner's verified stop starts first and runs beside the mount's exit, so its up to 3 s
                // of polling never holds the mount's stop back; the run ends once both are done.
                var alignerEnded = EndAlignerOnceAsync();
                try {
                    // Every exit stops the mount, a clean Stop included (it bumps the generation but not the start
                    // sequence); only a run that a NEWER RUN superseded leaves the mount to that run. Tracking goes off,
                    // or back to how the run found it after Done with stop_tracking_when_done off (ApplyExitTracking).
                    if (!exited) {
                        await ExitMountOnceAsync(finished: end.Requested == "complete", end.Requested ?? "ended",
                            handedToGuiding: end.Requested == HandedToGuiding).ConfigureAwait(false);
                    }
                    // #2066 (ARA-2) — the exit has stopped the mount (or left it to a takeover): the motion owner goes back,
                    // not held while the Polar Aligner's end stop finishes.
                    end.ReleaseHold();
                    await NoteAlignerEndAsync(end, await alignerEnded.ConfigureAwait(false), alignerEndReported || !NoNewerRunStarted(startSeq))
                        .ConfigureAwait(false);
                } finally {
                    // #2069 — whatever the exit or the aligner's end threw, the motion owner goes back (once) and the run's
                    // frames are not left behind.
                    end.ReleaseHold();
                    TryDeleteWorkDir(workDir);
                }
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

        /// <summary>The live loop shared by the coarse measurement (main camera) and the fine one (guide camera),
        /// both reading each solve as a §45.8 reverse-projection from the seed. Captures and solves until
        /// Stop or Done cancels it (#2082: it never moves on to another measurement). <paramref name="toError"/>
        /// turns a good solve (and its time) into the alt/az error.</summary>
        private async Task SolveLoopAsync(
                LiveCapture capture,
                Func<PolarAlignSolveOutcome, DateTimeOffset, (double AltErr, double AzErr)> toError,
                (double RaDeg, double DecDeg)? initialHint, Func<Task> renewLeaseIfDue,
                int gen, CancellationToken ct) {
            // The count runs on from the run's start: frame ids stay unique within a run (failed frames
            // included) and the §45.13 row counts every live frame.
            int iteration;
            lock (_gate) {
                iteration = _liveFrames;
            }
            var consecutiveFailures = 0;
            var hint = initialHint;

            while (!ct.IsCancellationRequested) {
                // #2066 — Stop Mount or Emergency Stop while the user adjusts ends the run too.
                ThrowIfStoppedFromOutside(gen);
                // Re-read every frame: exposure, binning, cadence and loop mode apply mid-run.
                var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
                if (settings.LoopMode == PolarAlignLoopModes.SingleFrame) {
                    settings = await WaitForCaptureRequestAsync(gen, renewLeaseIfDue, ct).ConfigureAwait(false);
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
                var s = await capture(frameId, hint, settings, timing, ct).ConfigureAwait(false);
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
        /// loop mode), keeping the guider lease alive meanwhile. Returns the settings to shoot with.</summary>
        private async Task<PolarAlignSettingsDto> WaitForCaptureRequestAsync(
                int gen, Func<Task> renewLeaseIfDue, CancellationToken ct) {
            SetWaitingForCapture(gen, true);
            try {
                while (true) {
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

        /// <summary>#2082 — a point whose solves scatter more than this about their mean is retaken once; still over it,
        /// the measurement is flagged noisy.</summary>
        internal const double MaxPointScatterArcsec = 3.0;

        /// <summary>One point of a measurement (a seed or a check point): <see cref="PolarAlignSettingsDto.FramesPerPoint"/>
        /// frames with the mount still, averaged as unit vectors (#2082), with the rms scatter of the solves about their
        /// mean logged in arcseconds. A point scattered over <see cref="MaxPointScatterArcsec"/> is retaken once; still
        /// over it (or with no solve in the retake: the first set stands), the run goes on and its result is flagged noisy
        /// (<see cref="MarkMeasurementNoisy"/>). A frame that
        /// does not solve is retried, up to <c>SeedSolveAttempts</c> failures per set; a set with no solve fails the
        /// routine (a seed that can't solve means clouds/focus problems the live loop can't fix either).</summary>
        /// <returns>The point's mean pointing and the mean time of its solves (the three-point fit moves each point to
        /// the last one's time by the Earth's turn since it was taken).</returns>
        private async Task<(PolarAlignSolveOutcome Point, DateTimeOffset At)> SolveSeedFrameAsync(
                LiveCapture capture, string frameId,
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var frames = FramesPerPoint(settings);
            var set = (await SolvePointSetAsync(capture, frameId, hint, settings, frames, attemptsBefore: 0, gen, ct).ConfigureAwait(false))!;
            LogPointScatter(frameId, set.Solved, set.Failed, set.Point.RaDegJnow, set.Point.DecDegJnow, set.ScatterArcsec);
            if (set.ScatterArcsec > MaxPointScatterArcsec) {
                LogPointRetake(frameId, set.ScatterArcsec, MaxPointScatterArcsec);
                var retake = await SolvePointSetAsync(capture, frameId, (set.Point.RaDegJnow, set.Point.DecDegJnow), settings, frames,
                    attemptsBefore: set.Attempts, gen, ct, retake: true).ConfigureAwait(false);
                if (retake is null) {
                    // The retake did not solve (a cloud): the first set still stands for the point, flagged noisy.
                    LogPointRetakeUnsolved(frameId, set.ScatterArcsec);
                    MarkMeasurementNoisy(gen);
                    return (set.Point, set.At);
                }
                set = retake;
                LogPointScatter(frameId, set.Solved, set.Failed, set.Point.RaDegJnow, set.Point.DecDegJnow, set.ScatterArcsec);
                if (set.ScatterArcsec > MaxPointScatterArcsec) {
                    LogPointNoisy(frameId, set.ScatterArcsec);
                    MarkMeasurementNoisy(gen);
                }
            }
            return (set.Point, set.At);
        }

        /// <summary>#2067 — the circular mean (degrees) of a point's frames' position angles, the frames with none left out;
        /// null when none has one. Circular: frames at 359° and 1° are 0°, not 180°.</summary>
        internal static double? MeanPositionAngleDeg(IReadOnlyList<double?> anglesDeg) {
            double sin = 0, cos = 0;
            var any = false;
            foreach (var a in anglesDeg) {
                if (a is double deg) {
                    sin += Math.Sin(deg * Math.PI / 180.0);
                    cos += Math.Cos(deg * Math.PI / 180.0);
                    any = true;
                }
            }
            return any ? Math.Atan2(sin, cos) * 180.0 / Math.PI : null;
        }

        /// <summary>The frames each point of a measurement averages (<c>polar_align.frames_per_point</c>, clamped).</summary>
        private static int FramesPerPoint(PolarAlignSettingsDto settings) =>
            Math.Clamp(settings.FramesPerPoint, PolarAlignSettingsClamp.MinFramesPerPoint, PolarAlignSettingsClamp.MaxFramesPerPoint);

        /// <summary>One set of a point's frames (<see cref="SolveSeedFrameAsync"/>).</summary>
        private sealed record PointSet(PolarAlignSolveOutcome Point, DateTimeOffset At, int Solved, int Failed, double ScatterArcsec, int Attempts);

        /// <param name="attemptsBefore">The frames this point took already (a retake): frame ids stay unique.</param>
        /// <param name="retake">A retake: a set with no solve returns null (the first set stands) and keeps no frame.</param>
        private async Task<PointSet?> SolvePointSetAsync(
                LiveCapture capture, string frameId, (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings,
                int frames, int attemptsBefore, int gen, CancellationToken ct, bool retake = false) {
            var anyFrameReachedSolver = false;
            string? keptFrame = null;
            var solves = new List<(PolarAlignSolveOutcome S, DateTimeOffset At)>(frames);
            var failed = 0;
            var attempt = attemptsBefore;
            var frameHint = hint;
            while (solves.Count < frames && failed < SeedSolveAttempts) {
                attempt++;
                // The frame kept for the focal-length measurement: the last one a set with no solve may take.
                var timing = new FrameTiming { KeepUnsolvedFrame = !retake && solves.Count == 0 && failed == SeedSolveAttempts - 1 };
                // One id per frame, on frame_started and frame_complete alike.
                var attemptId = frameId + "-" + attempt.ToString(CultureInfo.InvariantCulture);
                var s = await capture(attemptId, frameHint, settings, timing, ct).ConfigureAwait(false);
                var at = DateTimeOffset.UtcNow;
                await CompleteFrameAsync(gen, attemptId, s, timing, s.Success ? 0 : failed + 1).ConfigureAwait(false);
                // #2066 — a stop during the exposure or the solve: no further frame, and no turn after it.
                ThrowIfStoppedFromOutside(gen);
                keptFrame = timing.KeptFramePath;
                if (s.Success) {
                    solves.Add((s, at));
                    frameHint = (s.RaDegJnow, s.DecDegJnow); // the mount is still: the next frame solves near this one
                    continue;
                }
                failed++;
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
            if (solves.Count > 0) {
                // #2072 — the mean carries its 1σ from the scatter, for the fit's uncertainty.
                var (point, meanAt, scatter) = AveragePointSolves(solves);
                // #2067 ARA-13 — the point keeps its frames' position angle, so the field check sees every point.
                var positionAngle = MeanPositionAngleDeg(solves.ConvertAll(p => p.S.PositionAngleDeg));
                return new PointSet(point with { PositionAngleDeg = positionAngle }, meanAt, solves.Count, failed, scatter, attempt);
            }
            if (retake) {
                return null;
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

        /// <summary>#2082 — a point of this run's measurement stayed scattered after its retake: the status, each progress
        /// event and the check's verdict say the readout rests on noisy solves.</summary>
        private void MarkMeasurementNoisy(int gen) {
            lock (_gate) {
                if (_generation == gen) {
                    _measurementNoisy = true;
                }
            }
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
        // confirmed. #2069: the Polar Aligner's end stop (alignerEnded, started beside the mount's) is awaited too, and its
        // own sentence appended when it was not confirmed.
        private async Task FailRoutineAsync(string reason, Exception ex, int gen, RunEnd end, Func<Task<bool?>> exitMount, Task<bool> alignerEnded,
                string? stoppedBy = null) {
            LogRoutineFailed(reason, ex);
            bool claimed;
            lock (_gate) {
                claimed = _generation == gen; // superseded between the catch filter and here — the successor owns state
                if (claimed) {
                    // #2066 — this failure is the run's last event and carries an unconfirmed stop itself (the sentence below):
                    // neither the stop path nor the exit tells mount_did_not_stop for it again.
                    end.ReportsItself = true;
                    _state = "failed";
                    _active = false;
                    RememberRunReadoutLocked(); // #2082 — a failed run that had a readout is still the last run
                    // In the same section that clears _active: a sequence start reading IsActive between the two would
                    // otherwise see no run and go on while the exit below still sends its stop rounds and tracking write.
                    _exitsInFlight++;
                }
            }
            bool? mountStopped;
            bool alignerStopped;
            try {
                mountStopped = await exitMount().ConfigureAwait(false);
                alignerStopped = await alignerEnded.ConfigureAwait(false);
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
            var notStopped = mountStopped == false && reason != MountDidNotStop;
            var message = notStopped ? ex.Message + " " + CutPowerSentence : ex.Message;
            if (!alignerStopped && reason != AlignerDidNotStop) {
                message += " " + AlignerCutPowerSentence;
            }
            await PublishErrorAsync(reason, message, stoppedBy, stopUnconfirmed: notStopped).ConfigureAwait(false);
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
                    AlignerMoves: _alignerMoves,
                    FinalSigmaArcmin: total is null ? null : _fitSigma?.TotalSigmaArcmin);
            }
            LogFinalError(outcome, record.FinalErrorArcmin, record.FinalSigmaArcmin, record.FinalAltErrorArcmin, record.FinalAzErrorArcmin, record.Iterations);
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
                await run.WaitAsync(RunUnwindGrace, RunUnwindClock).ConfigureAwait(false);
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
            double? decSpread;
            bool noisy;
            FitUncertainty? sigma;
            lock (_gate) {
                stage = _stage;
                caution = _readoutCaution;
                decSpread = _decSpreadArcsec;
                noisy = _measurementNoisy;
                sigma = _fitSigma;
            }
            return PublishAsync(WsEventCatalog.PolarAlignProgress, new JsonObject {
                ["stage"] = stage,
                ["readout_caution"] = caution,
                ["dec_spread_arcsec"] = decSpread,
                ["measurement_noisy"] = noisy,
                ["iteration"] = iteration,
                ["altitude_error_arcmin"] = Math.Round(altErrArcmin, 2),
                ["azimuth_error_arcmin"] = Math.Round(azErrArcmin, 2),
                ["total_error_arcmin"] = Math.Round(total, 2),
                // #2072 — the readout's 1σ (the seed fit's), beside each error.
                ["altitude_sigma_arcmin"] = sigma is null ? null : Math.Round(sigma.AltSigmaArcmin, 2),
                ["azimuth_sigma_arcmin"] = sigma is null ? null : Math.Round(sigma.AzSigmaArcmin, 2),
                ["total_sigma_arcmin"] = sigma is null ? null : Math.Round(sigma.TotalSigmaArcmin, 2),
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

        /// <param name="stoppedBy">#2107 — for <see cref="SlewAbortedExternally"/>: what stopped the mount
        /// (<see cref="MountStopReasons"/>), so the app words the end in its own language; left out when not known.</param>
        /// <param name="stopUnconfirmed">#2066 — the run's exit could not confirm its stop, and this error carries the
        /// cut-power sentence itself (no <c>mount_did_not_stop</c> follows): an app that words the end from
        /// <paramref name="stoppedBy"/> keeps the warning. Sent only when true.</param>
        private Task PublishErrorAsync(string reason, string message, string? stoppedBy = null, bool stopUnconfirmed = false) {
            var payload = new JsonObject {
                ["reason"] = reason,
                ["message"] = message,
            };
            if (stoppedBy is not null) {
                payload["stopped_by"] = stoppedBy;
            }
            if (stopUnconfirmed) {
                payload["stop_unconfirmed"] = true;
            }
            return PublishAsync(WsEventCatalog.PolarAlignError, payload);
        }

        /// <summary>#2066 — under <c>_gate</c>: whether the caller is the one to tell the client that the run's stop was not
        /// confirmed (<c>mount_did_not_stop</c>), and if so claims it. Not when the stop was confirmed or is not known yet,
        /// when the run's own failure report carries it, or when it was told already.</summary>
        private static bool ClaimNotStoppedReportLocked(RunEnd end) {
            if (end.MountStopped != false || end.ReportsItself || end.NotStoppedTold) {
                return false;
            }
            end.NotStoppedTold = true;
            return true;
        }

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

        [LoggerMessage(EventId = 4574, Level = LogLevel.Information, Message = "§45 polar-align {Frame}: {Solved} solves averaged ({Failed} failed), mean RA {RaDeg:F5}° Dec {DecDeg:F5}° (JNOW), rms scatter {ScatterArcsec:F2}″")]
        private partial void LogPointScatter(string frame, int solved, int failed, double raDeg, double decDeg, double scatterArcsec);

        [LoggerMessage(EventId = 4575, Level = LogLevel.Information, Message = "§45 polar-align {Frame}: rms scatter {ScatterArcsec:F2}″ is over {LimitArcsec:F0}″ — retaking the point once")]
        private partial void LogPointRetake(string frame, double scatterArcsec, double limitArcsec);

        [LoggerMessage(EventId = 4576, Level = LogLevel.Warning, Message = "§45 polar-align {Frame}: still {ScatterArcsec:F2}″ rms after the retake — the result is flagged noisy")]
        private partial void LogPointNoisy(string frame, double scatterArcsec);

        [LoggerMessage(EventId = 4577, Level = LogLevel.Warning, Message = "§45 polar-align {Frame}: the retake did not solve — the first set ({ScatterArcsec:F2}″ rms) stands for the point and the result is flagged noisy")]
        private partial void LogPointRetakeUnsolved(string frame, double scatterArcsec);

        [LoggerMessage(EventId = 4528, Level = LogLevel.Information, Message = "§45 polar-align seed fit: mount RA turn {RotationDeg:F3}° -> axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; seed error alt {AltArcmin:F2}′ ± {AltSigmaArcmin:F2}′ az {AzArcmin:F2}′ ± {AzSigmaArcmin:F2}′, total ± {TotalSigmaArcmin:F2}′ (1σ); solves {Solves}; σ floor {Floor}")]
        private partial void LogSeedFit(double rotationDeg, double axisRaDeg, double axisDecDeg, double altArcmin, double altSigmaArcmin, double azArcmin, double azSigmaArcmin, double totalSigmaArcmin, string solves, string floor);

        [LoggerMessage(EventId = 4529, Level = LogLevel.Information, Message = "§45 polar-align live readout {FrameId}: alt {AltArcmin:F2}′ az {AzArcmin:F2}′ (solve JNOW RA {RaDeg:F5}° Dec {DecDeg:F5}°, tracking {Tracking})")]
        private partial void LogLiveReadout(string? frameId, double altArcmin, double azArcmin, double raDeg, double decDeg, bool tracking);

        [LoggerMessage(EventId = 4530, Level = LogLevel.Information, Message = "§45 polar-align finished ({Outcome}): final error {TotalArcmin}′ ± {SigmaArcmin}′ (1σ; alt {AltArcmin}′ az {AzArcmin}′) after {Iterations} live iterations")]
        private partial void LogFinalError(string outcome, double? totalArcmin, double? sigmaArcmin, double? altArcmin, double? azArcmin, int iterations);

        [LoggerMessage(EventId = 4510, Level = LogLevel.Information, Message = "§45 polar-align routine started at the {Stage} stage (PA-session lease acquired)")]
        private partial void LogStarted(string stage);

        [LoggerMessage(EventId = 4511, Level = LogLevel.Information, Message = "§45 polar-align routine stopped (PA-session lease released)")]
        private partial void LogStopped();


        [LoggerMessage(EventId = 4513, Level = LogLevel.Warning, Message = "§45 WS publish of a polar-align event failed (best-effort)")]
        private partial void LogWsPublishFailed(Exception exception);

        [LoggerMessage(EventId = 4514, Level = LogLevel.Error, Message = "§45 polar-align routine failed: {Reason}")]
        private partial void LogRoutineFailed(string reason, Exception exception);

        [LoggerMessage(EventId = 4541, Level = LogLevel.Information, Message = "§45 polar-align: Check from the adjust loop — the loop ends like Stop, then the confirmed check starts")]
        private partial void LogCheckEndsAdjust();

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