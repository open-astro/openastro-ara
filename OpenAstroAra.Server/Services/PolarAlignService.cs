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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
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
    /// <para><b>State machine</b> (the <c>state</c> string in <see cref="PolarAlignStateDto"/>):
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

        private bool _active;
        private string _state = "idle";
        private int _framesCaptured;
        private string? _lastFrameId;
        private double? _altErrorArcmin;
        private double? _azErrorArcmin;
        private int _liveIterations;
        private DateTimeOffset _startedAt;

        // Run-generation token: Stop abandons a run task that won't unwind within the grace period
        // (a hung guider RPC), and a new Start may then launch a fresh run while the zombie is still
        // draining. Every state write / WS publish from a run checks its generation first, so a
        // superseded run's late effects are no-ops (its unique work dir keeps the files apart too).
        private int _generation;

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

        public PolarAlignService(
                GuiderService guider,
                ILogger<PolarAlignService> logger,
                IPolarAlignFrameSolver solver,
                ITelescopeMediator mount,
                IProfileStore profileStore,
                IWsBroadcaster? ws = null,
                IPolarAlignmentLog? log = null,
                IPolarAlignFrameFetcher? frameFetcher = null) {
            _guider = guider;
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
                    LastFrameId: _lastFrameId));
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
        public async Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) {
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
                var mountInfo = _mount.GetInfo();
                if (mountInfo?.Connected != true) {
                    throw new InvalidOperationException("mount is not connected");
                }
                var site = _profileStore.GetSiteSettings();
                if (!(double.IsFinite(site.LatitudeDeg) && Math.Abs(site.LatitudeDeg) > 0 && Math.Abs(site.LatitudeDeg) <= 90)) {
                    throw new InvalidOperationException(
                        "site latitude must be configured (non-zero) before polar alignment — the routine measures against your site's celestial pole");
                }
                var settings = _profileStore.GetPolarAlignSettings();
                await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                int gen;
                int startSeq;
                lock (_gate) {
                    _active = true;
                    _state = "seeding";
                    _framesCaptured = 0;
                    _lastFrameId = null;
                    _altErrorArcmin = null;
                    _azErrorArcmin = null;
                    _liveIterations = 0;
                    _startedAt = DateTimeOffset.UtcNow;
                    gen = ++_generation;
                    _lastCaptureFault = null; // fresh run — stale capture faults must not color its failures
                    // _abandonedCaptureDebts is deliberately NOT reset: a capture abandoned by the
                    // JUST-STOPPED run may still deliver its event into this run (review r2).
                    startSeq = ++_startSeq;
                }
                _runCts = new CancellationTokenSource();
                var runToken = _runCts.Token;
                _runTask = Task.Run(() => RunRoutineAsync(guiderClient, site, settings, gen, startSeq, runToken), CancellationToken.None);
                LogStarted();
                await PublishStateEventAsync(WsEventCatalog.PolarAlignStarted, "seeding").ConfigureAwait(false);
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
                    // A terminal "failed" is a real outcome a polling client must still see — a
                    // late/stray Stop must not clobber it back to "stopped" (the failure already
                    // published its error event and released the lease).
                    becameStopped = _state != "failed";
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
            _opLock.Dispose();
            if (_ownsFetcher && _frameFetcher is IDisposable d) {
                d.Dispose(); // only the self-constructed default — a DI-injected fetcher is container-owned
            }
        }

        // ── the routine ──────────────────────────────────────────────────────────────────────

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
                PHD2Guider guiderClient, SiteSettingsDto site, PolarAlignSettingsDto settings, int gen, int startSeq, CancellationToken ct) {
            // Per-run unique dir: an abandoned (zombie) run and its successor must never share
            // files — this run's cleanup deletes only its own directory.
            var workDir = Path.Combine(Path.GetTempPath(), "ara-polar-align", Guid.NewGuid().ToString("N"));
            bool? priorTracking = null;
            try {
                Directory.CreateDirectory(workDir);
                var north = site.LatitudeDeg > 0;

                // ── seed: frame A at the current pointing ──
                var hintA = CurrentMountPointingJnow();
                var a = await SolveSeedFrameAsync(guiderClient, workDir, "seed-a", hintA, settings, gen, ct).ConfigureAwait(false);

                // ── RA slew Δ, away from the meridian so the arc cannot cross it (a crossing risks
                // a meridian flip, which garbles the two-point fit — guider design §5 step 2). With
                // HA = LST − RA, decreasing RA increases HA (further west): slew west when already
                // west of the meridian, east when east.
                var info = _mount.GetInfo();
                var (mountRaDeg, mountDecDeg) = CurrentMountPointingJnow()
                    ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
                var lstDeg = info.SiderealTime * 15.0;
                var haDeg = Wrap180(lstDeg - mountRaDeg);
                var dirSign = haDeg >= 0 ? -1.0 : 1.0;
                var targetRaDeg = (mountRaDeg + dirSign * settings.SeedRotationDeg + 360.0) % 360.0;
                var target = new Coordinates(targetRaDeg, mountDecDeg, Epoch.JNOW, Coordinates.RAType.Degrees);
                if (!await _mount.SlewToCoordinatesAsync(target, ct).ConfigureAwait(false)) {
                    throw new RoutineFailedException("slew_failed", "the RA seed slew was rejected by the mount");
                }
                await _mount.WaitForSlew(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(settings.SettleSeconds), ct).ConfigureAwait(false);

                // ── seed: frame B, then the two-point axis fit. The rotation passed to the fit is
                // the SOLVED RA separation (what the mount actually did), not the commanded Δ.
                var b = await SolveSeedFrameAsync(guiderClient, workDir, "seed-b", CurrentMountPointingJnow(), settings, gen, ct).ConfigureAwait(false);
                var bTime = DateTimeOffset.UtcNow;
                var rotationDeg = Math.Abs(Wrap180(a.RaDegJnow - b.RaDegJnow));
                var axis = FitAxis(a, b, rotationDeg, north);
                var (altErr, azErr) = PolarAlignGeometry.AxisError(
                    axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, bTime);
                var seedPointing = PolarAlignGeometry.PointingAltAz(
                    b.RaDegJnow, b.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, bTime);

                // ── live adjust: tracking OFF makes the camera direction stationary in alt/az, so
                // every subsequent solve's alt/az delta from the seed pointing is purely the user's
                // knob adjustment (§45.8) — and the loop needs no further slews.
                priorTracking = info.TrackingEnabled;
                _mount.SetTrackingEnabled(false);
                SetErrors(gen, altErr, azErr, "adjusting", "seed-b");
                await PublishProgressAsync(gen, 0, altErr, azErr, solved: true).ConfigureAwait(false);
                await AdjustLoopAsync(guiderClient, workDir, site, settings, north, b, seedPointing, altErr, azErr, gen, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // Stop — the stopping path owns the state + events.
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
                if (NoNewerRunStarted(startSeq)) {
                    RestoreTrackingBestEffort(priorTracking);
                }
                TryDeleteWorkDir(workDir);
            }
        }

        private async Task AdjustLoopAsync(
                PHD2Guider guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings,
                bool north, PolarAlignSolveOutcome seedSolve,
                (double AltDeg, double AzDeg) seedPointing, double seedAltErr, double seedAzErr,
                int gen, CancellationToken ct) {
            var cadence = TimeSpan.FromMilliseconds(settings.LoopCadenceMs);
            var iteration = 0;
            var consecutiveFailures = 0;
            var lastRenew = DateTimeOffset.UtcNow;
            var lastGood = seedSolve;
            while (!ct.IsCancellationRequested) {
                var iterationStart = DateTimeOffset.UtcNow;
                if (iterationStart - lastRenew > LeaseRenewInterval) {
                    // Renewal is best-effort per iteration: a transient RPC fault must not abort a
                    // long adjust session (the interval leaves ~2.5 renew windows inside the 600s
                    // lease, and an expired lease just makes captures fail → the paused path).
                    try {
                        await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                        lastRenew = iterationStart;
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (GuiderRpcException ex) {
                        LogLeaseRenewFailed(ex);
                    } catch (InvalidOperationException ex) {
                        // Guider dropped mid-session — captures will fail the same way; let the
                        // solve-failure path (pause + retry) own the outcome.
                        LogLeaseRenewFailed(ex);
                    }
                }
                iteration++;
                var frameId = "live-" + iteration.ToString(CultureInfo.InvariantCulture);
                var s = await CaptureAndSolveAsync(guiderClient, workDir, frameId,
                    (lastGood.RaDegJnow, lastGood.DecDegJnow), settings, gen, ct).ConfigureAwait(false);
                if (!s.Success) {
                    consecutiveFailures++;
                    await PublishFrameCompleteAsync(gen, frameId, solved: false, consecutiveFailures).ConfigureAwait(false);
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
                    await Task.Delay(consecutiveFailures >= MaxConsecutiveSolveFailures ? PausedRetryDelay : cadence, ct).ConfigureAwait(false);
                    continue;
                }
                consecutiveFailures = 0;
                lastGood = s;
                var solveTime = DateTimeOffset.UtcNow;

                // §45.8 reverse-projection: the pointing's alt/az delta since the seed IS the knob
                // adjustment (rigid mount, tracking stopped). Azimuth-knob turns are rotations about
                // the vertical, so the azimuth ANGLE delta is shared by pointing and axis; it becomes
                // an arc-distance at the AXIS altitude (≈ apparent pole altitude + current alt error),
                // with the same east-positive sign convention as PolarAlignGeometry.AxisError.
                var p = PolarAlignGeometry.PointingAltAz(
                    s.RaDegJnow, s.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, solveTime);
                var altErr = seedAltErr + (p.AltDeg - seedPointing.AltDeg) * 60.0;
                var axisAltDeg = Math.Abs(site.LatitudeDeg)
                    + PolarAlignGeometry.BennettRefractionArcmin(Math.Abs(site.LatitudeDeg)) / 60.0
                    + altErr / 60.0;
                var azErr = seedAzErr + Wrap180(p.AzDeg - seedPointing.AzDeg)
                    * Math.Cos(axisAltDeg * Math.PI / 180.0) * 60.0 * (north ? 1.0 : -1.0);

                SetErrors(gen, altErr, azErr, "adjusting", frameId);
                lock (_gate) {
                    if (_generation == gen) {
                        _liveIterations = iteration;
                    }
                }
                await PublishFrameCompleteAsync(gen, frameId, solved: true, consecutiveSolveFailures: 0).ConfigureAwait(false);
                await PublishProgressAsync(gen, iteration, altErr, azErr, solved: true).ConfigureAwait(false);

                var elapsed = DateTimeOffset.UtcNow - iterationStart;
                if (elapsed < cadence) {
                    await Task.Delay(cadence - elapsed, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        // ── capture + solve plumbing ─────────────────────────────────────────────────────────

        /// <summary>Seed frames must solve — retry a few times, then fail the routine (a seed that
        /// can't solve means clouds/focus problems the live loop can't fix either).</summary>
        private async Task<PolarAlignSolveOutcome> SolveSeedFrameAsync(
                PHD2Guider guiderClient, string workDir, string frameId,
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            for (var attempt = 1; attempt <= SeedSolveAttempts; attempt++) {
                var s = await CaptureAndSolveAsync(guiderClient, workDir, frameId + "-" + attempt.ToString(CultureInfo.InvariantCulture), hint, settings, gen, ct).ConfigureAwait(false);
                await PublishFrameCompleteAsync(gen, frameId, s.Success, s.Success ? 0 : attempt).ConfigureAwait(false);
                if (s.Success) {
                    return s;
                }
            }
            string? captureFault;
            lock (_gate) {
                captureFault = _generation == gen ? _lastCaptureFault : null;
            }
            // A capture-layer fault (daemon rejected the capture, no frame ever reached the solver)
            // is NOT a sky/focus problem — blame the right layer, with the daemon's own message.
            if (captureFault is not null) {
                throw new RoutineFailedException("capture_rejected",
                    $"the {frameId} seed frame could not be captured after {SeedSolveAttempts} attempts: {captureFault}");
            }
            throw new RoutineFailedException("seed_solve_failed",
                $"the {frameId} seed frame failed to solve after {SeedSolveAttempts} attempts — check focus, sky conditions, and the guide optics configuration");
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
                (double RaDeg, double DecDeg)? hint, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var localPath = Path.Combine(workDir, frameId + ".fits");
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
            void OnComplete(object? sender, SingleFrameCompleteEventArgs e) {
                if (TryConsumeStaleDebt()) {
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
                        exposureMs: exposureMs, binning: settings.Binning > 1 ? settings.Binning : null,
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
                    // data (review r2: the common abandonment path, not just the timeout).
                    RecordAbandonedCapture();
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
                try {
                    return await _solver.SolveAsync(localPath, hint?.RaDeg, hint?.DecDeg, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    throw;
                } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException) {
                    throw; // user-fixable setup problem — fail the routine with the solver's message
                } catch (Exception ex) {
                    // A torn/partial FITS write or a solver crash on one frame is ONE failed solve.
                    LogCaptureFailed(frameId, ex.Message);
                    return new PolarAlignSolveOutcome(false, 0, 0);
                }
            } catch (TimeoutException) {
                // The daemon may still deliver this capture's event later — the next capture's
                // handler must swallow it rather than adopt its (stale) frame.
                RecordAbandonedCapture();
                RecordCaptureFault(gen, frameId, "SingleFrameComplete timed out");
                return new PolarAlignSolveOutcome(false, 0, 0);
            } finally {
                guiderClient.SingleFrameComplete -= OnComplete;
                TryDeleteFile(localPath);
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
                PolarAlignSolveOutcome a, PolarAlignSolveOutcome b, double rotationDeg, bool north) {
            try {
                return PolarAlignGeometry.FitAxisTwoPoint(
                    a.RaDegJnow, a.DecDegJnow, b.RaDegJnow, b.DecDegJnow, rotationDeg, north);
            } catch (ArgumentOutOfRangeException ex) {
                // Every geometry rejection (coincident pointings, inconsistent chord, axis > 30°
                // from the pole) is a user-actionable seed failure, not an internal error.
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
        }

        private (double RaDeg, double DecDeg)? CurrentMountPointingJnow() {
            var pos = _mount.GetCurrentPosition()?.Transform(Epoch.JNOW);
            return pos is null ? null : (pos.RADegrees, pos.Dec);
        }

        private void SetErrors(int gen, double altErrArcmin, double azErrArcmin, string state, string frameId) {
            lock (_gate) {
                if (_generation != gen) {
                    return; // a superseded run's late write is a no-op
                }
                _altErrorArcmin = altErrArcmin;
                _azErrorArcmin = azErrArcmin;
                _state = state;
                _lastFrameId = frameId;
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
            return PublishAsync(WsEventCatalog.PolarAlignProgress, new JsonObject {
                ["iteration"] = iteration,
                ["altitude_error_arcmin"] = Math.Round(altErrArcmin, 2),
                ["azimuth_error_arcmin"] = Math.Round(azErrArcmin, 2),
                ["total_error_arcmin"] = Math.Round(total, 2),
                ["zone"] = total < 10.0 ? "green" : total < 60.0 ? "yellow" : "red",
                ["solved"] = solved,
            });
        }

        private Task PublishFrameCompleteAsync(int gen, string frameId, bool solved, int consecutiveSolveFailures) =>
            !IsCurrent(gen) ? Task.CompletedTask :
            PublishAsync(WsEventCatalog.PolarAlignFrameComplete, new JsonObject {
                ["frame_id"] = frameId,
                ["solved"] = solved,
                ["consecutive_solve_failures"] = consecutiveSolveFailures,
            });

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

        [LoggerMessage(EventId = 4510, Level = LogLevel.Information, Message = "§45 polar-align routine started (PA-session lease acquired; seeding)")]
        private partial void LogStarted();

        [LoggerMessage(EventId = 4511, Level = LogLevel.Information, Message = "§45 polar-align routine stopped (PA-session lease released)")]
        private partial void LogStopped();

        [LoggerMessage(EventId = 4512, Level = LogLevel.Warning, Message = "§45 PA-session lease release failed (best-effort; the lease auto-expires)")]
        private partial void LogLeaseReleaseFailed(Exception exception);

        [LoggerMessage(EventId = 4513, Level = LogLevel.Warning, Message = "§45 WS publish of a polar-align event failed (best-effort)")]
        private partial void LogWsPublishFailed(Exception exception);

        [LoggerMessage(EventId = 4514, Level = LogLevel.Error, Message = "§45 polar-align routine failed: {Reason}")]
        private partial void LogRoutineFailed(string reason, Exception exception);

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
    }
}
