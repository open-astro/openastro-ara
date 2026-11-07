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
    /// The three stages of the polar-align routine. <b>Coarse</b>: the mount goes Home (Find Home, or
    /// the user puts it there and taps Continue); with tracking off, every main-camera solve is read
    /// as the RA axis itself (at Home the camera looks along the axis, give or take cone error) and
    /// compared with the apparent pole — rough, but it shows how far off and on which side while
    /// the user makes the big knob moves. <b>Fine</b>: the §45 guide-camera routine — from Home the
    /// mount first slews to Dec 85° (a camera on the axis gives the two-point fit no chord), then the
    /// seed turn, the fit and the live adjust loop. <b>Check</b>: Home again, Dec 85°, three
    /// guide-camera solves with two RA turns of <c>CheckStepDeg</c>, an exact three-point fit, and a
    /// verdict beside the fine stage's last readout. Nothing is adjusted in the check; it says how
    /// good the alignment really is (#1591).
    /// </summary>
    public sealed partial class PolarAlignService {

        // The fine seed and the check need the camera OFF the axis: a pointing within this many
        // degrees of the pole (the mount at Home reports Dec 90°) is slewed out to SeedDecDeg first.
        internal const double OnAxisDecDeg = 87.0;
        internal const double SeedDecDeg = 85.0;
        private const int CheckSolves = 3;

        // ── coarse ───────────────────────────────────────────────────────────────────────────

        private async Task RunCoarseStageAsync(
                PHD2Guider guiderClient, SiteSettingsDto site, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var mainCamera = _mainCamera
                ?? throw new RoutineFailedException("solver_configuration",
                    "the coarse stage needs the main camera's plate solver, which this daemon has not wired up — start at the fine stage instead");
            var renew = MakeLeaseRenewer(guiderClient, ct);
            await GoHomeAsync(settings, renew, gen, ct).ConfigureAwait(false);
            // Tracking off: the solved pointing then moves only with the knobs (§45.8's premise).
            SetTracking(false);
            await SetStageAsync(gen, PolarAlignStages.Coarse, "adjusting").ConfigureAwait(false);
            await SolveLoopAsync(
                capture: (frameId, hint, s, timing, token) => CaptureMainCameraAsync(mainCamera, frameId, hint, s, gen, timing, token),
                // At Home the pointing IS the axis: its distance from the apparent pole is the error.
                toError: (s, at) => PolarAlignGeometry.AxisError(s.RaDegJnow, s.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, at),
                initialHint: CurrentMountPointingJnow(), renew, gen, ct).ConfigureAwait(false);
            LogStageAdvanced(PolarAlignStages.Coarse, PolarAlignStages.Fine);
        }

        /// <summary>One coarse frame: the main camera's snapshot, solved with the main optics. A busy
        /// camera or an unsolved frame is one failed iteration (the loop's pause/retry path); unset
        /// optics fail the routine with the solver's own message.</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A camera or solver fault on one frame is one failed solve, not a routine abort (§45.11); configuration exceptions are excluded and fail the routine.")]
        private async Task<PolarAlignSolveOutcome> CaptureMainCameraAsync(
                IPolarAlignMainCameraSolver mainCamera, string frameId, (double RaDeg, double DecDeg)? hint,
                PolarAlignSettingsDto settings, int gen, FrameTiming timing, CancellationToken ct) {
            var exposure = settings.CoarseExposureSeconds > 0 ? settings.CoarseExposureSeconds : settings.ExposureSeconds;
            timing.ExposureSeconds = exposure;
            var start = Stopwatch.GetTimestamp();
            await PublishFrameStartedAsync(gen, frameId, exposure).ConfigureAwait(false);
            try {
                var s = await mainCamera.CaptureAndSolveAsync(exposure, hint, ct).ConfigureAwait(false);
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
            } catch (CameraBusyException ex) {
                RecordCaptureFault(gen, frameId, ex.Message);
                return new PolarAlignSolveOutcome(false, 0, 0);
            } catch (Exception ex) {
                LogCaptureFailed(frameId, ex.Message);
                return new PolarAlignSolveOutcome(false, 0, 0);
            } finally {
                // One figure: the main-camera path does not split capture from solve.
                timing.Capture = Stopwatch.GetElapsedTime(start);
            }
        }

        // ── fine ─────────────────────────────────────────────────────────────────────────────

        private async Task RunFineStageAsync(
                PHD2Guider guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            await SetStageAsync(gen, PolarAlignStages.Fine, "seeding").ConfigureAwait(false);
            // An Alpaca mount refuses SlewToCoordinatesAsync while not tracking ("requires Tracking to be
            // true", the iOptron on the CM4, 2026-11-05).
            if (!_mount.GetInfo().TrackingEnabled) {
                await TurnTrackingOnOrFailAsync(ct).ConfigureAwait(false);
                LogTrackingEnabled();
            }

            // ── §45.4 binning from the camera: ask the daemon for the guide camera's frame once per
            // run. A daemon without the RPC (or a refused call) is not a failure — auto falls back
            // to 1×1, the right answer for every guide camera anyway.
            _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            var chosenBinning = PolarAlignBinning.Resolve(settings.Binning, _runFrameSize);
            var binningSource = settings.Binning == PolarAlignBinning.Auto ? "auto" : "profile";
            LogBinningChosen(chosenBinning, binningSource, _runFrameSize?.Width ?? 0, _runFrameSize?.Height ?? 0);

            // From Home the camera looks along the axis: the two seed frames would coincide.
            await LeaveTheAxisAsync(settings, ct).ConfigureAwait(false);

            // ── seed: frame A at the current pointing ──
            var hintA = CurrentMountPointingJnow();
            var a = await SolveSeedFrameAsync(guiderClient, workDir, "seed-a", hintA, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-a", a.RaDegJnow, a.DecDegJnow);

            // ── RA slew Δ, away from the meridian so the arc cannot cross it (a crossing risks
            // a meridian flip, which garbles the two-point fit — guider design §5 step 2).
            var (mountRaDeg, mountDecDeg) = CurrentMountPointingJnow()
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            var dirSign = AwayFromMeridianSign(mountRaDeg);
            var slewedPointing = await TurnRaAsync(mountRaDeg, mountDecDeg, dirSign * settings.SeedRotationDeg, settings, ct).ConfigureAwait(false);

            // ── seed: frame B, then the two-point axis fit. The rotation passed to the fit is
            // the mount's OWN RA change (its encoders measure the turn about the mechanical
            // axis). Never the solved RA separation: that is sky RA about the celestial pole,
            // and with the axis a few degrees off the pole a camera near the pole shows a
            // tiny solved ΔRA for a 30° turn — the fit then rejects every seed.
            var signedRotationDeg = Wrap180(slewedPointing.RaDeg - mountRaDeg);
            var rotationDeg = Math.Abs(signedRotationDeg);
            var b = await SolveSeedFrameAsync(guiderClient, workDir, "seed-b", slewedPointing, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("seed-b", b.RaDegJnow, b.DecDegJnow);
            var bTime = DateTimeOffset.UtcNow;
            var axis = FitAxis(a, b, rotationDeg, north, raIncreasing: signedRotationDeg > 0);
            var (seedAltErr, seedAzErr) = PolarAlignGeometry.AxisError(
                axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, bTime);
            var seedPointing = PolarAlignGeometry.PointingAltAz(
                b.RaDegJnow, b.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, bTime);
            LogSeedFit(signedRotationDeg, axis.RaDeg, axis.DecDeg, seedAltErr, seedAzErr);

            // ── live adjust: tracking OFF makes the camera direction stationary in alt/az, so
            // every subsequent solve's alt/az delta from the seed pointing is purely the user's
            // knob adjustment (§45.8) — and the loop needs no further slews.
            SetTracking(false);
            SetErrors(gen, seedAltErr, seedAzErr, "adjusting", frameId: null); // keeps the seed-b attempt's id
            await PublishProgressAsync(gen, 0, seedAltErr, seedAzErr, solved: true).ConfigureAwait(false);
            await SolveLoopAsync(
                capture: (frameId, hint, s, timing, token) => CaptureAndSolveAsync(guiderClient, workDir, frameId, hint, s, gen, timing, token),
                toError: (s, at) => {
                    // §45.8 reverse-projection: the pointing's alt/az delta since the seed IS the knob
                    // adjustment (rigid mount, tracking stopped). Azimuth-knob turns are rotations about
                    // the vertical, so the azimuth ANGLE delta is shared by pointing and axis; it becomes
                    // an arc-distance at the AXIS altitude (≈ apparent pole altitude + current alt error),
                    // with the same east-positive sign convention as PolarAlignGeometry.AxisError.
                    var p = PolarAlignGeometry.PointingAltAz(
                        s.RaDegJnow, s.DecDegJnow, site.LatitudeDeg, site.LongitudeDeg, at);
                    var altErr = seedAltErr + (p.AltDeg - seedPointing.AltDeg) * 60.0;
                    var axisAltDeg = Math.Abs(site.LatitudeDeg)
                        + PolarAlignGeometry.BennettRefractionArcmin(Math.Abs(site.LatitudeDeg)) / 60.0
                        + altErr / 60.0;
                    var azErr = seedAzErr + Wrap180(p.AzDeg - seedPointing.AzDeg)
                        * Math.Cos(axisAltDeg * Math.PI / 180.0) * 60.0 * (north ? 1.0 : -1.0);
                    return (altErr, azErr);
                },
                initialHint: (b.RaDegJnow, b.DecDegJnow), MakeLeaseRenewer(guiderClient, ct), gen, ct).ConfigureAwait(false);

            // Next: remember the fine readout the check will be compared with.
            lock (_gate) {
                if (_generation == gen && _altErrorArcmin is double alt && _azErrorArcmin is double az) {
                    _fineFinalErrorArcmin = Math.Sqrt(alt * alt + az * az);
                }
            }
            LogStageAdvanced(PolarAlignStages.Fine, PolarAlignStages.Check);
        }

        // ── check ────────────────────────────────────────────────────────────────────────────

        private async Task RunCheckStageAsync(
                PHD2Guider guiderClient, string workDir, SiteSettingsDto site, PolarAlignSettingsDto settings, int gen, CancellationToken ct) {
            var north = site.LatitudeDeg > 0;
            var renew = MakeLeaseRenewer(guiderClient, ct);
            await SetStageAsync(gen, PolarAlignStages.Check, settings.UseHome ? "homing" : "checking").ConfigureAwait(false);
            await GoHomeAsync(settings, renew, gen, ct).ConfigureAwait(false);
            if (!_mount.GetInfo().TrackingEnabled) {
                await TurnTrackingOnOrFailAsync(ct).ConfigureAwait(false);
                LogTrackingEnabled();
            }
            if (_runFrameSize is null) {
                // A run that began at the check never read the guide frame size.
                _runFrameSize = await ReadGuideFrameSizeAsync(guiderClient, ct).ConfigureAwait(false);
            }
            await LeaveTheAxisAsync(settings, ct).ConfigureAwait(false);
            await SetStageAsync(gen, PolarAlignStages.Check, "checking").ConfigureAwait(false);

            var step = settings.CheckStepDeg > 0 ? settings.CheckStepDeg : 30.0;
            var (ra0, dec0) = CurrentMountPointingJnow()
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            var dirSign = AwayFromMeridianSign(ra0);

            var s1 = await SolveSeedFrameAsync(guiderClient, workDir, "check-1", (ra0, dec0), settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-1", s1.RaDegJnow, s1.DecDegJnow);
            await NoteCheckSolveAsync(gen, 1).ConfigureAwait(false);

            var p1 = await TurnRaAsync(ra0, dec0, dirSign * step, settings, ct).ConfigureAwait(false);
            var mountTurn1 = Wrap180(p1.RaDeg - ra0);
            var s2 = await SolveSeedFrameAsync(guiderClient, workDir, "check-2", p1, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-2", s2.RaDegJnow, s2.DecDegJnow);
            await NoteCheckSolveAsync(gen, 2).ConfigureAwait(false);

            var p2 = await TurnRaAsync(p1.RaDeg, dec0, dirSign * step, settings, ct).ConfigureAwait(false);
            var mountTurn2 = Wrap180(p2.RaDeg - p1.RaDeg);
            var s3 = await SolveSeedFrameAsync(guiderClient, workDir, "check-3", p2, settings, gen, ct).ConfigureAwait(false);
            LogSeedSolve("check-3", s3.RaDegJnow, s3.DecDegJnow);
            await NoteCheckSolveAsync(gen, 3).ConfigureAwait(false);
            var at = DateTimeOffset.UtcNow;

            (double RaDeg, double DecDeg, double ConeDeg) axis;
            try {
                axis = PolarAlignGeometry.FitAxisThreePoint(
                    s1.RaDegJnow, s1.DecDegJnow, s2.RaDegJnow, s2.DecDegJnow, s3.RaDegJnow, s3.DecDegJnow, north);
            } catch (ArgumentOutOfRangeException ex) {
                throw new RoutineFailedException("axis_fit_failed", ex.Message);
            }
            var (altErr, azErr) = PolarAlignGeometry.AxisError(axis.RaDeg, axis.DecDeg, site.LatitudeDeg, site.LongitudeDeg, at);
            // How far the solved turns disagree with what the mount says it turned: a bad solve, or a
            // mount that turns by other than it reports (the open question behind #1591).
            var turn1 = PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, s1.RaDegJnow, s1.DecDegJnow, s2.RaDegJnow, s2.DecDegJnow);
            var turn2 = PolarAlignGeometry.TurnAboutAxisDeg(axis.RaDeg, axis.DecDeg, s2.RaDegJnow, s2.DecDegJnow, s3.RaDegJnow, s3.DecDegJnow);
            var mismatch = Math.Max(Math.Abs(Math.Abs(turn1) - Math.Abs(mountTurn1)), Math.Abs(Math.Abs(turn2) - Math.Abs(mountTurn2)));
            var total = Math.Sqrt(altErr * altErr + azErr * azErr);
            double? fine;
            lock (_gate) {
                fine = _fineFinalErrorArcmin;
            }
            var result = new PolarAlignCheckResultDto(
                AltitudeErrorArcmin: Math.Round(altErr, 2),
                AzimuthErrorArcmin: Math.Round(azErr, 2),
                TotalErrorArcmin: Math.Round(total, 2),
                ConeHalfAngleDeg: Math.Round(axis.ConeDeg, 3),
                TurnMismatchDeg: Math.Round(mismatch, 3),
                FineErrorArcmin: fine is double f ? Math.Round(f, 2) : null,
                FineDeltaArcmin: fine is double g ? Math.Round(Math.Abs(total - g), 2) : null,
                Verdict: VerdictFor(total),
                MeasuredAt: at);
            LogCheckResult(axis.RaDeg, axis.DecDeg, altErr, azErr, total, axis.ConeDeg, mismatch, fine);
            await FinishCheckedAsync(gen, result).ConfigureAwait(false);
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
            await ReleaseLeaseBestEffortAsync(CancellationToken.None).ConfigureAwait(false);
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

        /// <summary>Send the mount Home: Find Home when it can, else park the routine on the user
        /// (state <c>homing</c>, <c>WaitingForHome</c>, the needs_home event) until Continue. Off with
        /// <c>UseHome</c> = the current pointing is "home".</summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Any mount fault during Find Home is this routine's home_failed, with the mount's message; never internal_error.")]
        private async Task GoHomeAsync(PolarAlignSettingsDto settings, Func<Task> renewLeaseIfDue, int gen, CancellationToken ct) {
            if (!settings.UseHome) {
                return;
            }
            var info = _mount.GetInfo();
            if (info.CanFindHome) {
                SetState(gen, "homing");
                LogFindingHome();
                bool homed;
                try {
                    // #1536 — the failure is this routine's to report; as a mount-op fault it would count toward abort-and-park.
                    homed = await TelescopeService.RunWithoutOpFaultsAsync(
                        () => _mount.FindHome(new Progress<ApplicationStatus>(), ct)).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    throw new RoutineFailedException("home_failed", $"the mount refused Find Home: {ex.Message}");
                }
                if (!homed) {
                    throw new RoutineFailedException("home_failed",
                        "the mount did not reach its Home position — put it at Home from the mount controls, then start again");
                }
                await Task.Delay(TimeSpan.FromSeconds(settings.SettleSeconds), ct).ConfigureAwait(false);
                return;
            }
            // No Find Home: the user puts the mount at Home (counterweights down, tube along the axis).
            SetState(gen, "homing");
            SetWaitingForHome(gen, true);
            LogWaitingForHome();
            await PublishAsync(WsEventCatalog.PolarAlignNeedsHome, new JsonObject {
                ["stage"] = CurrentStage(),
                ["message"] = "This mount cannot find Home by itself. Put it in its home position (counterweights down, tube pointing at the pole), then tap Continue.",
            }).ConfigureAwait(false);
            try {
                await WaitForAdvanceAsync(gen, renewLeaseIfDue, ct).ConfigureAwait(false);
            } finally {
                SetWaitingForHome(gen, false);
            }
        }

        /// <summary>Park until the user's Continue/Next, keeping the guider lease alive.</summary>
        private async Task WaitForAdvanceAsync(int gen, Func<Task> renewLeaseIfDue, CancellationToken ct) {
            while (!TakeAdvanceRequest(gen)) {
                await renewLeaseIfDue().ConfigureAwait(false);
                await Task.Delay(CaptureRequestPoll, ct).ConfigureAwait(false);
            }
        }

        /// <summary>A camera looking along the RA axis (the mount at Home, Dec ≈ 90°) gives the seed and
        /// the check no circle to fit: slew to Dec 85° at the current RA first, tracking on.</summary>
        private async Task LeaveTheAxisAsync(PolarAlignSettingsDto settings, CancellationToken ct) {
            var (raDeg, decDeg) = CurrentMountPointingJnow()
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            if (Math.Abs(decDeg) <= OnAxisDecDeg) {
                return;
            }
            var target = new Coordinates(raDeg, Math.CopySign(SeedDecDeg, decDeg), Epoch.JNOW, Coordinates.RAType.Degrees);
            LogLeavingAxis(decDeg, target.Dec);
            await SlewOrFailAsync(target, "the mount refused the slew from Home to Dec 85° — slew it there from the mount controls, then start again", ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(settings.SettleSeconds), ct).ConfigureAwait(false);
        }

        /// <summary>With HA = LST − RA, decreasing RA increases HA (further west): turn west when
        /// already west of the meridian, east when east — the arc then cannot cross it.</summary>
        private double AwayFromMeridianSign(double mountRaDeg) {
            var lstDeg = _mount.GetInfo().SiderealTime * 15.0;
            var haDeg = Wrap180(lstDeg - mountRaDeg);
            return haDeg >= 0 ? -1.0 : 1.0;
        }

        /// <summary>Turn the RA axis by <paramref name="signedDeltaDeg"/> as a GoTo at the same Dec, settle,
        /// and return the mount's reported pointing afterwards. A mount that reports under 1° of turn fails
        /// as <c>slew_failed</c>.</summary>
        private async Task<(double RaDeg, double DecDeg)> TurnRaAsync(
                double fromRaDeg, double decDeg, double signedDeltaDeg, PolarAlignSettingsDto settings, CancellationToken ct) {
            var targetRaDeg = (fromRaDeg + signedDeltaDeg + 360.0) % 360.0;
            var target = new Coordinates(targetRaDeg, decDeg, Epoch.JNOW, Coordinates.RAType.Degrees);
            await SlewOrFailAsync(target, SeedSlewRefusedMessage, ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(settings.SettleSeconds), ct).ConfigureAwait(false);
            var after = CurrentMountPointingJnow()
                ?? throw new RoutineFailedException("mount_fault", "the mount reports no current pointing");
            var turned = Math.Abs(Wrap180(after.RaDeg - fromRaDeg));
            if (turned < 1.0) {
                throw new RoutineFailedException("slew_failed",
                    $"the mount reports an RA change of {turned:F2}° after the turn — it did not rotate");
            }
            return after;
        }

        private async Task SlewOrFailAsync(Coordinates target, string refusedMessage, CancellationToken ct) {
            await KeepTrackingOnForSlewAsync(ct).ConfigureAwait(false);
            bool slewed;
            try {
                // #1536 — the failure is this routine's to report; as a mount-op fault it would count toward abort-and-park.
                slewed = await TelescopeService.RunWithoutOpFaultsAsync(() => _mount.SlewToCoordinatesAsync(target, ct)).ConfigureAwait(false);
            } catch (SequenceEntityFailedException ex) {
                // #1222 — the mediator refuses a slew it cannot frame (#1124: equatorial system
                // unknown) or that the mount rejected by throwing; that is a slew failure, not
                // internal_error.
                LogSeedSlewRefused(ex.Message);
                throw new RoutineFailedException("slew_failed", refusedMessage);
            }
            if (!slewed) {
                throw new RoutineFailedException("slew_failed", refusedMessage);
            }
            await _mount.WaitForSlew(ct).ConfigureAwait(false);
        }

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

        private void SetWaitingForHome(int gen, bool waiting) {
            lock (_gate) {
                if (_generation == gen) {
                    _waitingForHome = waiting;
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

        [LoggerMessage(EventId = 4536, Level = LogLevel.Information, Message = "§45 polar-align: sending the mount Home (Find Home)")]
        private partial void LogFindingHome();

        [LoggerMessage(EventId = 4537, Level = LogLevel.Information, Message = "§45 polar-align: the mount cannot find Home — waiting for the user to put it there")]
        private partial void LogWaitingForHome();

        [LoggerMessage(EventId = 4538, Level = LogLevel.Information, Message = "§45 polar-align: the camera is on the axis (Dec {DecDeg:F2}°) — slewing to Dec {TargetDecDeg:F0}° for the turn")]
        private partial void LogLeavingAxis(double decDeg, double targetDecDeg);

        [LoggerMessage(EventId = 4539, Level = LogLevel.Information, Message = "§45 polar-align three-point check: axis RA {AxisRaDeg:F4}° Dec {AxisDecDeg:F4}°; error alt {AltArcmin:F2}′ az {AzArcmin:F2}′ total {TotalArcmin:F2}′; cone {ConeDeg:F2}°; turn mismatch {MismatchDeg:F3}°; fine stage said {FineArcmin}′")]
        private partial void LogCheckResult(double axisRaDeg, double axisDecDeg, double altArcmin, double azArcmin, double totalArcmin, double coneDeg, double mismatchDeg, double? fineArcmin);
    }
}
