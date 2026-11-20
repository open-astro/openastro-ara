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
    /// The three stages of the polar-align routine. Every stage starts from where the mount points: the user pointed it
    /// near the pole with the counterweights down and confirmed the start (#2048). No stage sends the mount Home or slews
    /// it, like NINA's Three Point Polar Alignment started from the current position. <b>Coarse</b>: when the camera
    /// looks along the axis, the Dec motor tilts the tube 5° off it, and the §45 seed-and-adjust routine runs on the
    /// <i>main camera's</i> frames — three solves with two RA-motor turns, the three-point axis fit, then the live loop
    /// that reads knob moves — rough only in its cadence (the main camera's longer exposure and solve), good for the big
    /// moves on which side of the pole. <b>Fine</b>: the same routine on the guide camera, which solves a frame a second.
    /// <b>Check</b>: three guide-camera solves with two RA-motor turns of <c>CheckStepDeg</c>, an exact three-point fit,
    /// and a verdict beside the fine stage's last readout. Nothing is adjusted in the check; it says how good the
    /// alignment really is (#1591). The stages' RA turns keep to the band the user confirmed
    /// (<see cref="TurnDirectionForStageAsync"/>).
    /// </summary>
    public sealed partial class PolarAlignService {

        // The fine seed and the check need the camera OFF the axis: a pointing within this many
        // degrees of the pole is tilted out to SeedDecDeg first with the Dec motor.
        internal const double OnAxisDecDeg = 87.0;
        internal const double SeedDecDeg = 85.0;
        private const int CheckSolves = 3;
        // The check refuses a verdict when the solved turns disagree with the mount's by more than this, or
        // when the three pointings sit this close to one line through the axis (a Dec-85° circle is ~5°).
        internal const double MaxCheckTurnMismatchDeg = 5.0;
        internal const double MinCheckConeDeg = 1.0;

        // ── coarse ───────────────────────────────────────────────────────────────────────────

        private async Task RunCoarseStageAsync(
                IPolarAlignMainCameraSolver mainCamera, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, Func<Task> renew, int gen, CancellationToken ct) {
            await SetStageAsync(gen, PolarAlignStages.Coarse, "seeding").ConfigureAwait(false);
            // The axis is MEASURED (three solves, two turns, the three-point fit), never read off the camera's pointing:
            // on 2026-11-07 the camera-as-axis reading sat 75′ from the fine stage's seed (#1727).
            // Seed frames may need the blind fallback (the mount can be degrees off); the live loop takes
            // one fast look per frame and shoots again, like the guide-camera loop (#1136).
            await RunSeededAdjustAsync(
                (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover: true, gen, timing, token),
                renew, site, settings, band, gen, ct,
                liveCapture: (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, blindFailover: false, gen, timing, token)).ConfigureAwait(false);
            LogStageAdvanced(PolarAlignStages.Coarse, PolarAlignStages.Fine);
        }

        /// <summary>One coarse frame: the main camera's snapshot, solved with the main optics. A busy
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

        /// <summary>The fine stage, from where the mount points: after the coarse stage, where its turns and its
        /// tracking-off live loop left it (#2048: no Home, no GoTo back); its turns keep to the run's band.</summary>
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

            // Next: remember the fine readout the check will be compared with — both components, so a
            // readout of the same size on the other side of the pole still shows as a disagreement.
            lock (_gate) {
                if (_generation == gen && _altErrorArcmin is double alt && _azErrorArcmin is double az) {
                    _fineFinalAltArcmin = alt;
                    _fineFinalAzArcmin = az;
                }
            }
            LogStageAdvanced(PolarAlignStages.Fine, PolarAlignStages.Check);
        }

        // ── the seed-and-adjust routine the coarse and fine stages share ─────────────────────

        /// <summary>Leave the axis, seed the axis fit with three frames around two RA turns, then run the
        /// live adjust loop until the user's Next. <paramref name="capture"/> is the camera: the main
        /// camera's capture-and-solve in the coarse stage, the guider's in the fine stage. Everything
        /// else — the 5° Dec-motor tilt, the RA-motor turns away from the meridian, the three-point fit, the §45.8
        /// reverse-projection — is the same, so the two stages differ only in the camera and its
        /// cadence, never in what the readout means.</summary>
        /// <param name="liveCapture">The live loop's capture when it differs from the seeds' (the coarse
        /// stage: no blind fallback per frame); null = the same as <paramref name="capture"/>.</param>
        private async Task RunSeededAdjustAsync(
                LiveCapture capture, Func<Task> renewLeaseIfDue, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, CancellationToken ct, LiveCapture? liveCapture = null) {
            var north = site.LatitudeDeg > 0;
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);

            // A camera looking along the axis would see the seed frames coincide.
            await LeaveTheAxisAsync(gen, ct).ConfigureAwait(false);

            // Which way this stage's two turns go, inside the band the user confirmed (#2048).
            var dirSign = await TurnDirectionForStageAsync(band, 2.0 * settings.SeedRotationDeg, gen, ct).ConfigureAwait(false);

            // ── seed: three frames with two RA turns of SeedRotationDeg between them (NINA's
            // Three Point Polar Alignment: 10° steps, 20° in all, from where the scope points). The
            // axis is the circle through the three pointings — no reliance on the mount's reported
            // turn, which the fit only cross-checks (a slipping clutch, a stalled motor).
            // Read from the driver like the turns' own readings, so the mount turns compare like with like.
            var (ra0, dec0) = await FreshMountPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            var a = await SolveSeedFrameAsync(capture, "seed-a", (ra0, dec0), settings, gen, ct).ConfigureAwait(false);
            var aTime = DateTimeOffset.UtcNow;
            LogSeedSolve("seed-a", a.RaDegJnow, a.DecDegJnow);

            // ── RA turns: the first stage's away from the meridian so the arc cannot cross it (a crossing risks a
            // meridian flip, which garbles the fit — guider design §5 step 2); a later stage's back over the same arc.
            var p1 = await TurnRaAsync(ra0, dirSign * settings.SeedRotationDeg, gen, ct).ConfigureAwait(false);
            var mountTurn1 = FoldTolerantSignedTurnDeg(p1.RaDeg - ra0);
            var b = await SolveSeedFrameAsync(capture, "seed-b", p1, settings, gen, ct).ConfigureAwait(false);
            var bTime = DateTimeOffset.UtcNow;
            LogSeedSolve("seed-b", b.RaDegJnow, b.DecDegJnow);

            var p2 = await TurnRaAsync(p1.RaDeg, dirSign * settings.SeedRotationDeg, gen, ct).ConfigureAwait(false);
            var mountTurn2 = FoldTolerantSignedTurnDeg(p2.RaDeg - p1.RaDeg);
            var c = await SolveSeedFrameAsync(capture, "seed-c", p2, settings, gen, ct).ConfigureAwait(false);
            var cTime = DateTimeOffset.UtcNow;
            LogSeedSolve("seed-c", c.RaDegJnow, c.DecDegJnow);

            var (axis, _) = FitThreePointOrFail((a, aTime), (b, bTime), (c, cTime), mountTurn1, mountTurn2, north);
            var (seedAltErr, seedAzErr) = PolarAlignGeometry.AxisError(
                axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, cTime);
            var seedPointing = PolarAlignGeometry.PointingAltAz(
                c.RaDegJnow, c.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, cTime);
            LogSeedFit(mountTurn1 + mountTurn2, axis.RaDeg, axis.DecDeg, seedAltErr, seedAzErr);

            // ── live adjust: tracking OFF makes the camera direction stationary in alt/az, so
            // every subsequent solve's alt/az delta from the seed pointing is purely the user's
            // knob adjustment (§45.8) — and the loop needs no further slews. Confirmed off, and checked every frame (#2072).
            await TurnTrackingOffForLiveLoopAsync(gen, ct).ConfigureAwait(false);
            NoteReadoutReference(gen, seedPointing, site.LatitudeDeg); // #2072
            SetErrors(gen, seedAltErr, seedAzErr, "adjusting", frameId: null); // keeps the seed-c attempt's id
            await PublishProgressAsync(gen, 0, seedAltErr, seedAzErr, solved: true).ConfigureAwait(false);
            ResetAlignerBaseline();
            await SolveLoopAsync(
                liveCapture ?? capture,
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
        private async Task<PolarAlignCheckResultDto> RunCheckStageAsync(
                IGuideCaptureLink guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings,
                RunBand band, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            // From where the mount points (#2048): after the fine stage, where its turns and live loop left it.
            await SetStageAsync(gen, PolarAlignStages.Check, "checking").ConfigureAwait(false);
            await EnsureTrackingOnAsync(gen, ct).ConfigureAwait(false);
            if (_runFrameSize is null) {
                // A run that began at the check never read the guide frame size (StartAsync clears it per run).
                _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            }
            await LeaveTheAxisAsync(gen, ct).ConfigureAwait(false);

            var step = settings.CheckStepDeg; // always in range: PolarAlignSettingsClamp on every read
            var dirSign = await TurnDirectionForStageAsync(band, 2.0 * step, gen, ct).ConfigureAwait(false);
            // Read from the driver like the turns' own readings, so the mount turns compare like with like.
            var (ra0, dec0) = await FreshMountPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            LiveCapture capture = (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token);

            var s1 = await SolveSeedFrameAsync(capture, "check-1", (ra0, dec0), settings, gen, ct).ConfigureAwait(false);
            var t1 = DateTimeOffset.UtcNow;
            LogSeedSolve("check-1", s1.RaDegJnow, s1.DecDegJnow);
            await NoteCheckSolveAsync(gen, 1).ConfigureAwait(false);

            var p1 = await TurnRaAsync(ra0, dirSign * step, gen, ct).ConfigureAwait(false);
            var mountTurn1 = FoldTolerantSignedTurnDeg(p1.RaDeg - ra0);
            var s2 = await SolveSeedFrameAsync(capture, "check-2", p1, settings, gen, ct).ConfigureAwait(false);
            var t2 = DateTimeOffset.UtcNow;
            LogSeedSolve("check-2", s2.RaDegJnow, s2.DecDegJnow);
            await NoteCheckSolveAsync(gen, 2).ConfigureAwait(false);

            var p2 = await TurnRaAsync(p1.RaDeg, dirSign * step, gen, ct).ConfigureAwait(false);
            var mountTurn2 = FoldTolerantSignedTurnDeg(p2.RaDeg - p1.RaDeg);
            var s3 = await SolveSeedFrameAsync(capture, "check-3", p2, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-3", s3.RaDegJnow, s3.DecDegJnow);
            await NoteCheckSolveAsync(gen, 3).ConfigureAwait(false);
            var at = DateTimeOffset.UtcNow;

            var (axis, mismatch) = FitThreePointOrFail((s1, t1), (s2, t2), (s3, at), mountTurn1, mountTurn2, north);
            var (altErr, azErr) = PolarAlignGeometry.AxisError(axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, at);
            // Rounded first: the verdict and the total the client colours by must sit in the same band
            // (1.004′ is "excellent" at 1.00′, not "very_good" beside a 1.0′ readout).
            var total = Math.Round(Math.Sqrt(altErr * altErr + azErr * azErr), 2);
            double? fine = null;
            double? fineDelta = null;
            lock (_gate) {
                if (_fineFinalAltArcmin is double fAlt && _fineFinalAzArcmin is double fAz) {
                    fine = Math.Sqrt(fAlt * fAlt + fAz * fAz);
                    // The distance between the two measured axes, not the difference of their sizes: a
                    // fine readout the same size on the other side of the pole is a 2′ disagreement, not 0.
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
                Verdict: VerdictFor(total),
                MeasuredAt: at);
            LogCheckResult(axis.RaDeg, axis.DecDeg, altErr, azErr, total, axis.ConeDeg, mismatch, fine);
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
            lock (_gate) {
                if (_generation != gen) {
                    return;
                }
                _state = "checked";
                _active = false;
                _checkResult = result;
                _altErrorArcmin = result.AltitudeErrorArcmin;
                _azErrorArcmin = result.AzimuthErrorArcmin;
                RecordLastResultLocked(complete: false);
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
                ["measured_at"] = result.MeasuredAt.ToString("O", CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
            await WriteSessionRowBestEffortAsync("checked", CancellationToken.None).ConfigureAwait(false);
            await ReleaseLeaseBestEffortAsync().ConfigureAwait(false);
        }

        /// <summary>The verdict bands the client's rating uses (polar_error_rating.dart).</summary>
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

        /// <summary>A stage's deliberate switch-on before its turns — the previous stage's live loop left tracking off
        /// on purpose (NINA turns tracking on before its measurement too). Distinct from the turn guard's "tracking
        /// dropped" (<see cref="KeepTrackingOnForSlewAsync"/>), which is a mid-run fault.</summary>
        private async Task EnsureTrackingOnAsync(int gen, CancellationToken ct) {
            if (_mount.GetInfo().TrackingEnabled) {
                return;
            }
            await TurnTrackingOnOrFailAsync(gen, ct).ConfigureAwait(false);
            LogTrackingEnabled();
        }

        /// <summary>A camera looking along the RA axis (Dec ≈ 90°: the user pointed the tube right at the pole) gives
        /// the seed and the check no circle to fit: tilt it 5° off the axis first with the Dec motor alone (NINA's way — a
        /// GoTo leaves the firmware to pick a pier side, and on 2026-11-18 it picked the flipped one and swung the tube
        /// toward the pier). The move stops on the mount's own reading, 5° from the pole. A pointing already off the
        /// axis is left where it is.</summary>
        private async Task LeaveTheAxisAsync(int gen, CancellationToken ct) {
            var (_, decDeg) = await FreshMountPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            if (Math.Abs(decDeg) <= OnAxisDecDeg) {
                return;
            }
            var aimDecDeg = Math.CopySign(SeedDecDeg, decDeg == 0 ? 1.0 : decDeg);
            LogLeavingAxis(decDeg, aimDecDeg);
            // Progress is the gain in distance from the pole, so a tilt that heads over the pole from Dec 88°, say,
            // is reversed rather than ending 3° off the axis on the far side (from within 0.5° of the pole it can
            // still cross before the reversal threshold, which ends 5° off on the far side: harmless). The tube
            // goes to 5° from the pole.
            var startPoleDistDeg = 90.0 - Math.Abs(decDeg);
            await MoveAxisUntilAsync(TelescopeAxes.Secondary, 1.0, Math.Max(1.0, 90.0 - SeedDecDeg - startPoleDistDeg),
                (from, now) => Math.Abs(from.DecDeg) - Math.Abs(now.DecDeg),
                checkDirection: true, gen, ct).ConfigureAwait(false);
        }

        /// <summary>The mount's own signed RA turn (its sky-RA reading's change) from two of its pointing readings.
        /// A synced mount can report Dec past ±90° (the Dec axis over the pole), and <see cref="CurrentMountPointingJnow"/>
        /// folds such a reading by 180° in RA; one folded reading between two solves would make a 30° turn read as
        /// 150°. A turn is at most 30°, so the turn and its 180° complement are never confused.</summary>
        internal static double FoldTolerantSignedTurnDeg(double rawDeltaDeg) {
            var a = Wrap180(rawDeltaDeg);
            return Math.Abs(a) > 90.0 ? a - Math.CopySign(180.0, a) : a;
        }

        /// <summary>With HA = LST − RA, decreasing RA increases HA (further west): turn west when
        /// already west of the meridian, east when east — the arc then cannot cross it. Read once per run, at its
        /// start (the direction the user confirms); the later stages keep to the band it sets
        /// (<see cref="TurnDirectionForStageAsync"/>).</summary>
        private double AwayFromMeridianSign(double mountRaDeg) {
            var lstDeg = _mount.GetInfo().SiderealTime * 15.0;
            var haDeg = Wrap180(lstDeg - mountRaDeg);
            return haDeg >= 0 ? -1.0 : 1.0;
        }

        /// <summary>How far past the run's start pose (toward the meridian), or past its far edge, a stage may end:
        /// a turn overshoots its step by a fraction of a degree, and tracking drifts the axis west between the turns.</summary>
        internal const double BandToleranceDeg = 2.0;

        /// <summary>#2048 — how far the RA axis has turned from the run's start pose, in degrees, positive away from the
        /// meridian (the direction the user confirmed). It is the mechanical turn — the hour angle's change — so it
        /// counts the tracking between the turns and ignores the RA reading's drift while tracking is off: the RA
        /// reading's change since the start, less the Earth's turn since then.</summary>
        private static double RaOffsetFromStartDeg(RunBand band, double raNowDeg) {
            var earthDeg = Stopwatch.GetElapsedTime(band.StartedAt).TotalSeconds * SiderealDegPerSecond;
            return band.AwaySign * FoldTolerantSignedTurnDeg(raNowDeg - band.StartRaDeg - earthDeg);
        }

        /// <summary>#2048 — a stage's turns, kept inside the band the user confirmed at the start: from the start pose
        /// (offset 0) to <paramref name="maxSweepDeg"/> away from the meridian. With no Home and no GoTo back, a later
        /// stage starts where the last one ended, and turning away again would add up toward a counterweight limit
        /// (three stages of two 10° turns: 60°). So a stage turns BACK over the arc when it then ends no further than
        /// <see cref="BandToleranceDeg"/> past the start, away when it then ends inside the band, and otherwise first
        /// turns back to the start pose with the RA motor and then away. The first stage (offset 0) always turns away:
        /// the direction the user confirmed. Pure, for the tests.</summary>
        /// <param name="awaySign">The run's away-from-the-meridian sign in RA-reading terms (+1 east, −1 west).</param>
        /// <param name="offsetDeg">Where the axis is, from <see cref="RaOffsetFromStartDeg"/>.</param>
        /// <param name="travelDeg">This stage's two turns together.</param>
        /// <returns>The sign of this stage's turns, and the signed RA turn back to the start pose first (0 for none).</returns>
        internal static (double DirSign, double ReturnFirstDeg) PlanStageTurns(
                double awaySign, double offsetDeg, double travelDeg, double maxSweepDeg) {
            if (offsetDeg - travelDeg >= -BandToleranceDeg) {
                return (-awaySign, 0.0);
            }
            if (offsetDeg + travelDeg <= maxSweepDeg + BandToleranceDeg) {
                return (awaySign, 0.0);
            }
            // Neither fits (a long stage after short turns): offset > maxSweep + tolerance − travel ≥ tolerance, so the
            // turn back to the start is always a real turn, inside the band.
            return (awaySign, -awaySign * offsetDeg);
        }

        /// <summary>The sign of this stage's two turns (<see cref="PlanStageTurns"/>), after the turn back to the start
        /// pose when the band needs one. Logged either way.</summary>
        private async Task<double> TurnDirectionForStageAsync(RunBand band, double travelDeg, int gen, CancellationToken ct) {
            // From the driver, never the cache (DriverPointingJnowAsync): a placeholder reading would put the axis anywhere
            // in the band and turn it a way the user never confirmed.
            var (raNow, _) = await DriverPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", "the mount did not report where it points, so the turns were not started — check its connection, then start again");
            var offset = RaOffsetFromStartDeg(band, raNow);
            var (dirSign, returnFirst) = PlanStageTurns(band.AwaySign, offset, travelDeg, band.MaxSweepDeg);
            var stage = CurrentStage() ?? "?";
            var way = dirSign == band.AwaySign ? "the confirmed way" : "back toward the start";
            LogStageTurns(stage, offset, way, band.MaxSweepDeg, returnFirst);
            if (returnFirst != 0.0) {
                await TurnRaAsync(raNow, returnFirst, gen, ct).ConfigureAwait(false);
            }
            return dirSign;
        }

        [LoggerMessage(EventId = 4570, Level = LogLevel.Information, Message = "§45 polar-align {Stage} stage: the RA axis is {OffsetDeg:F1}° from the run's start; its turns go {Way} (band {MaxSweepDeg:F0}°, turn back first {ReturnFirstDeg:F1}°)")]
        private partial void LogStageTurns(string stage, double offsetDeg, string way, double maxSweepDeg, double returnFirstDeg);

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
                checkDirection: true, gen, ct).ConfigureAwait(false);
            var turned = Math.Abs(Wrap180(after.RaDeg - fromRaDeg));
            if (turned < 1.0) {
                throw new RoutineFailedException("slew_failed",
                    $"the mount reports an RA change of {turned:F2}° after the turn — it did not rotate");
            }
            return after;
        }

        /// <summary>The motor speeds of the routine's moves (°/s): NINA's 3°/s for the bulk of a move, then 1°/s
        /// for its last <see cref="AxisSlowZoneDeg"/>, so the stop on the mount's reading lands within about a degree.</summary>
        internal double AxisFastRateDegPerSec { get; set; } = 3.0;
        /// <summary>−1 once a turn of this run found the driver's RA motor sign inverted; reset per run.</summary>
        private double _raMotorSign = 1.0;
        internal double AxisSlowRateDegPerSec { get; set; } = 1.0;
        internal const double AxisSlowZoneDeg = 3.0;
        /// <summary>The motor runs on a lease: a daemon that stops renewing it (hung, crashed run) stops the axis.</summary>
        private const int AxisMoveLeaseMs = 3000;
        internal TimeSpan AxisMoveStall { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Added to a move's time bound (three times its travel at the slow speed): start-up and settle.</summary>
        internal TimeSpan AxisMoveBoundSlack { get; set; } = TimeSpan.FromSeconds(15);
        /// <summary>How often a move reads the mount's position from the driver (fresh, not the 2 s refresh).</summary>
        internal TimeSpan AxisMovePoll { get; set; } = TimeSpan.FromMilliseconds(250);
        /// <summary>After the stop, how long the mount is left to settle before its final reading.</summary>
        internal TimeSpan AxisSettle { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Run one axis's motor until <paramref name="progress"/> (start reading, current reading) reaches
        /// <paramref name="degrees"/> by the mount's own position, read from the driver four times a second; fast
        /// until the last <see cref="AxisSlowZoneDeg"/>, slow for the rest. Then stop, settle, and return the fresh
        /// reading. With <paramref name="checkDirection"/>, a move that runs the wrong way (progress below −0.5°)
        /// is stopped and reversed once — a driver's sign for "positive" is not standard; a move that then runs
        /// back past its start again does not reverse a second time, it stalls and fails. Every exit stops the
        /// motor: Stop, failure, stall (no progress for 10 s) and a time bound of three times the slow travel time.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A refused motor command is this routine's slew_failed with the mount's message, never internal_error.")]
        private async Task<(double RaDeg, double DecDeg)> MoveAxisUntilAsync(
                TelescopeAxes axis, double sign, double degrees,
                Func<(double RaDeg, double DecDeg), (double RaDeg, double DecDeg), double> progress, bool checkDirection,
                int gen, CancellationToken ct) {
            // #2089 — every reading of a move comes from the driver, never the cache: after a failed status poll the cache
            // is the (0, 0) placeholder, and progress read from it can look like a wrong-way turn, reverse the motor and run
            // it away from the band (and the session's origin) until the stall bound. A failed read stops the move.
            var start = await DriverPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", MoveReadFailedMessage);
            // A reversal learned on an earlier turn of this run applies from the start (the driver's sign is fixed).
            double learnedSign;
            lock (_gate) {
                learnedSign = axis == TelescopeAxes.Primary ? _raMotorSign : 1.0;
            }
            var direction = (sign >= 0 ? 1.0 : -1.0) * learnedSign;
            // Stop Mount (or Emergency Stop) during the move aborts the mount from outside this routine: every
            // renewal checks for it, and the telescope service refuses a renewal that would restart the motor.
            var abortEpoch = (_mount as ITelescopeService)?.AbortEpoch;
            var fast = degrees > AxisSlowZoneDeg;
            double Rate() => direction * (fast ? AxisFastRateDegPerSec : AxisSlowRateDegPerSec);
            var began = Stopwatch.GetTimestamp();
            var bound = TimeSpan.FromSeconds(degrees / AxisSlowRateDegPerSec * 3.0) + AxisMoveBoundSlack;
            var best = 0.0;
            var bestAt = Stopwatch.GetTimestamp();
            var reversed = false;
            var startRate = Rate();
            LogAxisMove(axis, degrees, startRate);
            BeginMotion(MountMotion.AxisMove, gen, ct);
            try {
                await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                while (true) {
                    await Task.Delay(AxisMovePoll, ct).ConfigureAwait(false);
                    if (!IsCurrent(gen)) {
                        throw new OperationCanceledException("the run was superseded while the mount moved");
                    }
                    if (AbortedSince(abortEpoch)) {
                        throw new RoutineFailedException("slew_failed", ExternalStopMessage);
                    }
                    // #2089 — noted as Polar Align's own move, so the next start does not read it as "moved by other means".
                    var now = await DriverPointingJnowAsync(ct).ConfigureAwait(false)
                        ?? throw new RoutineFailedException("mount_fault", MoveReadFailedMessage);
                    NoteMountLeft(now);
                    var p = progress(start, now);
                    if (p >= degrees - 1e-9) {
                        break;
                    }
                    if (checkDirection && !reversed && p <= -0.5) {
                        reversed = true;
                        direction = -direction;
                        if (axis == TelescopeAxes.Primary) {
                            lock (_gate) {
                                if (_generation == gen) { // never a superseded run's lesson, checked and written as one
                                    _raMotorSign = -_raMotorSign;
                                }
                            }
                        }
                        var reversedRate = Rate();
                        LogAxisMoveReversed(axis, reversedRate);
                        await RunAxisAsync(axis, 0).ConfigureAwait(false);
                        if (!IsCurrent(gen)) {
                            // A Stop during the stop above must not get the motor restarted the other way.
                            throw new OperationCanceledException("the run was stopped while the mount reversed");
                        }
                        await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false);
                        best = p;
                        bestAt = Stopwatch.GetTimestamp();
                        continue;
                    }
                    if (p > best + 0.05) {
                        best = p;
                        bestAt = Stopwatch.GetTimestamp();
                    } else if (Stopwatch.GetElapsedTime(bestAt) > AxisMoveStall) {
                        throw new RoutineFailedException("slew_failed",
                            $"the mount stopped moving after {Math.Max(best, 0):F1}° of the {degrees:F0}° move — check it can turn freely, then start again");
                    }
                    if (Stopwatch.GetElapsedTime(began) > bound) {
                        throw new RoutineFailedException("slew_failed",
                            $"the mount moved only {Math.Max(best, 0):F1}° of {degrees:F0}° in {bound.TotalSeconds:F0} s — check it, then start again");
                    }
                    if (fast && degrees - p <= AxisSlowZoneDeg) {
                        fast = false; // the last few degrees at the slow speed: the stop lands close to the target
                    }
                    if (!IsCurrent(gen)) {
                        // Checked again just before the renewal: a Stop during the read above must not get a motor restart.
                        throw new OperationCanceledException("the run was superseded while the mount moved");
                    }
                    await RunAxisAsync(axis, Rate(), abortEpoch).ConfigureAwait(false); // a new speed, or the lease renewed
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) when (!IsCurrent(gen)) {
                // A Stop (its AbortSlew refuses the next renewal, or ends the move) is a stop, not a failure.
                throw new OperationCanceledException("the run was stopped while the mount moved");
            } catch (RoutineFailedException) {
                throw;
            } catch (Exception) when (AbortedSince(abortEpoch)) {
                throw new RoutineFailedException("slew_failed", ExternalStopMessage);
            } catch (Exception ex) {
                // #1536 — the user gets the recovery; the driver's own text goes to the log.
                LogAxisMoveRefused(axis, ex);
                throw new RoutineFailedException("slew_failed",
                    "the mount refused to turn its motor for the measurement — check it is unparked and free to move, then start again");
            } finally {
                await StopAxisBestEffortAsync(axis).ConfigureAwait(false);
            }
            // Ended normally and stopped on the reading. A move that threw keeps the motion set: the exit stops it again.
            EndMotion();
            await Task.Delay(AxisSettle, ct).ConfigureAwait(false);
            var after = await DriverPointingJnowAsync(ct).ConfigureAwait(false)
                ?? throw new RoutineFailedException("mount_fault", MoveReadFailedMessage);
            NoteMountLeft(after);
            var moved = progress(start, after);
            LogAxisMoveDone(axis, moved);
            return after;
        }

        /// <summary>The daemon's leased motor command (the same one the app's direction pad uses) when the
        /// mount is the daemon's telescope service; the mediator's own call otherwise (tests). Production always
        /// takes the first branch: <c>TelescopeService</c>'s mediator <c>MoveAxis</c> is a no-op stub, so never
        /// route a real move through the fallback.</summary>
        private Task RunAxisAsync(TelescopeAxes axis, double rate, long? abortEpoch = null) =>
            _mount is ITelescopeService service
                ? service.MoveAxisAsync((int)axis, rate, rate == 0 ? null : AxisMoveLeaseMs, rate == 0 ? null : abortEpoch, CancellationToken.None)
                : Task.Run(() => _mount.MoveAxis(axis, rate));

        private const string MoveReadFailedMessage =
            "the mount did not report where it points during the turn, so the motor was stopped — check its connection, then start again";

        private const string ExternalStopMessage =
            "the mount was stopped from outside polar alignment (Stop Mount) — start again when ready";

        private bool AbortedSince(long? abortEpoch) =>
            abortEpoch is long since && _mount is ITelescopeService service && service.AbortEpoch != since;

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

        [LoggerMessage(EventId = 4551, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} motor ran the wrong way — reversed to {Rate:F2}°/s")]
        private partial void LogAxisMoveReversed(TelescopeAxes axis, double rate);

        [LoggerMessage(EventId = 4552, Level = LogLevel.Information, Message = "§45 polar-align: {Axis} motor move done, {Moved:F2}° by the mount's reading")]
        private partial void LogAxisMoveDone(TelescopeAxes axis, double moved);

        [LoggerMessage(EventId = 4554, Level = LogLevel.Warning, Message = "§45 polar-align: the mount refused the {Axis} motor move")]
        private partial void LogAxisMoveRefused(TelescopeAxes axis, Exception exception);

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
                // A Next that landed after the previous stage's loop took its own (a double tap in the
                // hand-off) must not be spent on this stage's first check — it would skip the stage.
                _advanceRequested = false;
                // A new stage measures afresh: the previous stage's readout must not stand for it on the
                // status, in a session row written by a Stop before its first solve, or in the client's fold.
                // (The fine stage's last readout the check compares with was copied before this call.)
                _altErrorArcmin = null;
                _azErrorArcmin = null;
                _lastFrame = null;
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

        [LoggerMessage(EventId = 4535, Level = LogLevel.Information, Message = "§45 polar-align: the user moved on from the {From} stage to {To}")]
        private partial void LogStageAdvanced(string from, string to);

        [LoggerMessage(EventId = 4538, Level = LogLevel.Information, Message = "§45 polar-align: the camera is on the axis (Dec {DecDeg:F2}°) — tilting toward Dec {TargetDecDeg:F0}° with the Dec motor")]
        private partial void LogLeavingAxis(double decDeg, double targetDecDeg);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Information, Message = "§45 polar-align three-point check: axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ total {TotalArcmin:F2}′; cone {ConeDeg:F2}°; turn mismatch {MismatchDeg:F3}°; fine stage said {FineArcmin}′")]
        private partial void LogCheckResult(double axisRaDeg, double axisDecDeg, double altArcmin, double azArcmin, double totalArcmin, double coneDeg, double mismatchDeg, double? fineArcmin);
    }
}