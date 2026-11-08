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
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
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
using OpenAstroAra.Equipment.Equipment.MyTelescope;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §45 — the polar-alignment routine (the engine slice, replacing the lifecycle-only skeleton).
    /// ARA owns the whole routine (guider POLAR_ALIGNMENT_DESIGN.md §4): the guider is a capture +
    /// lease source (<c>capture_single_frame</c> under the PA-session camera lease), ARA runs the
    /// plate solver (<see cref="IPolarAlignFrameSolver"/>, guide optics), the mount slews
    /// (<see cref="ITelescopeMediator"/>), and the <see cref="PolarAlignGeometry"/> math.
    ///
    /// <para><b>Stages</b> (<see cref="PolarAlignStages"/>, the partial in PolarAlignService.Stages.cs):
    /// <c>coarse</c> (Home, then the seed-and-adjust routine below on the main camera — slower frames,
    /// for the big moves), <c>fine</c> (the same routine on the guide camera), <c>check</c> (Home,
    /// three guide-camera solves with two RA turns, an exact three-point fit, state <c>checked</c>).
    /// The user's Next moves from one to the next.</para>
    ///
    /// <para><b>Fine-stage state machine</b> (the <c>state</c> string in <see cref="PolarAlignStateDto"/>):
    /// <c>idle</c> → Start → <c>seeding</c> (frame A → RA slew Δ away from the meridian → frame B →
    /// two-point axis fit) → <c>adjusting</c> (tracking stopped; continuous capture→solve, each
    /// solve's alt/az delta from the seed pointing IS the user's knob adjustment — §45.8
    /// reverse-projection, no re-slewing) → Stop → <c>stopped</c>. Five consecutive failed solves
    /// park the loop in <c>paused</c> (it keeps retrying at a slower cadence and resumes on the next
    /// good solve); a fatal error (seed solve failure, mount fault) lands in <c>failed</c>.
    /// Tracking is restored to its pre-routine value on every exit path.</para>
    /// </summary>
    public sealed partial class PolarAlignService : IPolarAlignService, IDisposable {

        private readonly GuiderService _guider;
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
        // values). The lease is renewed at less than half its 600 s timeout so one missed renew
        // (e.g. a slow solve) still leaves margin.
        private const int LeaseTimeoutSeconds = 600;
        internal const int MaxConsecutiveSolveFailures = 5;
        private const int SeedSolveAttempts = 3;

        // Instance (not const) so tests — InternalsVisibleTo — shrink the cadences: the real values
        // would make a paused-then-resume loop test take tens of seconds of wall clock. The §45.12
        // user-facing knobs (exposure, binning, seed Δ, cadence, settle) come from the profile's
        // PolarAlignSettingsDto, read once at Start.
        internal TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(240);
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
        private int _liveIterations;
        // Every live frame of the run, failed ones included — the frame-id counter across the stages.
        private int _liveFrames;
        private DateTimeOffset _startedAt;
        private bool _captureRequested;
        private bool _waitingForCapture;
        private PolarAlignLastFrameDto? _lastFrame;

        // Stages (PolarAlignStages): which one runs, and the hand-offs between them. _advanceRequested
        // is the user's Continue/Next; _waitingForHome = the mount cannot find Home itself;
        // _fineFinalAlt/AzArcmin = the fine stage's last readout, compared with the check's result.
        private string? _stage;
        private bool _advanceRequested;
        private bool _waitingForHome;
        private int _checkSolvesDone;
        private PolarAlignCheckResultDto? _checkResult;
        // The fine stage's last readout, both components; the check derives its total and the distance to its own axis.
        private double? _fineFinalAltArcmin;
        private double? _fineFinalAzArcmin;
        // Written by the run task only: whether this run changed tracking (then the exit restores it).
        private bool _trackingTouched;
        private readonly IPolarAlignMainCameraSolver? _mainCamera;

        // §45.4 — the guide camera's frame size as the daemon reported it at this run's start; null
        // when it could not (auto binning then stays at 1×1). Written by the run task only.
        private (int Width, int Height)? _runFrameSize;

        // Run-generation token: Stop abandons a run task that won't unwind within the grace period
        // (a hung guider RPC), and a new Start may then launch a fresh run while the zombie is still
        // draining. Every state write / WS publish from a run checks its generation first, so a
        // superseded run's late effects are no-ops (its unique work dir keeps the files apart too).
        private int _generation;

        /// <summary>True while the routine runs — the guide-camera focus loop refuses to start then (one
        /// PA-session lease, one capture stream).</summary>
        public bool IsActive {
            get {
                lock (_gate) {
                    return _active;
                }
            }
        }

        private bool IsCurrent(int gen) {
            lock (_gate) {
                return _generation == gen;
            }
        }

        // Start-sequence counter, bumped ONLY by StartAsync (unlike _generation, which Stop also
        // bumps). The hand-back fence: a run restores tracking unless a NEWER RUN has started —
        // a clean Stop must still restore (its generation is already stale by design), but a zombie
        // unblocking after a successor Start turned tracking off must not stomp the successor's
        // mount state (§45.8 depends on tracking staying off mid-adjust).
        private int _startSeq;

        private bool NoNewerRunStarted(int startSeq) {
            lock (_gate) {
                return _startSeq == startSeq;
            }
        }

        private CancellationTokenSource? _runCts;
        private Task? _runTask;

        private readonly IPolarAlignmentLog? _log;
        // The guide-camera focus loop shares the daemon's PA-session lease; a Start ends it first.
        private readonly Func<IGuideFocusService?>? _guideFocus;

        public PolarAlignService(
                GuiderService guider,
                ILogger<PolarAlignService> logger,
                IPolarAlignFrameSolver solver,
                ITelescopeMediator mount,
                IProfileStore profileStore,
                IWsBroadcaster? ws = null,
                IPolarAlignmentLog? log = null,
                IPolarAlignFrameFetcher? frameFetcher = null,
                Func<IGuideFocusService?>? guideFocus = null,
                IPolarAlignMainCameraSolver? mainCamera = null) {
            _guider = guider;
            _mainCamera = mainCamera;
            _logger = logger;
            _solver = solver;
            _mount = mount;
            _profileStore = profileStore;
            _ws = ws;
            _log = log;
            _ownsFetcher = frameFetcher is null; // the DI-provided fetcher is container-owned
            _frameFetcher = frameFetcher ?? new HttpPolarAlignFrameFetcher();
            _guideFocus = guideFocus;
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
                    WaitingForHome: _waitingForHome,
                    CheckSolvesDone: _checkSolvesDone,
                    CheckResult: _checkResult));
            }
        }

        /// <summary>Proxies the guider daemon's live-frame JPEG so a client on any network sees the
        /// frames polar align is solving (the daemon may sit on an address only this host reaches).
        /// Null when no guider is connected or the fetch fails — a missing preview is never an error.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The preview is best-effort; any fetch failure just means no image this time.")]
        public async Task<byte[]?> GetLiveFrameJpegAsync(CancellationToken ct) {
            PHD2Guider guiderClient;
            try {
                guiderClient = _guider.RequireConnectedGuider();
            } catch (InvalidOperationException ex) {
                LogLiveFrameUnavailable(ex.Message);
                return null;
            }
            var host = guiderClient.ConnectedHost;
            if (string.IsNullOrEmpty(host)) {
                LogLiveFrameUnavailable("guider connection endpoint unknown");
                return null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try {
                return await _frameFetcher.FetchLiveFrameJpegAsync(host, guiderClient.ConnectedRpcPort, timeout.Token).ConfigureAwait(false);
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

        /// <summary>Continue / Next: release a routine parked on the user — waiting for the mount at
        /// Home, or in a live loop (coarse → fine, fine → check). 409 otherwise.</summary>
        public async Task<OperationAcceptedDto> NextAsync(string? idempotencyKey, CancellationToken ct) {
            CancellationTokenSource? inFlight;
            lock (_gate) {
                var waitable = _active && (_waitingForHome || _state is "adjusting" or "paused");
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

        /// <summary>Consume the user's Continue/Next for this run (false when none is pending).</summary>
        private bool TakeAdvanceRequest(int gen) {
            lock (_gate) {
                if (_generation != gen || !_advanceRequested) {
                    return false;
                }
                _advanceRequested = false;
                return true;
            }
        }

        /// <summary>
        /// Begin the routine: preflight (connected guider, connected mount, a configured site
        /// latitude), acquire the guide-camera PA-session lease, then run the seed + live-adjust
        /// state machine on a background task. Idempotent — Start on an already-running routine is a
        /// no-op accept. Throws <see cref="InvalidOperationException"/> on a failed preflight
        /// (mapped to 409 by the endpoint) — the lease is only acquired after every check passes, so
        /// a failed Start leaves nothing to roll back.
        /// </summary>
        public Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) =>
            StartAsync(stage: null, idempotencyKey, ct);

        /// <summary>Begin at <paramref name="stage"/>: <c>coarse</c> (default) → Home + the main-camera
        /// seed and live loop; <c>fine</c> → the guide-camera seed + live loop from the current pointing;
        /// <c>check</c> → Home + the three-point measurement only.</summary>
        public async Task<OperationAcceptedDto> StartAsync(string? stage, string? idempotencyKey, CancellationToken ct) {
            var explicitStage = stage is not null;
            stage ??= PolarAlignStages.Coarse;
            if (!PolarAlignStages.IsValid(stage)) {
                throw new ArgumentException($"unknown polar-align stage '{stage}' — use coarse, fine or check");
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
                if (priorRun is not null) {
                    await AwaitRunUnwindAsync(priorRun).ConfigureAwait(false);
                }
                priorCts?.Dispose();
                var guiderClient = _guider.RequireConnectedGuider();
                if (_guideFocus?.Invoke() is { IsActive: true } focus) {
                    await focus.StopAsync().ConfigureAwait(false);
                }
                var mountInfo = _mount.GetInfo();
                if (mountInfo?.Connected != true) {
                    throw new InvalidOperationException("mount is not connected");
                }
                var site = _profileStore.GetSiteSettings();
                if (!(double.IsFinite(site.LatitudeDeg) && Math.Abs(site.LatitudeDeg) > 0 && Math.Abs(site.LatitudeDeg) <= 90)) {
                    throw new InvalidOperationException(
                        "site latitude must be configured (non-zero) before polar alignment — the routine measures against your site's celestial pole");
                }
                var settings = PolarAlignSettingsClamp.Apply(_profileStore.GetPolarAlignSettings());
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
                        // A Start that named no stage (Simple Mode, the Night Setup pilot) wants the routine,
                        // not the rough step in particular: a guide-camera-only session still polar-aligns.
                        LogCoarseSkipped(coarseUnavailable);
                        stage = PolarAlignStages.Fine;
                    }
                }
                // The coarse and check stages open with the mount going Home; without Home each opens
                // in its own first state (the fine stage always seeds at once).
                var initialState = stage switch {
                    PolarAlignStages.Check => settings.UseHome ? "homing" : "checking",
                    PolarAlignStages.Coarse => settings.UseHome ? "homing" : "seeding",
                    _ => FineStartHomesFirst(settings) ? "homing" : "seeding",
                };
                await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                int gen;
                int startSeq;
                lock (_gate) {
                    _active = true;
                    _state = initialState;
                    _stage = stage;
                    _advanceRequested = false;
                    _waitingForHome = false;
                    _checkSolvesDone = 0;
                    _checkResult = null;
                    _fineFinalAltArcmin = null;
                    _fineFinalAzArcmin = null;
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
                    _lastCaptureFault = null; // fresh run — stale capture faults must not color its failures
                    // _abandonedCaptureDebts is deliberately NOT reset: a capture abandoned by the
                    // JUST-STOPPED run may still deliver its event into this run (review r2).
                    startSeq = ++_startSeq;
                }
                _runCts = new CancellationTokenSource();
                var runToken = _runCts.Token;
                LogStarted(stage);
                // `started` goes out BEFORE the run task exists: the run's first event (needs_home on a
                // mount without Find Home) is only a directory create and two GetInfo calls away, and a
                // client that folds `started` after it would drop the Continue button.
                await PublishAsync(WsEventCatalog.PolarAlignStarted, new JsonObject { ["state"] = initialState, ["stage"] = stage }).ConfigureAwait(false);
                _runTask = Task.Run(() => RunRoutineAsync(guiderClient, mainCamera, site, settings, stage, gen, startSeq, runToken), CancellationToken.None);
                return Accepted("polar-align.start", idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        /// <summary>
        /// Stop the routine: cancel the state machine, wait for it to unwind (it restores the mount's
        /// pre-routine tracking state on its way out), release the PA-session lease (best-effort —
        /// it auto-expires regardless), and report <c>stopped</c>. Idempotent; §45 step 10: the mount
        /// stays exactly where it is — no slew home.
        /// </summary>
        public Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) =>
            EndRoutineAsync("polar-align.stop", outcome: "aborted", idempotencyKey, ct);

        /// <summary>
        /// §45 step 9 — the user is satisfied: same unwind as Stop, but the §45.13 session row records
        /// <c>complete</c> with the achieved error. Accepted (without a log row) when no routine is
        /// active, mirroring Stop's idempotency.
        /// </summary>
        public Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct) =>
            EndRoutineAsync("polar-align.complete", outcome: "complete", idempotencyKey, ct);

        private async Task<OperationAcceptedDto> EndRoutineAsync(
                string operationType, string outcome, string? idempotencyKey, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var (cts, run) = (_runCts, _runTask);
                _runCts = null;
                _runTask = null;
                // Supersede the run BEFORE cancelling: if it outlives the 15s unwind grace (a
                // guider/mount call that ignores cancellation for a while), its late catch blocks
                // must see a stale generation — otherwise a delayed failure would clobber the
                // terminal "stopped" state with "failed" + a spurious error event. The generation
                // guard protects a deliberate Stop exactly like it protects a successor Start.
                lock (_gate) {
                    _generation++;
                }
                if (cts is not null) {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                if (run is not null) {
                    await AwaitRunUnwindAsync(run).ConfigureAwait(false);
                }
                cts?.Dispose();
                bool becameStopped;
                bool wasActive;
                lock (_gate) {
                    wasActive = _active;
                    _active = false;
                    // The run's own gen-fenced resets are no-ops once the generation moved on above.
                    _waitingForHome = false;
                    _waitingForCapture = false;
                    // A terminal "failed" or "checked" is a real outcome a polling client must still
                    // see — a late/stray Stop must not clobber it back to "stopped" (both already
                    // published their event, wrote their row and released the lease). The user's own
                    // Done on the checked verdict (Complete) does end it: the client leaves the
                    // verdict on the stopped event, and the check already wrote the row.
                    becameStopped = _state is not "failed" && (_state != "checked" || outcome == "complete");
                    if (becameStopped) {
                        _state = "stopped";
                    }
                }
                if (wasActive) {
                    // Only a routine this call actually ended gets a session row — a failed routine
                    // already wrote its own, and a repeated Stop must not duplicate.
                    await WriteSessionRowBestEffortAsync(outcome, ct).ConfigureAwait(false);
                }
                await ReleaseLeaseBestEffortAsync(ct).ConfigureAwait(false);
                LogStopped();
                if (becameStopped) {
                    await PublishStateEventAsync(WsEventCatalog.PolarAlignStopped, "stopped").ConfigureAwait(false);
                }
                return Accepted(operationType, idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        public void Dispose() {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _advanceCts?.Dispose();
            _opLock.Dispose();
            if (_ownsFetcher && _frameFetcher is IDisposable d) {
                d.Dispose(); // only the self-constructed default — a DI-injected fetcher is container-owned
            }
        }

        // ── the routine ──────────────────────────────────────────────────────────────────────

        /// <summary>Unpark a parked mount before the routine moves it; fail as <c>mount_parked</c> when it
        /// will not unpark.</summary>
        private async Task UnparkIfParkedAsync(CancellationToken ct) {
            if (_mount.GetInfo() is not { AtPark: true }) {
                return;
            }
            LogUnparking();
            bool unparked;
            try {
                unparked = await _mount.UnparkTelescope(new Progress<ApplicationStatus>(), ct).ConfigureAwait(false);
            } catch (SequenceEntityFailedException ex) {
                throw new RoutineFailedException("mount_parked", $"the mount is parked and could not be unparked: {ex.Message}");
            }
            if (!unparked || _mount.GetInfo() is { AtPark: true }) {
                throw new RoutineFailedException("mount_parked",
                    "the mount is parked and could not be unparked — unpark it from the mount controls, then start again");
            }
        }

        private const string TrackingOffMessage =
            "the mount is not tracking and would not start — turn tracking on from the mount controls, then start again";

        /// <summary>A §42.3 fault recovery (a mount reconnect) can drop tracking between the routine's
        /// switch-on and its slew (CM4, 2026-11-05: six seconds later). Checked just before a slew, not
        /// during one: the iOptron reports tracking off while it slews.</summary>
        private async Task KeepTrackingOnForSlewAsync(int gen, CancellationToken ct) {
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
            try {
                if (!_mount.SetTrackingEnabled(true)) {
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

        private const string SeedSlewRefusedMessage =
            "the mount refused the RA seed slew — slew it to about Dec 85° from the mount controls, then start again";

        /// <summary>A fatal routine failure with a machine-readable reason for the WS error event
        /// (<c>seed_solve_failed</c>, <c>slew_failed</c>, …). Anything else that escapes the run
        /// task is reported as reason <c>internal_error</c>.</summary>
        [SuppressMessage("Design", "CA1032:Implement standard exception constructors",
            Justification = "Private, never serialized or thrown without a reason code; the standard ctors would allow constructing it without one.")]
        private sealed class RoutineFailedException(string reason, string message) : Exception(message) {
            public string Reason { get; } = reason;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The run task is the routine's top-level boundary: any escaped exception must land in the 'failed' state + WS error event, never fault an unobserved background task.")]
        private async Task RunRoutineAsync(
                PHD2Guider guiderClient, IPolarAlignMainCameraSolver? mainCamera, SiteSettingsDto site, PolarAlignSettingsDto settings, string stage,
                int gen, int startSeq, CancellationToken ct) {
            // Per-run unique dir: an abandoned (zombie) run and its successor must never share
            // files — this run's cleanup deletes only its own directory.
            var workDir = Path.Combine(Path.GetTempPath(), "ara-polar-align", Guid.NewGuid().ToString("N"));
            // The user's tracking setting, handed back on every exit path once this run has touched it.
            // Read AFTER the unpark below: a parked mount reports tracking off, and what the user gets
            // back is the tracking the mount came up with, not the parked reading.
            bool? priorTracking = null;
            lock (_gate) {
                _trackingTouched = false;
            }
            try {
                Directory.CreateDirectory(workDir);

                // #1665 — never capture with another profile's Guide Camera.
                if (_guider.OutOfStepReason is { } outOfStep) {
                    throw new RoutineFailedException("guider_out_of_step", outOfStep);
                }

                // ── unpark: the routine rotates the mount, and the mediator refuses every slew while it is
                // parked. 2026-10-17 the mount had been parked a minute before; the first frame solved and
                // the run then failed with "the RA seed slew was rejected by the mount". The user started a
                // routine that moves the mount, so unpark it here, before anything else.
                await UnparkIfParkedAsync(ct).ConfigureAwait(false);
                var mountInfo = _mount.GetInfo();
                priorTracking = mountInfo.TrackingEnabled;
                // Without Home, "home" is where the user put the mount: the later stages return there
                // before their turn, so the stages' turns never add up toward a limit.
                var startPointing = CurrentMountPointingJnow();
                // One PA-session lease renewer for the whole run: the stages share its clock, so a
                // hand-off never renews twice back to back.
                var renew = MakeLeaseRenewer(guiderClient, ct);

                // The stages run in order from the one the user started at; each live loop returns
                // when the user taps Next, and the check ends the run itself (state `checked`).
                // Only a stage AFTER another returns to the start pointing (use_home off): the first stage
                // is already there.
                var laterStage = false;
                if (stage == PolarAlignStages.Coarse) {
                    // StartAsync refused a coarse start without the main camera's solver.
                    await RunCoarseStageAsync(guiderClient,
                        mainCamera ?? throw new InvalidOperationException("the coarse stage started without a main-camera solver"),
                        site, settings, renew, gen, ct).ConfigureAwait(false);
                    stage = PolarAlignStages.Fine;
                    laterStage = true;
                }
                if (stage == PolarAlignStages.Fine) {
                    var homeFirst = laterStage || FineStartHomesFirst(settings);
                    await RunFineStageAsync(guiderClient, workDir, site, settings, homeFirst, laterStage ? startPointing : null, renew, gen, ct).ConfigureAwait(false);
                    stage = PolarAlignStages.Check;
                    laterStage = true;
                }
                await RunCheckStageAsync(guiderClient, workDir, site, settings, laterStage ? startPointing : null, renew, gen, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                // Stop — the stopping path owns the state + events. Any other cancellation (a solver or
                // HTTP timeout surfacing as TaskCanceledException) is a failure like any other below: left
                // here it would end the run silently with the state still "adjusting" and the lease held.
            } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException ex) when (IsCurrent(gen)) {
                // Solver setup problems (guide optics unset, ASTAP path wrong) are user-fixable —
                // surface the solver's own actionable message, not internal_error.
                await FailRoutineAsync("solver_configuration", ex, gen).ConfigureAwait(false);
            } catch (RoutineFailedException ex) when (IsCurrent(gen)) {
                await FailRoutineAsync(ex.Reason, ex, gen).ConfigureAwait(false);
            } catch (Exception ex) when (IsCurrent(gen)) {
                await FailRoutineAsync("internal_error", ex, gen).ConfigureAwait(false);
            } catch (Exception ex) {
                // A superseded (zombie) run: its failure must not touch state, events, the lease, or
                // the session log the successor run now owns — log and drain quietly.
                LogRoutineFailed("superseded", ex);
            } finally {
                // Hand back tracking unless a NEWER RUN has started (it owns the mount now and has
                // already turned tracking off for its own adjust loop — a zombie's late restore
                // would silently break the successor's §45.8 stationary-camera assumption). A clean
                // Stop still restores: Stop bumps the generation but not the start sequence.
                if (_trackingTouched && NoNewerRunStarted(startSeq)) {
                    RestoreTrackingBestEffort(priorTracking);
                }
                TryDeleteWorkDir(workDir);
            }
        }

        /// <summary>A fine start goes Home first whenever <c>use_home</c> is on — a fine stage after a check
        /// ("Adjust again") or after a stopped run would otherwise turn further out from wherever the last
        /// turn left the mount, toward a GEM's limit. A mount without Find Home is asked for Home (the
        /// needs_home wait), like every other stage; <c>use_home: false</c> starts from the current pointing.</summary>
        private static bool FineStartHomesFirst(PolarAlignSettingsDto settings) => settings.UseHome;

        /// <summary>Every tracking write of a run goes through here so the exit knows to hand it back. A
        /// superseded run writes nothing (see <see cref="TryMarkTrackingTouched"/>).</summary>
        private void SetTracking(int gen, bool enabled) {
            if (!TryMarkTrackingTouched(gen)) {
                return;
            }
            _mount.SetTrackingEnabled(enabled);
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
        private async Task<(int Width, int Height)?> ReadGuideFrameSizeAsync(PHD2Guider guiderClient, CancellationToken ct) {
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
                LogLiveReadout(frameId, altErr, azErr);
                lock (_gate) {
                    if (_generation == gen) {
                        _liveIterations = iteration;
                    }
                }
                await CompleteFrameAsync(gen, frameId, s, timing, consecutiveSolveFailures: 0).ConfigureAwait(false);
                await PublishProgressAsync(gen, iteration, altErr, azErr, solved: true).ConfigureAwait(false);

                var elapsed = DateTimeOffset.UtcNow - iterationStart;
                if (settings.LoopMode != PolarAlignLoopModes.SingleFrame && elapsed < cadence) {
                    await Task.Delay(cadence - elapsed, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>The guider PA-session lease renewer for a run: best-effort, at most once per
        /// <see cref="LeaseRenewInterval"/>.</summary>
        private Func<Task> MakeLeaseRenewer(PHD2Guider guiderClient, CancellationToken ct) {
            var lastRenew = DateTimeOffset.UtcNow;
            return async () => {
                var now = DateTimeOffset.UtcNow;
                if (now - lastRenew <= LeaseRenewInterval) {
                    return;
                }
                // Renewal is best-effort: a transient RPC fault must not abort a long adjust
                // session (the interval leaves ~2.5 renew windows inside the 600s lease, and an
                // expired lease just makes captures fail → the paused path).
                try {
                    await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                    lastRenew = now;
                } catch (OperationCanceledException) {
                    throw;
                } catch (GuiderRpcException ex) {
                    LogLeaseRenewFailed(ex);
                } catch (InvalidOperationException ex) {
                    // Guider dropped mid-session — captures will fail the same way; let the
                    // solve-failure path (pause + retry) own the outcome.
                    LogLeaseRenewFailed(ex);
                }
            };
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
        }

        // ── capture + solve plumbing ─────────────────────────────────────────────────────────

        /// <summary>Seed frames must solve — retry a few times, then fail the routine (a seed that
        /// can't solve means clouds/focus problems the live loop can't fix either).</summary>
        private async Task<PolarAlignSolveOutcome> SolveSeedFrameAsync(
                LiveCapture capture, string frameId,
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var anyFrameReachedSolver = false;
            for (var attempt = 1; attempt <= SeedSolveAttempts; attempt++) {
                var timing = new FrameTiming();
                // One id per attempt, on frame_started and frame_complete alike.
                var attemptId = frameId + "-" + attempt.ToString(CultureInfo.InvariantCulture);
                var s = await capture(attemptId, hint, settings, timing, ct).ConfigureAwait(false);
                await CompleteFrameAsync(gen, attemptId, s, timing, s.Success ? 0 : attempt).ConfigureAwait(false);
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
            throw new RoutineFailedException("seed_solve_failed",
                $"the {frameId} seed frame failed to solve after {SeedSolveAttempts} attempts — check focus, sky conditions, and the optics configuration of the camera in use");
        }

        /// <summary>One loop body: ask the guider for a saved solver frame, await its
        /// <c>SingleFrameComplete</c> event (the RPC only acks — the saved-FITS path arrives on the
        /// event), then hand the file to the solver. The FITS is deleted afterwards (§45.5 — PA
        /// frames never pollute the catalogue). A failed capture or solve returns
        /// <c>Success=false</c>; only transport-level faults throw.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A corrupt/truncated guider FITS or solver fault is one failed solve, not a routine abort (§45.11); configuration exceptions are deliberately excluded and fail the routine with an actionable message.")]
        private async Task<PolarAlignSolveOutcome> CaptureAndSolveAsync(
                PHD2Guider guiderClient, string workDir, string frameId,
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
            guiderClient.SingleFrameComplete += OnComplete;
            try {
                // The capture RPC gets the same one-failed-solve treatment as the solver call
                // below: a daemon-side rejection (camera busy, guiding running) or a dropped
                // guider is transient — it must feed the consecutive-failure/pause path (and the
                // seed retry loop), not hard-fail the routine (§45.11). The rejection text is kept
                // so an exhausted seed loop can say "the daemon refused the capture" instead of
                // blaming focus/sky (capture_rejected).
                try {
                    var exposureMs = Math.Max(1, (int)Math.Round(settings.ExposureSeconds * 1000.0));
                    await guiderClient.CaptureSolverFrameAsync(
                        exposureMs: exposureMs, binning: binning > 1 ? binning : null,
                        gain: null, subframe: null, path: null, save: true, ct).ConfigureAwait(false);
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
                var host = guiderClient.ConnectedHost;
                if (string.IsNullOrEmpty(host)) {
                    RecordCaptureFault(gen, frameId, "guider connection endpoint unknown — cannot fetch the saved frame");
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
                try {
                    await _frameFetcher.FetchAsync(
                        host, guiderClient.ConnectedRpcPort, filename, localPath, ct).ConfigureAwait(false);
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
                    return await _solver.SolveAsync(localPath, binning, hint?.RaDeg, hint?.DecDeg, ct).ConfigureAwait(false);
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
                guiderClient.SingleFrameComplete -= OnComplete;
                TryDeleteFile(localPath);
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

        private static (double RaDeg, double DecDeg) FitAxis(
                PolarAlignSolveOutcome a, PolarAlignSolveOutcome b, double rotationDeg, bool north, bool raIncreasing) {
            try {
                return PolarAlignGeometry.FitAxisTwoPoint(
                    a.RaDegJnow, a.DecDegJnow, b.RaDegJnow, b.DecDegJnow, rotationDeg, north, raIncreasing);
            } catch (ArgumentOutOfRangeException ex) {
                // Every geometry rejection (coincident pointings, inconsistent chord, axis > 30°
                // from the pole) is a user-actionable seed failure, not an internal error.
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
        }

        private (double RaDeg, double DecDeg)? CurrentMountPointingJnow() {
            var pos = _mount.GetCurrentPosition()?.Transform(Epoch.JNOW);
            if (pos is null) {
                return null;
            }
            // #1536 — a synced mount can report Dec past ±90° (the Dec axis is over the pole). The same sky
            // point is (RA+180°, 180°−Dec); folding keeps the Dec axis where it is, so the seed slew stays a
            // pure RA turn the mount accepts. Clamping to the pole instead would move the Dec axis between
            // the two seed frames, which the two-point fit does not model.
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
        private async Task FailRoutineAsync(string reason, Exception ex, int gen) {
            LogRoutineFailed(reason, ex);
            lock (_gate) {
                if (_generation != gen) {
                    return; // superseded between the catch filter and here — the successor owns state
                }
                _state = "failed";
                _active = false;
            }
            await PublishErrorAsync(reason, ex.Message).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("failed", CancellationToken.None).ConfigureAwait(false);
            await ReleaseLeaseBestEffortAsync(CancellationToken.None).ConfigureAwait(false);
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
                // from its own seed turn (#1727), so a run stopped there records a real measurement too,
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
                    Outcome: outcome);
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
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Stop must always complete; the run task's own boundary already logged any failure.")]
        private async Task AwaitRunUnwindAsync(Task run) {
            try {
                await run.WaitAsync(RunUnwindGrace).ConfigureAwait(false);
            } catch (TimeoutException) {
                LogRunUnwindTimedOut();
            } catch (Exception) {
                // Already handled + logged at the run task's boundary.
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Hand-back is best-effort on every exit path; a mount fault here must not mask the routine's outcome. Log-and-recover boundary.")]
        private void RestoreTrackingBestEffort(bool? priorTracking) {
            if (priorTracking is not bool tracking) {
                return;
            }
            try {
                _mount.SetTrackingEnabled(tracking);
            } catch (Exception ex) {
                LogTrackingRestoreFailed(ex);
            }
        }

        // Releasing the lease must not fail Stop: the guider may already be disconnected (nothing to
        // release, and the lease auto-expires), or the daemon may reject the call.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Lease release is best-effort: Stop must always succeed and the lease auto-expires. Log-and-recover boundary.")]
        private async Task ReleaseLeaseBestEffortAsync(CancellationToken ct) {
            PHD2Guider guiderClient;
            try {
                guiderClient = _guider.RequireConnectedGuider();
            } catch (InvalidOperationException) {
                return;
            }
            try {
                await guiderClient.SetPaSessionAsync(active: false, timeoutS: null, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogLeaseReleaseFailed(ex);
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

        private Task PublishStateEventAsync(string eventType, string state) =>
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
            lock (_gate) {
                stage = _stage;
            }
            return PublishAsync(WsEventCatalog.PolarAlignProgress, new JsonObject {
                ["stage"] = stage,
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
        private async Task PublishAsync(string eventType, JsonObject payload) {
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

        [LoggerMessage(EventId = 4529, Level = LogLevel.Information, Message = "§45 polar-align live readout {FrameId}: alt {AltArcmin:F2}′ az {AzArcmin:F2}′")]
        private partial void LogLiveReadout(string? frameId, double altArcmin, double azArcmin);

        [LoggerMessage(EventId = 4530, Level = LogLevel.Information, Message = "§45 polar-align finished ({Outcome}): final error {TotalArcmin}′ (alt {AltArcmin}′ az {AzArcmin}′) after {Iterations} live iterations")]
        private partial void LogFinalError(string outcome, double? totalArcmin, double? altArcmin, double? azArcmin, int iterations);

        [LoggerMessage(EventId = 4510, Level = LogLevel.Information, Message = "§45 polar-align routine started at the {Stage} stage (PA-session lease acquired)")]
        private partial void LogStarted(string stage);

        [LoggerMessage(EventId = 4511, Level = LogLevel.Information, Message = "§45 polar-align routine stopped (PA-session lease released)")]
        private partial void LogStopped();

        [LoggerMessage(EventId = 4512, Level = LogLevel.Warning, Message = "§45 PA-session lease release failed (best-effort; the lease auto-expires)")]
        private partial void LogLeaseReleaseFailed(Exception exception);

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

        [LoggerMessage(EventId = 4518, Level = LogLevel.Warning, Message = "§45 polar-align run task did not unwind within the stop grace period; abandoning (its cleanup still runs on return)")]
        private partial void LogRunUnwindTimedOut();

        [LoggerMessage(EventId = 4519, Level = LogLevel.Warning, Message = "§45.13 polar-alignment session row insert failed (best-effort)")]
        private partial void LogSessionRowFailed(Exception exception);

        [LoggerMessage(EventId = 4520, Level = LogLevel.Warning, Message = "§45 PA-session lease renewal failed (best-effort; retrying next iteration — the lease has margin)")]
        private partial void LogLeaseRenewFailed(Exception exception);

        [LoggerMessage(EventId = 4521, Level = LogLevel.Information, Message = "§45 live frame unavailable: {Reason}")]
        private partial void LogLiveFrameUnavailable(string reason);

        [LoggerMessage(EventId = 4522, Level = LogLevel.Information, Message = "§45 live frame fetch from the guider failed")]
        private partial void LogLiveFrameFetchFailed(Exception exception);

        [LoggerMessage(EventId = 4524, Level = LogLevel.Information, Message = "§45.4 polar-align binning {Binning}×{Binning} ({Source}; guide camera frame {Width}×{Height})")]
        private partial void LogBinningChosen(int binning, string source, int width, int height);

        [LoggerMessage(EventId = 4525, Level = LogLevel.Information, Message = "§45.4 the guider did not report the guide camera's frame size — auto binning stays at 1×1: {Reason}")]
        private partial void LogFrameSizeUnavailable(string reason);

        [LoggerMessage(EventId = 4531, Level = LogLevel.Information, Message = "§45 polar-align turned tracking on for the seed slew (restored on exit)")]
        private partial void LogTrackingEnabled();

        [LoggerMessage(EventId = 4532, Level = LogLevel.Warning, Message = "§45 polar-align could not turn tracking on")]
        private partial void LogTrackingEnableFailed(Exception exception);

        [LoggerMessage(EventId = 4533, Level = LogLevel.Warning, Message = "§45 polar-align tracking dropped before the seed slew — turning it back on")]
        private partial void LogTrackingDropped();

        [LoggerMessage(EventId = 4526, Level = LogLevel.Warning, Message = "§45 polar-align seed slew refused by the mount: {Reason}")]
        private partial void LogSeedSlewRefused(string reason);

        [LoggerMessage(EventId = 4523, Level = LogLevel.Information, Message = "§45 polar alignment: the mount is parked — unparking it before the seed slew")]
        private partial void LogUnparking();
    }
}
