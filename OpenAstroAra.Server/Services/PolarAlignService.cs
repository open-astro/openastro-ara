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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §45 — polar alignment, run by openastro-guider's own Static PA routine (#1611). The guider
    /// owns the measurement: it loops the guide camera, tracks the chosen star's sub-pixel centroid
    /// through the RA rotation, fits the centre of rotation and decomposes the pole offset into the
    /// alt/az bolt corrections (<c>staticpa_start</c> / <c>staticpa_get_status</c>). Ara is the driver
    /// and the display: it preflights the mount and site, gets the guider looping with a star
    /// (no PA-session lease: the lease blocks <c>loop</c>, and the guider's own tool already refuses
    /// <c>guide</c> while it runs), starts the tool (auto mode when the guider's mount can slew, else
    /// manual mode with Ara doing the RA turns and <c>staticpa_measure</c>), then polls the status
    /// object and republishes <c>live_adjustment</c> on the existing wire shape.
    ///
    /// <para><b>State machine</b> (the <c>state</c> string in <see cref="PolarAlignStateDto"/>):
    /// <c>idle</c> → Start → <c>seeding</c> (looping, star pick, the guider's rotation + fit) →
    /// <c>adjusting</c> (the fit is in; each poll's live alt/az error is the user's knob readout) →
    /// Stop/Done → <c>stopped</c>. Losing the star for five consecutive polls parks the loop in
    /// <c>paused</c> (it keeps polling and resumes when the guider sees the star again); a fatal
    /// error (the guider refusing, the tool closing underneath us, a mount fault) lands in
    /// <c>failed</c>.</para>
    /// </summary>
    public sealed partial class PolarAlignService : IPolarAlignService, IDisposable {

        private readonly GuiderService _guider;
        private readonly ILogger<PolarAlignService> _logger;
        private readonly IWsBroadcaster? _ws;
        private readonly ITelescopeMediator _mount;
        private readonly IProfileStore _profileStore;
        private readonly IPolarAlignmentLog? _log;
        private readonly IPolarAlignFrameFetcher _frameFetcher;
        private readonly bool _ownsFetcher;
        private readonly Func<IGuideFocusService?>? _guideFocus;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _opLock = new(1, 1);

        internal const int MaxConsecutiveStarMisses = 5;
        private const int FindStarAttempts = 5;
        private const int ManualMeasurePolls = 30;

        internal TimeSpan RunUnwindGrace { get; set; } = TimeSpan.FromSeconds(15);
        /// <summary>Bench knob: how long a star pick or a manual-mode measurement waits for the
        /// guider's next frame between attempts (the exposure plus this).</summary>
        internal TimeSpan FrameWaitSlack { get; set; } = TimeSpan.FromMilliseconds(500);

        private bool _active;
        private string _state = "idle";
        private int _framesCaptured;
        private string? _lastFrameId;
        private double? _altErrorArcmin;
        private double? _azErrorArcmin;
        private int _liveIterations;
        private DateTimeOffset _startedAt;
        private int _generation;
        private CancellationTokenSource? _runCts;
        private Task? _runTask;

        public bool IsActive {
            get {
                lock (_gate) {
                    return _active;
                }
            }
        }

        public PolarAlignService(
                GuiderService guider,
                ILogger<PolarAlignService> logger,
                ITelescopeMediator mount,
                IProfileStore profileStore,
                IWsBroadcaster? ws = null,
                IPolarAlignmentLog? log = null,
                IPolarAlignFrameFetcher? frameFetcher = null,
                Func<IGuideFocusService?>? guideFocus = null) {
            _guider = guider;
            _logger = logger;
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
                    WaitingForCapture: false,
                    LastFrame: null));
            }
        }

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
            } catch (Exception ex) {
                LogLiveFrameFetchFailed(ex);
                return null;
            }
        }

        /// <summary>The guider's tool reads every frame it loops, so there is no single frame to
        /// request: accepted as a no-op in the adjust phase (the next poll is the next readout),
        /// 409 outside it, as before.</summary>
        public Task<OperationAcceptedDto> RequestCaptureAsync(string? idempotencyKey, CancellationToken ct) {
            lock (_gate) {
                if (!_active || _state is not ("adjusting" or "paused")) {
                    throw new InvalidOperationException("polar alignment is not in the adjust phase");
                }
            }
            return Task.FromResult(Accepted("polar-align.capture", idempotencyKey));
        }

        public async Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                lock (_gate) {
                    if (_active) {
                        return Accepted("polar-align.start", idempotencyKey);
                    }
                }
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
                var settings = _profileStore.GetPolarAlignSettings();
                int gen;
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
                }
                _runCts = new CancellationTokenSource();
                var runToken = _runCts.Token;
                _runTask = Task.Run(() => RunRoutineAsync(guiderClient, site, settings, gen, runToken), CancellationToken.None);
                LogStarted();
                await PublishStateEventAsync(WsEventCatalog.PolarAlignStarted, "seeding").ConfigureAwait(false);
                return Accepted("polar-align.start", idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        public Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) =>
            EndRoutineAsync("polar-align.stop", outcome: "aborted", idempotencyKey, ct);

        public Task<OperationAcceptedDto> CompleteAsync(string? idempotencyKey, CancellationToken ct) =>
            EndRoutineAsync("polar-align.complete", outcome: "complete", idempotencyKey, ct);

        private async Task<OperationAcceptedDto> EndRoutineAsync(
                string operationType, string outcome, string? idempotencyKey, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var (cts, run) = (_runCts, _runTask);
                _runCts = null;
                _runTask = null;
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
                    becameStopped = _state != "failed";
                    if (becameStopped) {
                        _state = "stopped";
                    }
                }
                if (wasActive) {
                    await WriteSessionRowBestEffortAsync(outcome, ct).ConfigureAwait(false);
                    await CloseGuiderToolBestEffortAsync(ct).ConfigureAwait(false);
                }
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
                d.Dispose();
            }
        }

        // ── the routine ──────────────────────────────────────────────────────────────────────

        [SuppressMessage("Design", "CA1032:Implement standard exception constructors",
            Justification = "Private, never serialized or thrown without a reason code.")]
        private sealed class RoutineFailedException(string reason, string message) : Exception(message) {
            public string Reason { get; } = reason;
        }

        private const string SlewRefusedMessage =
            "the mount refused the RA turn — slew it to about Dec 85° from the mount controls, then start again";

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The run task's boundary: every failure must land in the failed state and release the guider.")]
        private async Task RunRoutineAsync(
                PHD2Guider guiderClient, SiteSettingsDto site, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            bool? trackingToRestore = null;
            try {
                var north = site.LatitudeDeg > 0;
                var exposure = TimeSpan.FromSeconds(Math.Max(0.01, settings.ExposureSeconds));
                await UnparkIfParkedAsync(ct).ConfigureAwait(false);
                trackingToRestore = EnsureTrackingOn();
                await SetExposureBestEffortAsync(guiderClient, exposure, ct).ConfigureAwait(false);
                await guiderClient.LoopAsync(ct).ConfigureAwait(false);
                await FindStarWithRetriesAsync(guiderClient, exposure, ct).ConfigureAwait(false);

                Phd2StaticPaStatus status;
                var manual = false;
                try {
                    status = await guiderClient.StaticPaStartAsync(auto: true, north, ct).ConfigureAwait(false);
                } catch (GuiderRpcException ex) when (ex.Message.Contains("cannot slew", StringComparison.OrdinalIgnoreCase)) {
                    // The guider has no slewable mount of its own: Ara turns RA, the guider measures.
                    manual = true;
                    LogManualMode(ex.Message);
                    status = await guiderClient.StaticPaStartAsync(auto: false, north, ct).ConfigureAwait(false);
                }
                LogToolStarted(manual ? "manual" : "auto", status.CanSlew ?? false, status.PixelScale ?? 0, status.RefStar ?? -1);
                if (manual) {
                    await MeasureManualPointsAsync(guiderClient, settings, exposure, ct).ConfigureAwait(false);
                }
                await PollLoopAsync(guiderClient, settings, gen, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } catch (RoutineFailedException ex) when (IsCurrent(gen)) {
                await FailRoutineAsync(ex.Reason, ex, gen).ConfigureAwait(false);
            } catch (GuiderRpcException ex) when (IsCurrent(gen)) {
                await FailRoutineAsync("guider_refused", ex, gen).ConfigureAwait(false);
            } catch (InvalidOperationException ex) when (IsCurrent(gen)) {
                await FailRoutineAsync("guider_disconnected", ex, gen).ConfigureAwait(false);
            } catch (Exception ex) when (IsCurrent(gen)) {
                await FailRoutineAsync("internal_error", ex, gen).ConfigureAwait(false);
            } catch (Exception ex) {
                LogRoutineFailed("superseded", ex);
            } finally {
                // Manual mode leaves the mount at its last RA turn, which is where the adjust phase wants
                // it; tracking goes back to what the user had only if the routine turned it on.
                RestoreTrackingBestEffort(trackingToRestore);
            }
        }

        /// <summary>The guider's Static PA slews in RA (auto mode) and reads the star through the turn, and
        /// an Alpaca mount refuses SlewToCoordinatesAsync while not tracking ("requires Tracking to be
        /// true", the iOptron on the CM4, 2026-11-05). Tracking is turned on before the tool starts;
        /// returns the prior state when it was changed, so the exit path can put it back.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A mount fault while enabling tracking becomes the routine's tracking_off failure.")]
        private bool? EnsureTrackingOn() {
            var info = _mount.GetInfo();
            if (info is null || info.TrackingEnabled) {
                return null;
            }
            try {
                if (_mount.SetTrackingEnabled(true)) {
                    LogTrackingEnabled();
                    return false;
                }
            } catch (Exception ex) {
                LogTrackingEnableFailed(ex);
            }
            throw new RoutineFailedException("tracking_off",
                "the mount is not tracking and would not start — turn tracking on from the mount controls, then start again");
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Hand-back is best-effort; a mount fault here must not mask the routine's outcome.")]
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

        private async Task SetExposureBestEffortAsync(PHD2Guider guiderClient, TimeSpan exposure, CancellationToken ct) {
            try {
                await guiderClient.SetExposureMsAsync((int)Math.Round(exposure.TotalMilliseconds), ct).ConfigureAwait(false);
            } catch (GuiderRpcException ex) {
                LogExposureNotSet(ex.Message); // the guider keeps its own exposure; the routine still runs
            }
        }

        /// <summary>The guider picks its star on a frame it has already looped, so the first tries
        /// after <c>loop</c> may find nothing yet.</summary>
        private async Task FindStarWithRetriesAsync(PHD2Guider guiderClient, TimeSpan exposure, CancellationToken ct) {
            GuiderRpcException? last = null;
            for (var attempt = 1; attempt <= FindStarAttempts; attempt++) {
                await Task.Delay(exposure + FrameWaitSlack, ct).ConfigureAwait(false);
                try {
                    var star = await guiderClient.FindStarAsync(ct).ConfigureAwait(false);
                    LogStarPicked(star.X, star.Y, attempt);
                    return;
                } catch (GuiderRpcException ex) {
                    last = ex;
                }
            }
            throw new RoutineFailedException("no_star",
                $"the guider found no usable star in {FindStarAttempts} frames ({last?.Message}) — check the guide camera's focus and exposure, then start again");
        }

        /// <summary>Manual mode: the guider's Static PA needs three star positions, so Ara turns RA
        /// twice (away from the meridian, like the auto routine), re-picks the star, and asks the
        /// guider to record points 2 and 3.</summary>
        private async Task MeasureManualPointsAsync(PHD2Guider guiderClient, PolarAlignSettingsDto settings, TimeSpan exposure, CancellationToken ct) {
            var info = _mount.GetInfo();
            var (startRaDeg, decDeg) = CurrentMountPointingJnow()
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            var lstDeg = info.SiderealTime * 15.0;
            var haDeg = Wrap180(lstDeg - startRaDeg);
            var dirSign = haDeg >= 0 ? -1.0 : 1.0;
            var step = Math.Max(5.0, settings.SeedRotationDeg);
            for (var position = 2; position <= 3; position++) {
                var targetRaDeg = (startRaDeg + dirSign * step * (position - 1) + 720.0) % 360.0;
                var target = new Coordinates(targetRaDeg, decDeg, Epoch.JNOW, Coordinates.RAType.Degrees);
                bool slewed;
                try {
                    slewed = await TelescopeService.RunWithoutOpFaultsAsync(() => _mount.SlewToCoordinatesAsync(target, ct)).ConfigureAwait(false);
                } catch (SequenceEntityFailedException ex) {
                    LogSlewRefused(ex.Message);
                    throw new RoutineFailedException("slew_failed", SlewRefusedMessage);
                }
                if (!slewed) {
                    throw new RoutineFailedException("slew_failed", SlewRefusedMessage);
                }
                await _mount.WaitForSlew(ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, settings.SettleSeconds)), ct).ConfigureAwait(false);
                await FindStarWithRetriesAsync(guiderClient, exposure, ct).ConfigureAwait(false);
                await guiderClient.StaticPaMeasureAsync(position, ct).ConfigureAwait(false);
                // The point is read on the guider's next frame.
                var recorded = false;
                for (var poll = 0; poll < ManualMeasurePolls && !recorded; poll++) {
                    await Task.Delay(exposure + FrameWaitSlack, ct).ConfigureAwait(false);
                    var status = await guiderClient.StaticPaGetStatusAsync(ct).ConfigureAwait(false);
                    recorded = status.MeasuredPoints?.Any(p => p.Position == position) == true;
                }
                if (!recorded) {
                    throw new RoutineFailedException("measure_failed", $"the guider did not record star position #{position}");
                }
                LogManualPoint(position, targetRaDeg);
            }
        }

        private async Task PollLoopAsync(PHD2Guider guiderClient, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var cadence = TimeSpan.FromMilliseconds(Math.Max(100, settings.LoopCadenceMs));
            var misses = 0;
            var paused = false;
            while (!ct.IsCancellationRequested) {
                await Task.Delay(cadence, ct).ConfigureAwait(false);
                var status = await guiderClient.StaticPaGetStatusAsync(ct).ConfigureAwait(false);
                if (status.Active != true) {
                    throw new RoutineFailedException("guider_closed", "the guider closed its polar-alignment tool");
                }
                if (status.Calced != true) {
                    if (status.Aligning != true && status.Auto == true) {
                        throw new RoutineFailedException("fit_failed",
                            "the guider stopped measuring before it had a fit — it lost the star during the RA turn; start again");
                    }
                    continue; // still rotating / measuring
                }
                var live = status.LiveAdjustment;
                if (live?.AltErrorArcmin is not double alt || live.AzErrorArcmin is not double az) {
                    misses++;
                    if (misses >= MaxConsecutiveStarMisses && !paused) {
                        paused = true;
                        SetState(gen, "paused");
                        LogLoopPaused(misses);
                        await PublishStateEventAsync(WsEventCatalog.PolarAlignPaused, "paused").ConfigureAwait(false);
                    }
                    continue;
                }
                misses = 0;
                paused = false;
                int iteration;
                lock (_gate) {
                    if (_generation != gen) {
                        return;
                    }
                    _altErrorArcmin = alt;
                    _azErrorArcmin = az;
                    _state = "adjusting";
                    _framesCaptured++;
                    iteration = ++_liveIterations;
                    _lastFrameId = $"live-{iteration}";
                }
                var total = live.TotalErrorArcmin ?? Math.Sqrt(alt * alt + az * az);
                LogLiveReadout(iteration, alt, az, total);
                await PublishProgressAsync(gen, iteration, alt, az).ConfigureAwait(false);
            }
        }

        private (double RaDeg, double DecDeg)? CurrentMountPointingJnow() {
            var pos = _mount.GetCurrentPosition()?.Transform(Epoch.JNOW);
            if (pos is null) {
                return null;
            }
            // #1536 — a synced mount can report Dec past ±90°; fold to the same sky point.
            return Math.Abs(pos.Dec) > 90.0
                ? ((pos.RADegrees + 180.0) % 360.0, Math.CopySign(180.0, pos.Dec) - pos.Dec)
                : (pos.RADegrees, pos.Dec);
        }

        private static double Wrap180(double deg) => ((deg + 540.0) % 360.0) - 180.0;

        private bool IsCurrent(int gen) {
            lock (_gate) {
                return _generation == gen;
            }
        }

        private void SetState(int gen, string state) {
            lock (_gate) {
                if (_generation == gen) {
                    _state = state;
                }
            }
        }

        private async Task FailRoutineAsync(string reason, Exception ex, int gen) {
            LogRoutineFailed(reason, ex);
            lock (_gate) {
                if (_generation != gen) {
                    return;
                }
                _state = "failed";
                _active = false;
            }
            await PublishErrorAsync(reason, ex.Message).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("failed", CancellationToken.None).ConfigureAwait(false);
            await CloseGuiderToolBestEffortAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Session logging is best-effort record-keeping; the routine outcome must not depend on it.")]
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
            LogFinalError(outcome, record.FinalErrorArcmin, record.FinalAltErrorArcmin, record.FinalAzErrorArcmin, record.Iterations);
            try {
                await _log.InsertAsync(record, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogSessionRowFailed(ex);
            }
        }

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

        /// <summary>Hand the guider back as we found it: tool stopped and closed, the camera no
        /// longer looping. Every step is best-effort — a guider that already dropped is fine.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Hand-back is best-effort on every exit path; the guider's tool is torn down on its own if it isn't.")]
        private async Task CloseGuiderToolBestEffortAsync(CancellationToken ct) {
            PHD2Guider guiderClient;
            try {
                guiderClient = _guider.RequireConnectedGuider();
            } catch (InvalidOperationException) {
                return;
            }
            try {
                await guiderClient.StaticPaStopAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogToolCloseFailed("staticpa_stop", ex);
            }
            try {
                await guiderClient.StaticPaCloseAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogToolCloseFailed("staticpa_close", ex);
            }
            try {
                await guiderClient.StopCaptureAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogToolCloseFailed("stop_capture", ex);
            }
        }

        // ── events ───────────────────────────────────────────────────────────────────────────

        private Task PublishStateEventAsync(string eventType, string state) =>
            PublishAsync(eventType, new JsonObject { ["state"] = state });

        private Task PublishProgressAsync(int gen, int iteration, double altErrArcmin, double azErrArcmin) {
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
                ["solved"] = true,
            });
        }

        private Task PublishErrorAsync(string reason, string message) =>
            PublishAsync(WsEventCatalog.PolarAlignError, new JsonObject {
                ["reason"] = reason,
                ["message"] = message,
            });

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "WS publish is best-effort; a broadcaster fault must not abort the routine.")]
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

        // ── log ──────────────────────────────────────────────────────────────────────────────

        [LoggerMessage(EventId = 4526, Level = LogLevel.Information, Message = "§45 polar-align started (guider Static PA)")]
        private partial void LogStarted();

        [LoggerMessage(EventId = 4527, Level = LogLevel.Information, Message = "§45 polar-align star picked at ({X:F1}, {Y:F1}) on attempt {Attempt}")]
        private partial void LogStarPicked(double x, double y, int attempt);

        [LoggerMessage(EventId = 4528, Level = LogLevel.Information, Message = "§45 polar-align guider tool started: mode {Mode}, can_slew {CanSlew}, pixel scale {PixelScale:F2}″/px, ref star {RefStar}")]
        private partial void LogToolStarted(string mode, bool canSlew, double pixelScale, int refStar);

        [LoggerMessage(EventId = 4529, Level = LogLevel.Information, Message = "§45 polar-align live readout #{Iteration}: alt {AltArcmin:F2}′ az {AzArcmin:F2}′ total {TotalArcmin:F2}′")]
        private partial void LogLiveReadout(int iteration, double altArcmin, double azArcmin, double totalArcmin);

        [LoggerMessage(EventId = 4530, Level = LogLevel.Information, Message = "§45 polar-align ended ({Outcome}): total {TotalArcmin}′ alt {AltArcmin}′ az {AzArcmin}′ after {Iterations} readouts")]
        private partial void LogFinalError(string outcome, double? totalArcmin, double? altArcmin, double? azArcmin, int iterations);

        [LoggerMessage(EventId = 4531, Level = LogLevel.Information, Message = "§45 polar-align stopped")]
        private partial void LogStopped();

        [LoggerMessage(EventId = 4533, Level = LogLevel.Warning, Message = "§45 polar-align WS publish failed")]
        private partial void LogWsPublishFailed(Exception exception);

        [LoggerMessage(EventId = 4534, Level = LogLevel.Error, Message = "§45 polar-align routine failed ({Reason})")]
        private partial void LogRoutineFailed(string reason, Exception exception);

        [LoggerMessage(EventId = 4535, Level = LogLevel.Warning, Message = "§45 polar-align paused: the guider has not seen the star for {Misses} polls")]
        private partial void LogLoopPaused(int misses);

        [LoggerMessage(EventId = 4536, Level = LogLevel.Warning, Message = "§45 polar-align run did not unwind within the grace period; abandoning it")]
        private partial void LogRunUnwindTimedOut();

        [LoggerMessage(EventId = 4537, Level = LogLevel.Warning, Message = "§45 polar-align session row failed")]
        private partial void LogSessionRowFailed(Exception exception);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Debug, Message = "§45 polar-align live frame unavailable: {Reason}")]
        private partial void LogLiveFrameUnavailable(string reason);

        [LoggerMessage(EventId = 4540, Level = LogLevel.Debug, Message = "§45 polar-align live frame fetch failed")]
        private partial void LogLiveFrameFetchFailed(Exception exception);

        [LoggerMessage(EventId = 4541, Level = LogLevel.Warning, Message = "§45 polar-align RA turn refused: {Reason}")]
        private partial void LogSlewRefused(string reason);

        [LoggerMessage(EventId = 4542, Level = LogLevel.Information, Message = "§45 polar-align unparking the mount")]
        private partial void LogUnparking();

        [LoggerMessage(EventId = 4543, Level = LogLevel.Information, Message = "§45 polar-align manual mode: {Reason}")]
        private partial void LogManualMode(string reason);

        [LoggerMessage(EventId = 4544, Level = LogLevel.Information, Message = "§45 polar-align manual point #{Position} recorded at RA {RaDeg:F2}°")]
        private partial void LogManualPoint(int position, double raDeg);

        [LoggerMessage(EventId = 4545, Level = LogLevel.Warning, Message = "§45 polar-align guider hand-back {Rpc} failed")]
        private partial void LogToolCloseFailed(string rpc, Exception exception);

        [LoggerMessage(EventId = 4546, Level = LogLevel.Warning, Message = "§45 polar-align exposure not set on the guider: {Reason}")]
        private partial void LogExposureNotSet(string reason);

        [LoggerMessage(EventId = 4547, Level = LogLevel.Information, Message = "§45 polar-align turned tracking on for the guider's RA turn (restored on exit)")]
        private partial void LogTrackingEnabled();

        [LoggerMessage(EventId = 4548, Level = LogLevel.Warning, Message = "§45 polar-align could not turn tracking on")]
        private partial void LogTrackingEnableFailed(Exception exception);

        [LoggerMessage(EventId = 4549, Level = LogLevel.Warning, Message = "§45 polar-align could not restore tracking")]
        private partial void LogTrackingRestoreFailed(Exception exception);
    }
}
