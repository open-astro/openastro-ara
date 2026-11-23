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
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;
using PierSide = OpenAstroAra.Core.Enums.PierSide;
using OpenAstroAra.Core.Model;
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
    /// The three measurements of the polar-align routine; each Start runs ONE of them (#2082). Every one starts from where
    /// the mount points: the user pointed it 2–5° from the pole with the counterweights down and confirmed the start
    /// (#2048). No measurement sends the mount Home, slews it or moves its Dec axis, like NINA's Three Point Polar
    /// Alignment started from the current position. <b>Coarse</b>: the §45 seed-and-adjust routine on the <i>main
    /// camera's</i> frames — three solves with two RA-motor turns, the three-point axis fit, then the live loop that reads
    /// knob moves — rough only in its cadence (the main camera's longer exposure and solve); "Rough first (main camera)"
    /// in the app. <b>Fine</b>: the same routine on the profile's <c>polar_align.camera</c> — the guide camera, which
    /// solves a frame a second, by default. <b>Check</b>: three solves on that camera with two RA-motor turns
    /// of <c>CheckStepDeg</c>, an exact three-point fit, and a verdict beside the last run's readout. Each point of a
    /// measurement averages <c>polar_align.frames_per_point</c> frames (<see cref="SolveSeedFrameAsync"/>). Nothing is adjusted in
    /// the check; it says how good the alignment really is (#1591). A measurement never leads into another: the live loop
    /// runs until Stop or Done, and a check is a new, confirmed Start (from the adjust loop, it ends the loop first).
    /// Every Start's turns go the way its plan says (<see cref="PlanSweep"/>): the session's 30° sweep limit holds
    /// across repeated Starts and Checks.
    /// </summary>
    public sealed partial class PolarAlignService {

        // #2082 — a start this close to the pole is refused before anything moves: the camera would look along the RA axis,
        // the three pointings would coincide, and nothing tilts it off the axis any more (the Dec move is gone).
        internal const double OnAxisDecDeg = 87.0;
        private const int CheckSolves = 3;
        // The check refuses a verdict when the solved turns disagree with the mount's by more than this, or
        // when the three pointings sit this close to one line through the axis (a Dec-85° circle is ~5°).
        internal const double MaxCheckTurnMismatchDeg = 5.0;
        internal const double MinCheckConeDeg = 1.0;

        // ── the main camera: coarse ("Rough first"), and fine with polar_align.camera = main ──

        /// <summary>The seed-and-adjust measurement on the main camera (#2082): the coarse stage ("Rough first (main
        /// camera)", after the guide camera could not solve the pole field), or the fine one when the profile's
        /// <c>polar_align.camera</c> is <c>main</c>. The stage keeps its own meaning (the rough step's Done waits for the
        /// tolerance); the camera is the only difference from the guide-camera measurement.</summary>
        private async Task RunMainCameraStageAsync(
                string stage, IPolarAlignMainCameraSolver mainCamera, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, Func<Task> renew, int gen, CancellationToken ct) {
            await SetStageAsync(gen, stage, "seeding").ConfigureAwait(false);
            // The axis is MEASURED (three solves, two turns, the three-point fit), never read off the camera's pointing:
            // on 2026-11-07 the camera-as-axis reading sat 75′ from the fine stage's seed (#1727).
            // Seed frames may need the blind fallback (the mount can be degrees off); the live loop takes
            // one fast look per frame and shoots again, like the guide-camera loop (#1136).
            await RunSeededAdjustAsync(
                (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover: true, gen, timing, token),
                renew, site, settings, band, gen, ct,
                liveCapture: (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover: false, gen, timing, token)).ConfigureAwait(false);
        }

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

        // ── fine ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The fine measurement, from where the mount points (#2048: no Home, no GoTo); its turns go the way the
        /// user confirmed.</summary>
        private async Task RunFineStageAsync(
                IGuideCaptureLink guiderClient, SiteSettingsDto site, PolarAlignSettingsDto settings, RunBand band, string workDir,
                Func<Task> renew, int gen, CancellationToken ct) {
            await SetStageAsync(gen, PolarAlignStages.Fine, "seeding").ConfigureAwait(false);

            // ── §45.4 binning from the camera: ask the daemon for the guide camera's frame once per
            // run. A daemon without the RPC (or a refused call) is not a failure — auto falls back
            // to 1×1, the right answer for every guide camera anyway.
            _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            var chosenBinning = PolarAlignBinning.Resolve(settings.Binning, _runFrameSize);
            var binningSource = settings.Binning == PolarAlignBinning.Auto ? "auto" : "profile";
            LogBinningChosen(chosenBinning, binningSource, _runFrameSize?.Width ?? 0, _runFrameSize?.Height ?? 0);

            await RunSeededAdjustAsync(
                (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token),
                renew, site, settings, band, gen, ct).ConfigureAwait(false);
        }

        // ── the seed-and-adjust routine the coarse and fine stages share ─────────────────────

        /// <summary>Seed the axis fit with three frames around two RA turns, then run the live adjust loop until Stop or
        /// Done (#2082: it never moves on to another measurement). <paramref name="capture"/> is the camera: the main
        /// camera's capture-and-solve in the coarse measurement, the guider's in the fine one. Everything else — the RA-motor
        /// turns the way the user confirmed, the three-point fit, the §45.8 reverse-projection — is the same, so the two
        /// differ only in the camera and its cadence, never in what the readout means.</summary>
        /// <param name="liveCapture">The live loop's capture when it differs from the seeds' (the coarse
        /// stage: no blind fallback per frame); null = the same as <paramref name="capture"/>.</param>
        private async Task RunSeededAdjustAsync(
                LiveCapture capture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, CancellationToken ct, LiveCapture? liveCapture = null) {
            var north = site.LatitudeDeg > 0;
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);

            // The way the user confirmed (PlanSweep): the profile's east_direction at a new origin, else back toward it (#2081).
            var dirSign = band.AwaySign;
            // #2081 — a mount with no saved RA motor direction for its driver turns 0.5° first and saves which way it went.
            await TestRaMotorDirectionIfOwedAsync(band, gen, ct).ConfigureAwait(false);

            // ── seed: three frames with two RA turns of SeedRotationDeg between them (NINA's
            // Three Point Polar Alignment: 10° steps, 20° in all, from where the scope points). The
            // axis is the circle through the three pointings — no reliance on the mount's reported
            // turn, which the fit only cross-checks (a slipping clutch, a stalled motor).
            // Read from the driver like the turns' own readings, so the mount turns compare like with like.
            var (ra0, dec0) = await StartPointingOffTheAxisAsync(gen, ct).ConfigureAwait(false);
            var (a, aTime) = await SolveSeedFrameAsync(capture, "seed-a", (ra0, dec0), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-a", a.RaDegJnow, a.DecDegJnow);

            // ── RA turns: at a new origin the profile's east_direction, shown in the confirm step (#2081; the user picks a
            // start whose turns do not cross the meridian, NINA's advice); a later start's back toward the origin.
            var p1 = await TurnRaAsync(ra0, dirSign * settings.SeedRotationDeg, gen, ct).ConfigureAwait(false);
            var mountTurn1 = FoldTolerantSignedTurnDeg(p1.RaDeg - ra0);
            var (b, bTime) = await SolveSeedFrameAsync(capture, "seed-b", p1, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-b", b.RaDegJnow, b.DecDegJnow);
            CheckFieldRotation(a, b, mountTurn1, "seed-b"); // #2067 ARA-13 — before the next turn: a flipped mount turns no further

            var p2 = await TurnRaAsync(p1.RaDeg, dirSign * settings.SeedRotationDeg, gen, ct).ConfigureAwait(false);
            var mountTurn2 = FoldTolerantSignedTurnDeg(p2.RaDeg - p1.RaDeg);
            var (c, cTime) = await SolveSeedFrameAsync(capture, "seed-c", p2, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-c", c.RaDegJnow, c.DecDegJnow);
            CheckFieldRotation(b, c, mountTurn2, "seed-c");
            NoteDecSpread(gen, "seed", dec0, p1.DecDeg, p2.DecDeg); // #2081 — NINA's 2″ Dec-spread warning

            var (axis, _) = FitThreePointOrFail((a, aTime), (b, bTime), (c, cTime), mountTurn1, mountTurn2, north);
            var sigma = FitUncertaintyFor((a, aTime), (b, bTime), (c, cTime), north, site); // #2072
            await AdjustFromSeedAsync(axis, sigma, c, cTime, mountTurn1 + mountTurn2, liveCapture ?? capture, renewLeaseIfDue, site, gen, ct).ConfigureAwait(false);
        }

        /// <summary>The seed's axis error, then the live adjust loop until Stop or Done. <paramref name="c"/> is the seed's last solve, taken at <paramref name="cTime"/>;
        /// <paramref name="seedTurnDeg"/> is logged; <paramref name="sigma"/> is the fit's uncertainty (#2072), the live readout's.</summary>
        private async Task AdjustFromSeedAsync(
                (double RaDeg, double DecDeg, double ConeDeg) axis, FitUncertainty sigma, PolarAlignSolveOutcome c, DateTimeOffset cTime, double seedTurnDeg,
                LiveCapture liveCapture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            var (seedAltErr, seedAzErr) = PolarAlignGeometry.AxisError(
                axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, cTime);
            var seedPointing = PolarAlignGeometry.PointingAltAz(
                c.RaDegJnow, c.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, cTime);
            LogSeedFit(seedTurnDeg, axis.RaDeg, axis.DecDeg, seedAltErr, sigma.AltSigmaArcmin, seedAzErr, sigma.AzSigmaArcmin, sigma.TotalSigmaArcmin, sigma.Solves, sigma.Floor);
            SetFitSigma(gen, sigma);

            // ── live adjust: tracking OFF makes the camera direction stationary in alt/az, so
            // every subsequent solve's alt/az delta from the seed pointing is purely the user's
            // knob adjustment (§45.8) — and the loop needs no further slews. Confirmed off, and checked every frame (#2072).
            await TurnTrackingOffForLiveLoopAsync(gen, ct).ConfigureAwait(false);
            NoteReadoutReference(gen, seedPointing, site.LatitudeDeg); // #2072
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

        // ── check ────────────────────────────────────────────────────────────────────────────

        /// <returns>The verdict. The caller stops the mount and confirms it before it reports the verdict
        /// (<see cref="FinishCheckedAsync"/>).</returns>
        /// <param name="mainCamera">The main camera when the profile's <c>polar_align.camera</c> is <c>main</c> (#2082);
        /// null = the guide camera.</param>
        private async Task<PolarAlignCheckResultDto> RunCheckStageAsync(
                IGuideCaptureLink guiderClient, IPolarAlignMainCameraSolver? mainCamera, string workDir, SiteSettingsDto site,
                PolarAlignSettingsDto settings, RunBand band, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            // From where the mount points (#2048), the way the user confirmed (#2082: a Check is its own confirmed Start).
            await SetStageAsync(gen, PolarAlignStages.Check, "checking").ConfigureAwait(false);
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);
            // StartAsync clears the frame size per run: a guide-camera check reads it afresh.
            if (mainCamera is null) {
                _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            }

            var step = settings.CheckStepDeg; // always in range: PolarAlignSettingsClamp on every read
            var dirSign = band.AwaySign;
            await TestRaMotorDirectionIfOwedAsync(band, gen, ct).ConfigureAwait(false); // #2081
            // Read from the driver like the turns' own readings, so the mount turns compare like with like.
            var (ra0, dec0) = await StartPointingOffTheAxisAsync(gen, ct).ConfigureAwait(false);
            // A check frame on the main camera may need the blind fallback, like a seed frame.
            LiveCapture capture = mainCamera is not null
                ? (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover: true, gen, timing, token)
                : (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token);

            var (s1, t1) = await SolveSeedFrameAsync(capture, "check-1", (ra0, dec0), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-1", s1.RaDegJnow, s1.DecDegJnow);
            await NoteCheckSolveAsync(gen, 1).ConfigureAwait(false);

            var p1 = await TurnRaAsync(ra0, dirSign * step, gen, ct).ConfigureAwait(false);
            var mountTurn1 = FoldTolerantSignedTurnDeg(p1.RaDeg - ra0);
            var (s2, t2) = await SolveSeedFrameAsync(capture, "check-2", p1, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-2", s2.RaDegJnow, s2.DecDegJnow);
            CheckFieldRotation(s1, s2, mountTurn1, "check-2"); // #2067 ARA-13
            await NoteCheckSolveAsync(gen, 2).ConfigureAwait(false);

            var p2 = await TurnRaAsync(p1.RaDeg, dirSign * step, gen, ct).ConfigureAwait(false);
            var mountTurn2 = FoldTolerantSignedTurnDeg(p2.RaDeg - p1.RaDeg);
            // The point's mean time: the fit and the axis error are referred to it.
            var (s3, at) = await SolveSeedFrameAsync(capture, "check-3", p2, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-3", s3.RaDegJnow, s3.DecDegJnow);
            CheckFieldRotation(s2, s3, mountTurn2, "check-3");
            NoteDecSpread(gen, "check", dec0, p1.DecDeg, p2.DecDeg); // #2081 — NINA's 2″ Dec-spread warning
            await NoteCheckSolveAsync(gen, 3).ConfigureAwait(false);

            var (axis, mismatch) = FitThreePointOrFail((s1, t1), (s2, t2), (s3, at), mountTurn1, mountTurn2, north);
            return CheckResultFor(axis, FitUncertaintyFor((s1, t1), (s2, t2), (s3, at), north, site), mismatch, site, at);
        }

        /// <summary>The check's verdict from its fitted axis, measured at <paramref name="at"/>, beside the last run's
        /// readout.</summary>
        private PolarAlignCheckResultDto CheckResultFor(
                (double RaDeg, double DecDeg, double ConeDeg) axis, FitUncertainty sigma, double mismatch, SiteSettingsDto site, DateTimeOffset at) {
            var (altErr, azErr) = PolarAlignGeometry.AxisError(axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, at);
            // Rounded first: the verdict and the total the client colours by must sit in the same band
            // (1.004′ is "excellent" at 1.00′, not "very_good" beside a 1.0′ readout).
            var total = Math.Round(Math.Sqrt(altErr * altErr + azErr * azErr), 2);
            // #2082 — compared with the last run's readout (Joey may overrule: the plan's proposal, a fine stage no longer
            // leads into the check). Both components, so a readout of the same size on the other side of the pole still
            // shows as a disagreement.
            double? fine = null;
            double? fineDelta = null;
            bool noisy;
            lock (_gate) {
                noisy = _measurementNoisy;
                if (_previousRunAltArcmin is double fAlt && _previousRunAzArcmin is double fAz) {
                    fine = Math.Sqrt(fAlt * fAlt + fAz * fAz);
                    // The distance between the two measured axes, not the difference of their sizes: a
                    // readout the same size on the other side of the pole is a 2′ disagreement, not 0.
                    fineDelta = Math.Sqrt((altErr - fAlt) * (altErr - fAlt) + (azErr - fAz) * (azErr - fAz));
                }
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

        /// <summary>Sidereal rate, degrees of RA per second of time.</summary>
        internal const double SiderealDegPerSecond = 360.98564736629 / 86400.0;

        /// <summary>The mount's axis from three solves taken at different times (the seed and the check). Each
        /// solve is moved to the last one's time by the Earth's turn since it was taken: the RA axis is fixed to
        /// the ground, so the three pointings lie on one circle about it in the ground frame (NINA's TPPA works in
        /// alt/az for the same reason). The fit is refused when the solved turns disagree with the mount's own by
        /// more than <see cref="MaxCheckTurnMismatchDeg"/>, or the pointings sit too close to the axis.</summary>
        /// <param name="mountTurn1">The mount's signed sky-RA change between the first two solves (its motor turn:
        /// tracking holds the reading still). In the ground frame the tube also turned with the Earth meanwhile, so
        /// the turn to compare with the solved one is the hour-angle change, sidereal rate × elapsed − RA change.</param>
        internal static ((double RaDeg, double DecDeg, double ConeDeg) Axis, double MismatchDeg) FitThreePointOrFail(
                (PolarAlignSolveOutcome S, DateTimeOffset At) p1, (PolarAlignSolveOutcome S, DateTimeOffset At) p2,
                (PolarAlignSolveOutcome S, DateTimeOffset At) p3, double mountTurn1, double mountTurn2, bool north) {
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
            // How far the solved turns disagree with what the mount says it turned: a bad solve, or a
            // mount that turns by other than it reports (the open question behind #1591).
            var turn1 = PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra1, p1.S.DecDegJnow, ra2, p2.S.DecDegJnow);
            var turn2 = PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, ra2, p2.S.DecDegJnow, ra3, p3.S.DecDegJnow);
            var groundTurn1 = Math.Abs(((p2.At - p1.At).TotalSeconds * SiderealDegPerSecond) - mountTurn1);
            var groundTurn2 = Math.Abs(((p3.At - p2.At).TotalSeconds * SiderealDegPerSecond) - mountTurn2);
            var mismatch = Math.Max(Math.Abs(Math.Abs(turn1) - groundTurn1), Math.Abs(Math.Abs(turn2) - groundTurn2));
            // No answer from a fit the geometry does not support: three solves of one field (a mount that
            // reports turns it did not make — a slipping clutch, a stalled motor) still pass the coincidence
            // guards on noise and fit a random axis that can land near the pole by chance.
            if (mismatch > MaxCheckTurnMismatchDeg || axis.ConeDeg < MinCheckConeDeg) {
                throw new RoutineFailedException("axis_fit_failed",
                    $"the three solves do not describe a turn about one axis (turn mismatch {mismatch:F1}°, cone {axis.ConeDeg:F2}°) — " +
                    "the mount may not have turned as it reported (a slipping clutch, a stalled motor); check it and start again");
            }
            return (axis, mismatch);
        }

        /// <summary>The check's end: state <c>checked</c>, the result on the status, its event, the
        /// §45.13 row (outcome <c>checked</c>) and the lease released — the run is over.</summary>
        private async Task FinishCheckedAsync(int gen, PolarAlignCheckResultDto result) {
            // #2081 — the mount's Dec spread across the check's three points, when over 2″: the verdict says so.
            result = result with { DecSpreadArcsec = RunDecSpreadArcsec(gen) };
            lock (_gate) {
                if (_generation != gen) {
                    return;
                }
                _state = "checked";
                _active = false;
                _checkResult = result;
                _altErrorArcmin = result.AltitudeErrorArcmin;
                _azErrorArcmin = result.AzimuthErrorArcmin;
                _fitSigma = CheckSigma(result); // #2072 — the verdict's own σ, on the status and the session row
                RecordLastResultLocked(complete: false);
                RememberRunReadoutLocked(); // #2082 — the verdict is the readout a later check compares with
            }
            await PublishAsync(WsEventCatalog.PolarAlignChecked, new JsonObject {
                ["altitude_error_arcmin"] = result.AltitudeErrorArcmin,
                ["azimuth_error_arcmin"] = result.AzimuthErrorArcmin,
                ["total_error_arcmin"] = result.TotalErrorArcmin,
                ["cone_half_angle_deg"] = result.ConeHalfAngleDeg,
                ["turn_mismatch_deg"] = result.TurnMismatchDeg,
                ["fine_error_arcmin"] = result.FineErrorArcmin,
                ["fine_delta_arcmin"] = result.FineDeltaArcmin,
                ["verdict"] = result.Verdict,
                ["dec_spread_arcsec"] = result.DecSpreadArcsec,
                ["noisy"] = result.Noisy,
                ["altitude_sigma_arcmin"] = result.AltitudeSigmaArcmin,
                ["azimuth_sigma_arcmin"] = result.AzimuthSigmaArcmin,
                ["total_sigma_arcmin"] = result.TotalSigmaArcmin,
                ["measured_at"] = result.MeasuredAt.ToString("O", CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("checked", CancellationToken.None).ConfigureAwait(false);
            await ReleaseLeaseBestEffortAsync().ConfigureAwait(false);
        }

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
            return PublishStageAsync(gen, PolarAlignStages.Check, "checking", done);
        }

        // ── mount moves shared by the stages ─────────────────────────────────────────────────

        /// <summary>A measurement's deliberate switch-on before its turns — the last run's exit or live loop left tracking
        /// off on purpose (NINA turns tracking on before its measurement too). Distinct from the turn guard's "tracking
        /// dropped" (<see cref="KeepTrackingOnForSlewAsync"/>), which is a mid-run fault.</summary>
        private async Task EnsureTrackingOnAsync(int gen, CancellationToken ct) {
            if (_mount.GetInfo().TrackingEnabled) {
                return;
            }
            await TurnTrackingOnOrFailAsync(gen, ct).ConfigureAwait(false);
            LogTrackingEnabled();
        }

        /// <summary>#2166 — the <see cref="System.Exception.Data"/> key of an on-axis refusal's Dec reading (degrees).</summary>
        internal const string OnAxisDecKey = "polar_align.on_axis_dec_deg";

        /// <summary>The message of a start too close to the pole (the <c>polar-align-on-axis</c> refusal and the run's
        /// <c>on_axis</c> failure).</summary>
        internal const string OnAxisMessage =
            "The scope points within 3° of the pole: point it 2–5° from the pole, counterweights down, then start again";

        /// <summary>#2082 — the measurement's first reading, from the driver: where its turns start. A pointing within
        /// 3° of the pole (the mount was moved there since the confirmed start) fails the run before anything moves:
        /// a camera looking along the axis gives the three frames no circle to fit, and nothing tilts it off any more.
        /// #2066 — a reading the run cannot trust fails it too (<see cref="RunPointingJnowAsync"/>).</summary>
        private async Task<(double RaDeg, double DecDeg)> StartPointingOffTheAxisAsync(int gen, CancellationToken ct) {
            var pointing = await RunPointingJnowAsync(gen, ct).ConfigureAwait(false);
            if (Math.Abs(pointing.DecDeg) > OnAxisDecDeg) {
                throw new RoutineFailedException("on_axis", OnAxisMessage);
            }
            return pointing;
        }

        /// <summary>The mount's own signed RA turn (its sky-RA reading's change) from two of its pointing readings.
        /// A synced mount can report Dec past ±90° (the Dec axis over the pole), and a reading folded by 180° in RA
        /// between two solves would make a 30° turn read as 150° (#1536). The routine refuses such a reading now
        /// (#2066, <c>pose_ambiguous</c>); the fold stays here as the last guard. A turn is at most 30°, so the turn and
        /// its 180° complement are never confused.</summary>
        internal static double FoldTolerantSignedTurnDeg(double rawDeltaDeg) {
            var a = Wrap180(rawDeltaDeg);
            return Math.Abs(a) > 90.0 ? a - Math.CopySign(180.0, a) : a;
        }

        /// <summary>How far past its planned turns a start's sweep may reach before the session's limit refuses it: a turn
        /// overshoots its step by a fraction of a degree, and tracking drifts the axis between the turns.</summary>
        internal const double BandToleranceDeg = 2.0;

        /// <summary>Turn the RA axis by <paramref name="signedDeltaDeg"/> with the RA motor alone (NINA's Three
        /// Point Polar Alignment: MoveAxis on the primary axis until the mount's RA reading has changed by the
        /// step), settle, and return the mount's reported pointing afterwards. No GoTo: the firmware never picks a
        /// pier side, and the Dec axis cannot move. A mount that reports under 1° of turn fails as <c>slew_failed</c>.</summary>
        private async Task<(double RaDeg, double DecDeg)> TurnRaAsync(
                double fromRaDeg, double signedDeltaDeg, int gen, CancellationToken ct) {
            // The previous turn's live loop or a mount fault may have dropped tracking: re-check before each turn.
            await KeepTrackingOnForSlewAsync(gen, ct).ConfigureAwait(false);
            var intended = signedDeltaDeg >= 0 ? 1.0 : -1.0;
            var after = await MoveAxisUntilAsync(TelescopeAxes.Primary, intended, Math.Abs(signedDeltaDeg),
                (from, now) => intended * Wrap180(now.RaDeg - from.RaDeg),
                checkDirection: true, gen, ct,
                beforeMove: start => ThrowIfTurnPassesSweepLimit(start.RaDeg, signedDeltaDeg)).ConfigureAwait(false);
            var turned = Math.Abs(Wrap180(after.RaDeg - fromRaDeg));
            if (turned < 1.0) {
                throw new RoutineFailedException("slew_failed",
                    $"the mount reports an RA change of {turned:F2}° after the turn — it did not rotate");
            }
            return after;
        }

        /// <summary>The motor speeds the routine asks for (°/s): NINA's 3°/s for the bulk of a move, then 1°/s for its last
        /// <see cref="AxisSlowZoneDeg"/>, so the stop on the mount's reading lands within about a degree. #2083 — each is
        /// matched to the fastest rate the mount offers at or below it (<see cref="MatchTurnRates"/>), never rounded up.</summary>
        internal double AxisFastRateDegPerSec { get; set; } = 3.0;
        /// <summary>#2081 — the RA motor sign this run turns with: the saved one, or the one its direction test learned
        /// (−1: a positive rate lowers the mount's RA reading). Set per run; guarded by <c>_gate</c>.</summary>
        private double _raMotorSign = 1.0;
        internal double AxisSlowRateDegPerSec { get; set; } = 1.0;
        internal const double AxisSlowZoneDeg = 3.0;
        /// <summary>The motor runs on a lease: a daemon that stops renewing it (hung, crashed run) stops the axis.</summary>
        private const int AxisMoveLeaseMs = 3000;
        /// <summary>#2081 — a turn whose reading makes no 0.05° of progress for this long fails (<see cref="SlewStalled"/>):
        /// one poll more than AlpacaBridge iOptron's 1 s position cache, so a frozen reading costs about 4°, not 21°.</summary>
        internal TimeSpan AxisMoveStall { get; set; } = TimeSpan.FromSeconds(2);
        /// <summary>Added to a turn's time bound (twice its travel at the matched rates, <see cref="TurnTimeBound"/>): the
        /// stop lands on a reading that may trail the mount by up to <see cref="FreshReadMaxAge"/>.</summary>
        internal TimeSpan AxisMoveBoundSlack { get; set; } = TimeSpan.FromSeconds(2);
        /// <summary>How often a move reads the mount's position from the driver (fresh, not the 2 s refresh).</summary>
        internal TimeSpan AxisMovePoll { get; set; } = TimeSpan.FromMilliseconds(250);
        /// <summary>After the stop, how long two still readings must be apart before the turn counts as ended
        /// (<see cref="SettleAfterTurnAsync"/>): longer than the mount's readings can trail it, so two answers from one
        /// cached sample never pass for a mount standing still. #2166 rig check — the HAE29C on AlpacaBridge 6.0.0-beta.4
        /// trailed its motor by up to 2 s (AlpacaBridge iOptron's 1 s cache plus its status polling), so 2.5 s (was 1.5 s).</summary>
        internal TimeSpan AxisSettle { get; set; } = TimeSpan.FromSeconds(2.5);

        /// <summary>Run one axis's motor until <paramref name="progress"/> (start reading, current reading) reaches
        /// <paramref name="degrees"/> by the mount's own position, read from the driver four times a second, or until the
        /// motor has been told the distance (the commanded rates times how long they ran, #2166 rig check: a reading that
        /// trails the motor no longer adds its lag to the turn); fast until the reading is within the last
        /// <see cref="AxisSlowZoneDeg"/>, slow for the rest, at the rates the mount offers at or below the routine's (never
        /// above, #2083). Then stop and wait for the mount to stand still; after a stop by the clock the settled reading says
        /// where it ended, and a shortfall over <see cref="ClockStopTopUpDeg"/> gets a top-up move under the same guards and
        /// time bound. Then check the turn arrived, and return the fresh reading. With <paramref name="checkDirection"/>, a move that runs the wrong way (progress below
        /// −<see cref="WrongWayDeg"/>) is stopped and fails the run (<see cref="TurnWrongWay"/>, #2081): it is never reversed,
        /// because a reversal trusts the reading's sign, and a wrong reading (a bad sync, counterweight up) would turn the
        /// motor toward the limit and call that the right way. The RA motor's sign is the run's saved or tested one
        /// (<paramref name="motorSign"/> overrides it: the direction test sends a positive-assumed rate). #2067 (<see cref="SlewStalled"/> and the
        /// other move guards, PolarAlignService.MoveGuards.cs) — every exit stops the motor: Stop, failure, a stall (no
        /// progress for 2 s), a time bound of twice the planned travel time, a Dec reading that moves, a pier side that
        /// changes, a reading that jumps, and a mount that runs on past the step after its stop.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A refused motor command is this routine's slew_failed with the mount's message, never internal_error.")]
        private async Task<(double RaDeg, double DecDeg)> MoveAxisUntilAsync(
                TelescopeAxes axis, double sign, double degrees,
                Func<(double RaDeg, double DecDeg), (double RaDeg, double DecDeg), double> progress, bool checkDirection,
                int gen, CancellationToken ct, Action<(double RaDeg, double DecDeg)>? beforeMove = null, double? motorSign = null) {
            // #2089 — every reading of a move comes from the driver, never the cache: after a failed status poll the cache
            // is the (0, 0) placeholder, and progress read from it can look like a wrong-way turn, reverse the motor and run
            // it away from the band (and the session's origin) until the stall bound. #2066 — a reading the run cannot trust
            // (none, the placeholder, another mount session, older than 2 s, Dec past the pole) stops the move and fails it.
            // #2066 (ARA-2) — and nothing else moves the mount, nor guides, when the turn starts: two readings 1 s apart
            // (mount_busy otherwise). The second is where the turn starts.
            var (startRa, startDec, startSide) = await EnsureMountSteadyAsync(gen, ct).ConfigureAwait(false);
            var start = (startRa, startDec);
            // #2083 — the rates the mount really runs at, at or below the routine's: the time bound is planned on them.
            var (fastRate, slowRate) = MatchTurnRates(axis);
            // The caller's check of the move from the reading it starts at, before the motor runs (a turn's sweep limit).
            beforeMove?.Invoke(start);
            // #2081 — the RA motor's saved (or just tested) sign: the reading moves the way the run asks, or it fails.
            double learnedSign;
            if (motorSign is double fixedSign) {
                learnedSign = fixedSign;
            } else {
                lock (_gate) {
                    learnedSign = axis == TelescopeAxes.Primary ? _raMotorSign : 1.0;
                }
            }
            var direction = (sign >= 0 ? 1.0 : -1.0) * learnedSign;
            // Stop Mount (or Emergency Stop, a park, a Find Home) aborts the mount from outside this routine: every poll
            // checks for it, and the telescope service refuses a renewal that would restart the motor. #2066 — the run's
            // reading from its Start, not a fresh one per move: a stop between two moves (during a solve, a settle) is a
            // stop too, and the next move must not start under a count read after it.
            var abortEpoch = RunAbortEpoch(gen);
            var fast = degrees > AxisSlowZoneDeg;
            double Rate() => direction * (fast ? fastRate : slowRate);
            var bound = TurnTimeBound(degrees, fastRate, slowRate);
            var best = 0.0;
            var settled = false;
            var startRate = Rate();
            LogAxisMove(axis, degrees, startRate);
            (double RaDeg, double DecDeg, PierSide Side) after;
            BeginMotion(MountMotion.AxisMove, gen, ct);
            try {
                await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                // Timed from when the mount took the motor command: a slow round trip is not a stall. On the monotonic
                // clock: a wall clock that steps back (chrony, a UTCDate write) must not hold the stall and the bound off.
                var began = Clock.GetTimestamp();
                var bestAt = began;
                var last = (Ra: startRa, At: began);
                // #2166 rig check — the motor's commanded travel: the turn also stops once it reaches the step, however far
                // the reading trails the motor (the HAE29C's trailed it by up to 2 s).
                var travel = new CommandedTravel(Clock);
                travel.Reset(0.0, Rate(), began);
                var segmentBegan = began;
                // A top-up's settle is not motor time: the bound counts only the moves (#2188 review).
                var settledFor = TimeSpan.Zero;
                // Where the current segment started (the settled progress before a top-up): the follow check is per segment.
                var segmentBase = 0.0;
                while (true) {
                    var byClock = false;
                    var read = 0.0;
                    while (true) {
                        await Task.Delay(NextAxisPoll(travel, degrees), ct).ConfigureAwait(false);
                        if (!IsCurrent(gen)) {
                            throw new OperationCanceledException("the run was superseded while the mount moved");
                        }
                        ThrowIfStoppedFromOutside(gen);
                        // #2089 — noted as Polar Align's own move, so the next start does not read it as "moved by other means".
                        // #2067 — the poll reads the position and the pier side (no Slewing round trip): a turn that changes
                        // the pier side, or moves the Dec reading, stops at that reading.
                        var (nowRa, nowDec, nowSide) = await RunPoseJnowAsync(gen, ct, MountReadKind.Pose).ConfigureAwait(false);
                        var at = Clock.GetTimestamp();
                        var now = (nowRa, nowDec);
                        NoteMountLeft(now, nowSide is PierSide.pierUnknown ? startSide : nowSide);
                        // A reading that jumped is checked first: its Dec and pier side are no more to be trusted than its RA.
                        var change = Wrap180(nowRa - last.Ra);
                        if (change != 0) {
                            ThrowIfReadingJumped(change, fastRate, Clock.GetElapsedTime(last.At, at));
                            last = (nowRa, at);
                        }
                        ThrowIfTurnLeftItsAxis(startDec, startSide, nowDec, nowSide);
                        var p = progress(start, now);
                        read = p;
                        if (p >= degrees - 1e-9) {
                            break;
                        }
                        if (checkDirection && p <= -WrongWayDeg) {
                            // #2081 — never reversed: the motor stops (the finally below) and the run fails.
                            LogAxisMoveWrongWay(axis, -p, Rate(), learnedSign);
                            throw new RoutineFailedException(TurnWrongWay, TurnWrongWayMessage);
                        }
                        if (p > best + 0.05) {
                            best = p;
                            bestAt = at;
                        } else if (Clock.GetElapsedTime(bestAt, at) > AxisMoveStall) {
                            LogAxisMoveStalled(axis, Math.Max(best, 0), degrees, AxisMoveStall.TotalSeconds);
                            throw new RoutineFailedException(SlewStalled, string.Create(CultureInfo.InvariantCulture,
                                $"the mount's reading stopped changing after {Math.Max(best, 0):F1}° of the {degrees:F0}° turn (no progress for {AxisMoveStall.TotalSeconds:F0} s) — check it can turn freely and its connection, then start again"));
                        }
                        if (Clock.GetElapsedTime(began, at) - settledFor > bound) {
                            LogAxisMoveTimedOut(axis, Math.Max(best, 0), degrees, bound.TotalSeconds);
                            throw new RoutineFailedException(SlewTimeout, string.Create(CultureInfo.InvariantCulture,
                                $"the mount turned only {Math.Max(best, 0):F1}° of {degrees:F0}° in {bound.TotalSeconds:F0} s, twice the time the turn takes at its speed — check it, then start again"));
                        }
                        // #2166 rig check — the motor has been told the whole step: stop it now, whatever the reading says.
                        var told = travel.DegreesAt(at);
                        if (told >= degrees - 1e-9) {
                            byClock = true;
                            break;
                        }
                        if (fast && degrees - p <= AxisSlowZoneDeg) {
                            fast = false; // the last few degrees at the slow speed: the stop lands close to the target
                        }
                        if (!IsCurrent(gen)) {
                            // Checked again just before the renewal: a Stop during the read above must not get a motor restart.
                            throw new OperationCanceledException("the run was superseded while the mount moved");
                        }
                        await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false); // a new speed, or the lease renewed
                        // Timed once the mount took it: the old rate ran until then, so a slowdown never under-counts (#2188 review).
                        travel.Run(Rate(), Clock.GetTimestamp());
                    }
                    // Arrived on the reading (or the clock): stop the motor, then see the mount stand still. The motion stays
                    // marked until it does (ARA-3): a Stop meanwhile still sends AbortSlew, whatever an early "not slewing" says.
                    if (RunStopsFenced(gen)) {
                        // #2066 — the arriving read hung past a takeover's bound: the mount is someone else's, no stop from here.
                        throw new OperationCanceledException("the run was fenced as the turn arrived");
                    }
                    var stoppedAt = Clock.GetTimestamp();
                    var commanded = travel.DegreesAt(stoppedAt);
                    await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
                    settled = true;
                    after = await SettleAfterTurnAsync(start, startSide, degrees, progress, gen, ct).ConfigureAwait(false);
                    settledFor += Clock.GetElapsedTime(stoppedAt, Clock.GetTimestamp());
                    if (!byClock) {
                        break;
                    }
                    // Stopped by the clock: the settled reading says where the turn really ended.
                    LogClockStop(axis, commanded, read, degrees);
                    var settledDeg = progress(start, (after.RaDeg, after.DecDeg));
                    if (checkDirection && settledDeg <= -SettledWrongWayDeg(commanded - segmentBase)) {
                        // #2081 — never reversed, as on a poll: the motor is stopped and the run fails (#2188 review).
                        LogAxisMoveWrongWay(axis, -settledDeg, Rate(), learnedSign);
                        throw new RoutineFailedException(TurnWrongWay, TurnWrongWayMessage);
                    }
                    // The reading made no progress for the second half of the segment: it froze, not lagged.
                    var stoodStill = Clock.GetElapsedTime(bestAt, stoppedAt) * 2 >= Clock.GetElapsedTime(segmentBegan, stoppedAt);
                    ThrowIfReadingDidNotFollow(axis, settledDeg - segmentBase, commanded - segmentBase, degrees, stoodStill, "turn");
                    var rest = degrees - settledDeg;
                    if (rest <= ClockStopTopUpDeg) {
                        break;
                    }
                    // Short of the step once the reading caught up: a top-up for the rest, under the same guards and bound.
                    LogClockStopTopUp(axis, settledDeg, degrees, rest);
                    ThrowIfStoppedFromOutside(gen);
                    if (!IsCurrent(gen)) {
                        throw new OperationCanceledException("the run was superseded while the mount moved");
                    }
                    fast = rest > AxisSlowZoneDeg;
                    best = Math.Max(best, settledDeg);
                    settled = false;
                    await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                    segmentBegan = Clock.GetTimestamp();
                    bestAt = segmentBegan;
                    last = (after.RaDeg, segmentBegan);
                    travel.Reset(settledDeg, Rate(), segmentBegan);
                    segmentBase = settledDeg;
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) when (!IsCurrent(gen)) {
                // A Stop (its AbortSlew refuses the next renewal, or ends the move) is a stop, not a failure.
                throw new OperationCanceledException("the run was stopped while the mount moved");
            } catch (RoutineFailedException) {
                throw;
            } catch (Exception) when (AbortedSince(abortEpoch)) {
                // A refused motor command after a stop: from outside, the run-wide watch's failure. Only Polar Align's own
                // stop moved the count (a superseded run's late resend): the mount still refused the move.
                ThrowIfStoppedFromOutside(gen);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused the motor move right after a stop — check it is still, then start again");
            } catch (Exception ex) {
                // #1536 — the user gets the recovery; the driver's own text goes to the log.
                LogAxisMoveRefused(axis, ex);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused to turn its motor for the measurement — check it is unparked and free to move, then start again");
            } finally {
                // #2066 — not once a takeover or an end gave up on this run and fenced it: a late MoveAxis 0 (a read that
                // hung past the takeover's bound) would land on the park or the move that came after. #2067 — nor after the
                // arrival stop: the settle's confirmed stop owns any stop from there.
                if (!settled && !RunStopsFenced(gen)) {
                    await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
                }
            }
            // Ended normally and stood still. A move that threw keeps the motion set: the exit stops it again.
            EndMotion();
            var moved = progress(start, (after.RaDeg, after.DecDeg));
            ThrowIfTurnDidNotArrive(degrees, moved, startDec, after.DecDeg);
            LogAxisMoveDone(axis, moved);
            return (after.RaDeg, after.DecDeg);
        }

        /// <summary>The daemon's leased motor command (the same one the app's direction pad uses), through the seam
        /// (#2066). Never the mediator's <c>MoveAxis</c>: <c>TelescopeService</c>'s is a no-op stub.</summary>
        private Task RunAxisAsync(TelescopeAxes axis, double rate, long? abortEpoch = null) =>
            _motionControl.MoveAxisAsync((int)axis, rate, rate == 0 ? null : AxisMoveLeaseMs, rate == 0 ? null : abortEpoch, CancellationToken.None);

        private bool AbortedSince(long? abortEpoch) =>
            abortEpoch is long since && _motionControl.AbortEpoch != since;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "The stop is best-effort on every exit; its fault must not mask the move's outcome, and the lease stops the axis anyway.")]
        private async Task StopAxisBestEffortAsync(TelescopeAxes axis) {
            try {
                await RunAxisAsync(axis, 0).ConfigureAwait(false);
            } catch (Exception ex) {
                LogAxisStopFailed(axis, ex);
            }
        }

        [LoggerMessage(EventId = 4550, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} motor move of {Degrees:F1}° at {Rate:F2}°/s (no GoTo)")]
        private partial void LogAxisMove(TelescopeAxes axis, double degrees, double rate);

        [LoggerMessage(EventId = 4551, Level = LogLevel.Warning, Message = "§45 polar-align: the {Axis} motor turned {WrongDeg:F2}° the other way at {Rate:F2}°/s (saved motor sign {Sign:+0;-0}) — stopped, never reversed: turn_wrong_way")]
        private partial void LogAxisMoveWrongWay(TelescopeAxes axis, double wrongDeg, double rate, double sign);

        [LoggerMessage(EventId = 4552, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} motor move done, {Moved:F2}° by the mount's reading")]
        private partial void LogAxisMoveDone(TelescopeAxes axis, double moved);

        [LoggerMessage(EventId = 4554, Level = LogLevel.Warning, Message = "§45 polar-align: the mount refused the {Axis} motor move")]
        private partial void LogAxisMoveRefused(TelescopeAxes axis, Exception exception);

        [LoggerMessage(EventId = 4630, Level = LogLevel.Warning, Message = "§45 polar-align: the {Axis} turn stalled after {BestDeg:F2}° of {Degrees:F1}° (no progress for {Seconds:F1} s) — slew_stalled")]
        private partial void LogAxisMoveStalled(TelescopeAxes axis, double bestDeg, double degrees, double seconds);

        [LoggerMessage(EventId = 4631, Level = LogLevel.Warning, Message = "§45 polar-align: the {Axis} turn made {BestDeg:F2}° of {Degrees:F1}° within its {Seconds:F1} s bound — slew_timeout")]
        private partial void LogAxisMoveTimedOut(TelescopeAxes axis, double bestDeg, double degrees, double seconds);

        [LoggerMessage(EventId = 4553, Level = LogLevel.Warning, Message = "§45 polar-align: stopping the {Axis} motor failed (its lease stops it)")]
        private partial void LogAxisStopFailed(TelescopeAxes axis, Exception exception);

        // ── state + events ───────────────────────────────────────────────────────────────────

        private string? CurrentStage() {
            lock (_gate) {
                return _stage;
            }
        }

        private void SetState(int gen, string state) {
            lock (_gate) {
                if (_generation == gen) {
                    _state = state;
                }
            }
        }

        private Task SetStageAsync(int gen, string stage, string state) {
            lock (_gate) {
                if (_generation != gen) {
                    return Task.CompletedTask;
                }
                _stage = stage;
                _state = state;
                _checkSolvesDone = 0;
                // A measurement starts afresh: no earlier readout stands for it on the status, in a session row written
                // by a Stop before its first solve, or in the client's fold.
                _altErrorArcmin = null;
                _azErrorArcmin = null;
                _lastFrame = null;
                _decSpreadArcsec = null; // #2081 — the warning is the measurement's own
                _measurementNoisy = false;
            }
            LogStage(stage, state);
            return PublishStageAsync(gen, stage, state, solvesDone: 0);
        }

        private Task PublishStageAsync(int gen, string stage, string state, int solvesDone) =>
            !IsCurrent(gen) ? Task.CompletedTask :
            PublishAsync(WsEventCatalog.PolarAlignStage, new JsonObject {
                ["stage"] = stage,
                ["state"] = state,
                ["check_solves_done"] = solvesDone,
                ["check_solves_total"] = CheckSolves,
            });

        [LoggerMessage(EventId = 4534, Level = LogLevel.Information, Message = "§45 polar-align stage {Stage} ({State})")]
        private partial void LogStage(string stage, string state);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Information, Message = "§45 polar-align three-point check: axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ total {TotalArcmin:F2}′ ± {TotalSigmaArcmin:F2}′ (1σ); cone {ConeDeg:F2}°; turn mismatch {MismatchDeg:F3}°; last run said {FineArcmin}′; solves {Solves}; σ floor {Floor}")]
        private partial void LogCheckResult(double axisRaDeg, double axisDecDeg, double altArcmin, double azArcmin, double totalArcmin, double totalSigmaArcmin, double coneDeg, double mismatchDeg, double? fineArcmin, string solves, string floor);
    }
}