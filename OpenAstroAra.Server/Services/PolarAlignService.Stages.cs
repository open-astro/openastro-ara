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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// The two measurements of a polar-align run (<see cref="PolarAlignStages"/>). Both start from the mount's Home
    /// position (tube at the pole, counterweights down) and make NINA's Three Point Polar Alignment: three points with two
    /// timed RA-motor turns of <c>polar_align.seed_rotation_deg</c> between them (<see cref="TurnRaAsync"/>), each point
    /// the average of <c>polar_align.frames_per_point</c> frames (<see cref="SolveSeedFrameAsync"/>), and the axis fitted
    /// as the circle through the three solved pointings (<see cref="FitThreePointOrFail"/>). The turns go the profile's
    /// <c>polar_align.east_direction</c>; no measurement slews the mount or moves its Dec axis.
    /// <b>Align</b>: the measurement, then the live loop that reads the user's knob moves until Done or Stop.
    /// <b>Verify</b>: the measurement again once Done sent the mount Home, with a verdict beside the adjust loop's last
    /// readout; nothing is adjusted. Both measure with the profile's <c>polar_align.camera</c>: the guide camera, which
    /// solves a frame a second, or the main camera.
    /// </summary>
    public sealed partial class PolarAlignService {

        private const int CheckSolves = 3;

        /// <summary>The fit is refused when a solved turn is smaller than this many times the turn the motor was told: the
        /// mount barely turned (a slipping clutch, a stalled motor).</summary>
        internal const double MinSolvedTurnRatio = 0.3;

        /// <summary>The fit is refused when a solved turn is larger than this many times the turn the motor was told. A
        /// timed turn overruns when its stop command stalls on a slow link (4 s at 2°/s adds 8° to a 10° turn), and three
        /// distinct points still fit the axis: only a turn far outside what a late stop explains is refused.</summary>
        internal const double MaxSolvedTurnRatio = 3.0;

        /// <summary>The fit is refused when two of the three solved points are closer than this on the sky: the frames
        /// show the same field.</summary>
        internal const double MinPointSeparationArcsec = 20.0;

        /// <summary>Where the first frame of a measurement is looked for: the celestial pole of the site's hemisphere,
        /// which the tube points at in the Home position. Each later frame is looked for at the solve before it.</summary>
        private static (double RaDeg, double DecDeg) PoleHint(bool north) => (0.0, north ? 90.0 : -90.0);

        /// <summary>The signed size of each of a measurement's two RA turns (<c>polar_align.seed_rotation_deg</c>): the way
        /// the profile's <c>polar_align.east_direction</c> says (east by default in the north, west in the south).</summary>
        private static double SignedTurnDeg(PolarAlignSettingsDto settings, SiteSettingsDto site) =>
            PolarAlignEastDirection.Sign(settings.EastDirection, site.LatitudeDeg) * settings.SeedRotationDeg;

        /// <summary>The camera a measurement shoots: the main camera (<paramref name="mainCamera"/>, when the profile's
        /// <c>polar_align.camera</c> is <c>main</c>) or the guide camera through the guider.</summary>
        /// <param name="blindFailover">A main-camera frame may fall back to the blind solver: a measurement's points
        /// (the mount can be degrees off the pole), never the live loop's frames, which take one fast look and shoot again.</param>
        private LiveCapture CaptureWith(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, string workDir, bool blindFailover, int gen) =>
            mainCamera is not null
                ? (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover, gen, timing, token)
                : (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token);

        /// <summary>One main-camera frame: the camera's snapshot, solved with the main optics. A busy
        /// camera or an unsolved frame is one failed iteration (the loop's pause/retry path); unset
        /// optics fail the routine with the solver's own message.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A camera or solver fault on one frame is one failed solve, not a routine abort (§45.11); configuration exceptions are excluded and fail the routine.")]
        private async Task<PolarAlignSolveOutcome> CaptureMainCameraAsync(
                IPolarAlignMainCameraSolver mainCamera, string frameId, (double RaDeg, double DecDeg)? hint,
                PolarAlignSettingsDto settings, bool blindFailover, int gen, FrameTiming timing, CancellationToken ct) {
            var exposure = settings.CoarseExposureSeconds; // always > 0: PolarAlignSettingsClamp on every read
            timing.ExposureSeconds = exposure;
            var start = Stopwatch.GetTimestamp();
            await PublishFrameStartedAsync(gen, frameId, exposure).ConfigureAwait(false);
            try {
                var s = await mainCamera.CaptureAndSolveAsync(exposure, hint, blindFailover, ct).ConfigureAwait(false);
                // A result, solved or not, means the frame reached the solver: whatever failed before was
                // the capture layer's and is over — an unsolved pole field is a sky/focus answer, like the
                // guider path's. (A stale "camera busy" here would relabel it capture_rejected.)
                lock (_gate) {
                    if (_generation == gen) {
                        _framesCaptured++;
                        _lastFrameId = frameId;
                        _lastCaptureFault = null;
                    }
                }
                return s;
            } catch (OperationCanceledException) {
                throw;
            } catch (OpenAstroAra.PlateSolving.PlateSolverConfigurationException) {
                throw; // user-fixable setup problem — fail the routine with the solver's message
            } catch (Exception ex) {
                // Busy, disconnected, a store or driver fault, a timeout: all the capture layer's, so an
                // exhausted seed quotes the message (capture_rejected) instead of blaming focus and sky.
                RecordCaptureFault(gen, frameId, ex.Message);
                return new PolarAlignSolveOutcome(false, 0, 0);
            } finally {
                // One figure: the main-camera path does not split capture from solve.
                timing.Capture = Stopwatch.GetElapsedTime(start);
            }
        }

        // ── align ────────────────────────────────────────────────────────────────────────────

        /// <summary>The align measurement and its live adjust loop. Returns when Done ends the loop
        /// (<see cref="RunEnd.Done"/>): the frame in flight is abandoned and the run goes on (PolarAlignService.Chain.cs).
        /// A Stop cancels <paramref name="ct"/>, as everywhere.</summary>
        /// <param name="mainCamera">The main camera when the profile's <c>polar_align.camera</c> is <c>main</c>; null = the
        /// guide camera.</param>
        private async Task RunAlignStageAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, SiteSettingsDto site,
                PolarAlignSettingsDto settings, string workDir, Func<Task> renewLeaseIfDue, RunEnd end, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            await BeginMeasurementAsync(gen, PolarAlignStages.Align, "seeding").ConfigureAwait(false);
            await ReadGuideFrameAsync(guiderClient, mainCamera, settings, ct).ConfigureAwait(false);
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);

            // The seed: three points with two RA turns of SeedRotationDeg between them (NINA's 10° steps, 20° in all).
            // The axis is the circle through the three solved pointings; the turns the motor was told only cross-check it.
            var turn = SignedTurnDeg(settings, site);
            var capture = CaptureWith(guiderClient, mainCamera, workDir, blindFailover: true, gen);
            var (a, aTime) = await SolveSeedFrameAsync(capture, "seed-a", PoleHint(north), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-a", a.RaDegJnow, a.DecDegJnow);
            await TurnRaAsync(turn, gen, ct).ConfigureAwait(false);
            var (b, bTime) = await SolveSeedFrameAsync(capture, "seed-b", (a.RaDegJnow, a.DecDegJnow), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-b", b.RaDegJnow, b.DecDegJnow);
            await TurnRaAsync(turn, gen, ct).ConfigureAwait(false);
            var (c, cTime) = await SolveSeedFrameAsync(capture, "seed-c", (b.RaDegJnow, b.DecDegJnow), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-c", c.RaDegJnow, c.DecDegJnow);

            var (axis, mismatch) = FitThreePointOrFail((a, aTime), (b, bTime), (c, cTime), turn, turn, north);
            var sigma = FitUncertaintyFor((a, aTime), (b, bTime), (c, cTime), north, site);
            // The loop runs on its own token, which Done cancels: the run's token stays for the chain that follows.
            using var adjusting = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loop = AdjustFromSeedAsync(axis, sigma, mismatch, c, cTime,
                CaptureWith(guiderClient, mainCamera, workDir, blindFailover: false, gen), renewLeaseIfDue, site, gen, adjusting.Token);
            if (await Task.WhenAny(loop, end.Done).ConfigureAwait(false) != loop) {
                await adjusting.CancelAsync().ConfigureAwait(false);
            }
            try {
                await loop.ConfigureAwait(false);
            } catch (OperationCanceledException) when (end.Done.IsCompleted && !ct.IsCancellationRequested) {
                // Done ended the loop where it was.
            }
        }

        /// <summary>§45.4 — for a guide-camera measurement, ask the guider for the guide camera's frame size once per run
        /// (the binning comes from it). A guider without the RPC, or a refused call, is not a failure: auto binning falls
        /// back to 1×1, the right answer for every guide camera anyway.</summary>
        private async Task ReadGuideFrameAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, PolarAlignSettingsDto settings, CancellationToken ct) {
            if (mainCamera is not null) {
                return;
            }
            _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            var chosenBinning = PolarAlignBinning.Resolve(settings.Binning, _runFrameSize);
            var binningSource = settings.Binning == PolarAlignBinning.Auto ? "auto" : "profile";
            LogBinningChosen(chosenBinning, binningSource, _runFrameSize?.Width ?? 0, _runFrameSize?.Height ?? 0);
        }

        /// <summary>The seed's axis error, then the live adjust loop until <paramref name="ct"/> is cancelled (Done, or a
        /// Stop). <paramref name="c"/> is the seed's
        /// last solve, taken at <paramref name="cTime"/>; <paramref name="sigma"/> is the fit's uncertainty, the live
        /// readout's; <paramref name="mismatchDeg"/> (how far the solved turns were from the told ones) is logged.</summary>
        private async Task AdjustFromSeedAsync(
                (double RaDeg, double DecDeg, double ConeDeg) axis, FitUncertainty sigma, double mismatchDeg, PolarAlignSolveOutcome c, DateTimeOffset cTime,
                LiveCapture liveCapture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            var (seedAltErr, seedAzErr) = PolarAlignGeometry.AxisError(
                axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, cTime);
            var seedPointing = PolarAlignGeometry.PointingAltAz(
                c.RaDegJnow, c.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, cTime);
            LogSeedFit(axis.RaDeg, axis.DecDeg, axis.ConeDeg, mismatchDeg, seedAltErr, sigma.AltSigmaArcmin, seedAzErr, sigma.AzSigmaArcmin, sigma.TotalSigmaArcmin, sigma.Solves, sigma.Floor);
            SetFitSigma(gen, sigma);

            // ── live adjust: tracking OFF makes the camera direction stationary in alt/az, so
            // every subsequent solve's alt/az delta from the seed pointing is purely the user's
            // knob adjustment (§45.8) — and the loop needs no further turns. Confirmed off, and checked every frame.
            await TurnTrackingOffForLiveLoopAsync(gen, ct).ConfigureAwait(false);
            NoteReadoutReference(gen, seedPointing, site.LatitudeDeg);
            SetErrors(gen, seedAltErr, seedAzErr, "adjusting", frameId: null); // keeps the seed-c attempt's id
            await PublishProgressAsync(gen, 0, seedAltErr, seedAzErr, solved: true).ConfigureAwait(false);
            ResetAlignerBaseline();
            await SolveLoopAsync(
                liveCapture,
                toError: (s, at) => {
                    // §45.8 reverse-projection: the pointing's alt/az delta since the seed is the knob
                    // adjustment seen from the camera (rigid mount, tracking stopped); far from the pole it
                    // is mapped back to the axis's shift (AxisShiftFromPointingShift). The azimuth-knob
                    // shift is an ANGLE about the vertical; it becomes an arc-distance at the AXIS altitude (≈ apparent pole altitude + current alt error),
                    // with the same east-positive sign convention as PolarAlignGeometry.AxisError.
                    var p = PolarAlignGeometry.PointingAltAz(
                        s.RaDegJnow, s.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, at);
                    var (altShiftDeg, azShiftDeg) = PolarAlignGeometry.AxisShiftFromPointingShift(
                        seedPointing.AltDeg, seedPointing.AzDeg, p.AltDeg - seedPointing.AltDeg,
                        Wrap180(p.AzDeg - seedPointing.AzDeg), site.LatitudeDeg);
                    var altErr = seedAltErr + altShiftDeg * 60.0;
                    var axisAltDeg = Math.Abs(site.LatitudeDeg)
                        + PolarAlignGeometry.BennettRefractionArcmin(Math.Abs(site.LatitudeDeg)) / 60.0
                        + altErr / 60.0;
                    var azErr = seedAzErr + azShiftDeg
                        * Math.Cos(axisAltDeg * Math.PI / 180.0) * 60.0 * (north ? 1.0 : -1.0);
                    return (altErr, azErr);
                },
                initialHint: (c.RaDegJnow, c.DecDegJnow), renewLeaseIfDue, gen, ct).ConfigureAwait(false);
        }

        // ── verify ───────────────────────────────────────────────────────────────────────────

        /// <summary>The verify measurement: the three-point measurement again, with a verdict beside the adjust loop's
        /// last readout (<paramref name="adjustReadout"/>).</summary>
        /// <param name="mainCamera">The main camera when the profile's <c>polar_align.camera</c> is <c>main</c>; null = the
        /// guide camera.</param>
        /// <param name="turnDeg">Each of its two RA turns, signed: the align measurement's own from Home, or the opposite
        /// way on a mount that cannot find Home, which brings the tube back to where the run started.</param>
        /// <returns>The verdict, also on the status from here on (<see cref="NoteVerdict"/>).</returns>
        private async Task<PolarAlignCheckResultDto> RunVerifyStageAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, string workDir, SiteSettingsDto site,
                PolarAlignSettingsDto settings, double turnDeg, (double AltArcmin, double AzArcmin)? adjustReadout, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);

            var capture = CaptureWith(guiderClient, mainCamera, workDir, blindFailover: true, gen);
            var (s1, t1) = await SolveSeedFrameAsync(capture, "check-1", PoleHint(north), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-1", s1.RaDegJnow, s1.DecDegJnow);
            await NoteCheckSolveAsync(gen, 1).ConfigureAwait(false);

            await TurnRaAsync(turnDeg, gen, ct).ConfigureAwait(false);
            var (s2, t2) = await SolveSeedFrameAsync(capture, "check-2", (s1.RaDegJnow, s1.DecDegJnow), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-2", s2.RaDegJnow, s2.DecDegJnow);
            await NoteCheckSolveAsync(gen, 2).ConfigureAwait(false);

            await TurnRaAsync(turnDeg, gen, ct).ConfigureAwait(false);
            // The point's mean time: the fit and the axis error are referred to it.
            var (s3, at) = await SolveSeedFrameAsync(capture, "check-3", (s2.RaDegJnow, s2.DecDegJnow), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-3", s3.RaDegJnow, s3.DecDegJnow);
            await NoteCheckSolveAsync(gen, 3).ConfigureAwait(false);

            var (axis, mismatch) = FitThreePointOrFail((s1, t1), (s2, t2), (s3, at), turnDeg, turnDeg, north);
            var verdict = CheckResultFor(axis, FitUncertaintyFor((s1, t1), (s2, t2), (s3, at), north, site), mismatch, site, at, adjustReadout);
            NoteVerdict(gen, verdict);
            return verdict;
        }

        /// <summary>The verify measurement's verdict from its fitted axis, measured at <paramref name="at"/>, beside the
        /// adjust loop's last readout.</summary>
        private PolarAlignCheckResultDto CheckResultFor(
                (double RaDeg, double DecDeg, double ConeDeg) axis, FitUncertainty sigma, double mismatch, SiteSettingsDto site, DateTimeOffset at,
                (double AltArcmin, double AzArcmin)? adjustReadout) {
            var (altErr, azErr) = PolarAlignGeometry.AxisError(axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, at);
            // Rounded first: the verdict and the total the client colours by must sit in the same band
            // (1.004′ is "excellent" at 1.00′, not "very_good" beside a 1.0′ readout).
            var total = Math.Round(Math.Sqrt(altErr * altErr + azErr * azErr), 2);
            // Compared with the adjust readout, both components, so a readout of the same size on the other side of
            // the pole still shows as a disagreement.
            double? fine = null;
            double? fineDelta = null;
            if (adjustReadout is var (fAlt, fAz)) {
                fine = Math.Sqrt(fAlt * fAlt + fAz * fAz);
                // The distance between the two measured axes, not the difference of their sizes: a
                // readout the same size on the other side of the pole is a 2′ disagreement, not 0.
                fineDelta = Math.Sqrt((altErr - fAlt) * (altErr - fAlt) + (azErr - fAz) * (azErr - fAz));
            }
            bool noisy;
            lock (_gate) {
                noisy = _measurementNoisy;
            }
            var result = new PolarAlignCheckResultDto(
                AltitudeErrorArcmin: Math.Round(altErr, 2),
                AzimuthErrorArcmin: Math.Round(azErr, 2),
                TotalErrorArcmin: total,
                ConeHalfAngleDeg: Math.Round(axis.ConeDeg, 3),
                TurnMismatchDeg: Math.Round(mismatch, 3),
                FineErrorArcmin: fine is double f ? Math.Round(f, 2) : null,
                FineDeltaArcmin: fineDelta is double g ? Math.Round(g, 2) : null,
                // #2072 — rated on the error plus twice its 1σ (Joey, 2026-11-21): 0.2′ ± 1.3′ is not "excellent".
                Verdict: VerdictFor(RatedErrorArcmin(total, sigma.TotalSigmaArcmin)),
                MeasuredAt: at,
                Noisy: noisy,
                AltitudeSigmaArcmin: Math.Round(sigma.AltSigmaArcmin, 2),
                AzimuthSigmaArcmin: Math.Round(sigma.AzSigmaArcmin, 2),
                TotalSigmaArcmin: Math.Round(sigma.TotalSigmaArcmin, 2));
            LogCheckResult(axis.RaDeg, axis.DecDeg, altErr, azErr, total, sigma.TotalSigmaArcmin, axis.ConeDeg, mismatch, fine, sigma.Solves, sigma.Floor);
            return result;
        }

        /// <summary>The verdict is known: it is on the status at once, with its errors and their σ, while the run still
        /// brings the mount Home.</summary>
        private void NoteVerdict(int gen, PolarAlignCheckResultDto result) {
            lock (_gate) {
                if (_generation != gen) {
                    return;
                }
                _checkResult = result;
                _altErrorArcmin = result.AltitudeErrorArcmin;
                _azErrorArcmin = result.AzimuthErrorArcmin;
                _fitSigma = CheckSigma(result); // the verdict's own σ, on the status and the session row
            }
        }

        /// <summary>Sidereal rate, degrees of RA per second of time.</summary>
        internal const double SiderealDegPerSecond = 360.98564736629 / 86400.0;

        /// <summary>The mount's axis from the three solved points of a measurement, taken at different times. Each
        /// solve is moved to the last one's time by the Earth's turn since it was taken: the RA axis is fixed to
        /// the ground, so the three pointings lie on one circle about it in the ground frame (NINA's TPPA works in
        /// alt/az for the same reason). The solves are the truth about what the mount did; the turns the motor was told
        /// only cross-check them. The fit is refused (<c>axis_fit_failed</c>) when two of the three points are closer than
        /// <see cref="MinPointSeparationArcsec"/> on the sky (the frames show the same field), or when a solved turn is
        /// under <see cref="MinSolvedTurnRatio"/> or over <see cref="MaxSolvedTurnRatio"/> times the told one.</summary>
        /// <param name="toldTurn1Deg">The turn the RA motor was told between the first two points (degrees). Only its
        /// size is compared: which way a positive motor rate turns is the driver's own convention, and from Home either
        /// way is safe, so a mount that turns the other way still fits.</param>
        /// <param name="toldTurn2Deg">The same between the last two points.</param>
        /// <returns>The axis, and the larger of the two differences between a solved turn and its told one.</returns>
        internal static ((double RaDeg, double DecDeg, double ConeDeg) Axis, double MismatchDeg) FitThreePointOrFail(
                (PolarAlignSolveOutcome S, DateTimeOffset At) p1, (PolarAlignSolveOutcome S, DateTimeOffset At) p2,
                (PolarAlignSolveOutcome S, DateTimeOffset At) p3, double toldTurn1Deg, double toldTurn2Deg, bool north) {
            // As solved, not moved in time: tracking holds a field that the mount did not turn away from.
            static double ApartArcsec(PolarAlignSolveOutcome a, PolarAlignSolveOutcome b) =>
                PolarAlignGeometry.SeparationDeg(a.RaDegJnow, a.DecDegJnow, b.RaDegJnow, b.DecDegJnow) * 3600.0;
            var closest = Math.Min(ApartArcsec(p1.S, p2.S), Math.Min(ApartArcsec(p2.S, p3.S), ApartArcsec(p1.S, p3.S)));
            if (!(closest >= MinPointSeparationArcsec)) {
                throw new RoutineFailedException("axis_fit_failed", string.Create(CultureInfo.InvariantCulture,
                    $"the three frames show the same field (two of them are {closest:F0}″ apart): the mount did not turn, or the camera looks straight down the RA axis — check the mount turns in RA (clutch tight, nothing blocking it), then start again"));
            }
            double ToRef((PolarAlignSolveOutcome S, DateTimeOffset At) p) =>
                p.S.RaDegJnow + (p3.At - p.At).TotalSeconds * SiderealDegPerSecond;
            var ra1 = ToRef(p1);
            var ra2 = ToRef(p2);
            var ra3 = p3.S.RaDegJnow;
            (double RaDeg, double DecDeg, double ConeDeg) axis;
            try {
                axis = PolarAlignGeometry.FitAxisThreePoint(
                    ra1, p1.S.DecDegJnow, ra2, p2.S.DecDegJnow, ra3, p3.S.DecDegJnow, north);
            } catch (ArgumentOutOfRangeException ex) {
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
            // The turn the solves show the motor made between two points: the turn about the fitted axis in the ground
            // frame (RA rising, whichever end of the axis the hemisphere names), plus the Earth's turn that tracking
            // followed meanwhile.
            var raSense = north ? 1.0 : -1.0;
            double MotorTurnDeg(double groundTurnDeg, DateTimeOffset from, DateTimeOffset to) =>
                (raSense * groundTurnDeg) + ((to - from).TotalSeconds * SiderealDegPerSecond);
            var solved1 = MotorTurnDeg(
                PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra1, p1.S.DecDegJnow, ra2, p2.S.DecDegJnow), p1.At, p2.At);
            var solved2 = MotorTurnDeg(
                PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra2, p2.S.DecDegJnow, ra3, p3.S.DecDegJnow), p2.At, p3.At);
            static bool AsTold(double solvedDeg, double toldDeg) =>
                Math.Abs(solvedDeg) >= MinSolvedTurnRatio * Math.Abs(toldDeg) && Math.Abs(solvedDeg) <= MaxSolvedTurnRatio * Math.Abs(toldDeg);
            var mismatch = Math.Max(
                Math.Abs(Math.Abs(solved1) - Math.Abs(toldTurn1Deg)), Math.Abs(Math.Abs(solved2) - Math.Abs(toldTurn2Deg)));
            if (!AsTold(solved1, toldTurn1Deg) || !AsTold(solved2, toldTurn2Deg)) {
                throw new RoutineFailedException("axis_fit_failed", string.Create(CultureInfo.InvariantCulture,
                    $"the mount did not turn as told: the frames show turns of {Math.Abs(solved1):F1}° and {Math.Abs(solved2):F1}° where the motor was told {Math.Abs(toldTurn1Deg):F1}° and {Math.Abs(toldTurn2Deg):F1}° (a slipping clutch, a stalled motor, a stop that landed far too late) — check the mount and start again"));
            }
            return (axis, mismatch);
        }

        /// <summary>The run's end: state <c>checked</c>, its event, the §45.13 row (outcome <c>checked</c>) and the lease
        /// released. The verdict is on the status already (<see cref="NoteVerdict"/>); the caller has brought the mount
        /// Home and confirmed it stopped.</summary>
        private async Task FinishCheckedAsync(int gen, PolarAlignCheckResultDto result) {
            lock (_gate) {
                if (_generation != gen) {
                    return;
                }
                _state = "checked";
                _active = false;
                RecordLastResultLocked();
            }
            await PublishAsync(WsEventCatalog.PolarAlignChecked, CheckResultJson(result)).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("checked", CancellationToken.None).ConfigureAwait(false);
            await ReleaseLeaseBestEffortAsync().ConfigureAwait(false);
        }

        /// <summary>A verdict as the <c>polar_align.checked</c> event carries it, and each <c>polar_align.stage</c> event
        /// once it is known (<c>check_result</c>): the status's <c>check_result</c> keys.</summary>
        private static JsonObject CheckResultJson(PolarAlignCheckResultDto result) => new() {
            ["altitude_error_arcmin"] = result.AltitudeErrorArcmin,
            ["azimuth_error_arcmin"] = result.AzimuthErrorArcmin,
            ["total_error_arcmin"] = result.TotalErrorArcmin,
            ["cone_half_angle_deg"] = result.ConeHalfAngleDeg,
            ["turn_mismatch_deg"] = result.TurnMismatchDeg,
            ["fine_error_arcmin"] = result.FineErrorArcmin,
            ["fine_delta_arcmin"] = result.FineDeltaArcmin,
            ["verdict"] = result.Verdict,
            ["noisy"] = result.Noisy,
            ["altitude_sigma_arcmin"] = result.AltitudeSigmaArcmin,
            ["azimuth_sigma_arcmin"] = result.AzimuthSigmaArcmin,
            ["total_sigma_arcmin"] = result.TotalSigmaArcmin,
            ["measured_at"] = result.MeasuredAt.ToString("O", CultureInfo.InvariantCulture),
        };

        /// <summary>The verdict bands the client's rating uses (polar_error_rating.dart), applied to the error plus twice
        /// its 1σ (<see cref="RatedErrorArcmin"/>, #2072).</summary>
        internal static string VerdictFor(double totalArcmin) =>
            totalArcmin <= 1.0 ? "excellent"
            : totalArcmin <= 3.0 ? "very_good"
            : totalArcmin <= 10.0 ? "good"
            : totalArcmin <= 30.0 ? "rough"
            : "far_off";

        private Task NoteCheckSolveAsync(int gen, int done) {
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                _checkSolvesDone = done;
            }
            return PublishStageAsync(gen);
        }

        /// <summary>A measurement's deliberate switch-on before its turns — the last run's exit or the live loop left
        /// tracking off on purpose (NINA turns tracking on before its measurement too). Distinct from the turn's "tracking
        /// dropped" (<see cref="KeepTrackingOnForTurnAsync"/>), which is a mid-run fault.</summary>
        private async Task EnsureTrackingOnAsync(int gen, CancellationToken ct) {
            if (_mount.GetInfo().TrackingEnabled) {
                return;
            }
            await TurnTrackingOnOrFailAsync(gen, ct).ConfigureAwait(false);
            LogTrackingEnabled();
        }

        // ── state + events ───────────────────────────────────────────────────────────────────

        /// <summary>A measurement starts afresh (<see cref="BeginMeasurementLocked"/>), announced by its
        /// <c>polar_align.stage</c> event.</summary>
        private Task BeginMeasurementAsync(int gen, string stage, string state) {
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                BeginMeasurementLocked(stage, state);
            }
            LogStage(stage, state);
            return PublishStageAsync(gen);
        }

        /// <summary>Caller holds <c>_gate</c>. The run's next measurement, in <paramref name="state"/>: no earlier readout
        /// stands for it on the status, in a session row written by a Stop before its first solve, or in the client's
        /// fold.</summary>
        private void BeginMeasurementLocked(string stage, string state) {
            _stage = stage;
            _state = state;
            _checkSolvesDone = 0;
            _altErrorArcmin = null;
            _azErrorArcmin = null;
            _lastFrame = null;
            _measurementNoisy = false;
        }

        /// <summary>The next state of the chain after Done (<c>homing</c>, <c>checking</c>), announced by its
        /// <c>polar_align.stage</c> event; the stage's readings stay.</summary>
        private Task SetChainStateAsync(int gen, string state) {
            string? stage;
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                _state = state;
                stage = _stage;
            }
            LogStage(stage, state);
            return PublishStageAsync(gen);
        }

        /// <summary><c>polar_align.stage</c>: the run's stage and state, the verify measurement's solve count, and its
        /// verdict (<c>check_result</c>) once it is known.</summary>
        private Task PublishStageAsync(int gen) {
            JsonObject payload;
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                payload = new JsonObject {
                    ["stage"] = _stage,
                    ["state"] = _state,
                    ["check_solves_done"] = _checkSolvesDone,
                    ["check_solves_total"] = CheckSolves,
                };
                if (_checkResult is { } verdict) {
                    payload["check_result"] = CheckResultJson(verdict);
                }
            }
            return PublishAsync(WsEventCatalog.PolarAlignStage, payload);
        }

        [LoggerMessage(EventId = 4534, Level = LogLevel.Information, Message = "§45 polar-align stage {Stage} ({State})")]
        private partial void LogStage(string? stage, string state);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Information, Message = "§45 polar-align verify measurement: axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ total {TotalArcmin:F2}′ ± {TotalSigmaArcmin:F2}′ (1σ); cone {ConeDeg:F2}°; turn mismatch {MismatchDeg:F3}°; the adjust readout was {FineArcmin}′; solves {Solves}; σ floor {Floor}")]
        private partial void LogCheckResult(double axisRaDeg, double axisDecDeg, double altArcmin, double azArcmin, double totalArcmin, double totalSigmaArcmin, double coneDeg, double mismatchDeg, double? fineArcmin, string solves, string floor);
    }
}
