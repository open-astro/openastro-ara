#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Locale;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Notification;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving.Interfaces;
using OpenAstroAra.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.PlateSolving {

    public class ImageSolver : IImageSolver {

        public ImageSolver(IPlateSolver plateSolver, IPlateSolver blindSolver) {
            this.plateSolver = plateSolver;
            this.blindSolver = blindSolver;
        }

        public async Task<PlateSolveResult> Solve(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus>? progress, CancellationToken ct) {
            ValidatePrerequisites(parameter);

            Logger.Info($"Platesolving with parameters: {parameter}");
            progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblPlateSolving"] });

            // Only the LAST stage may post the solver's own "solve failed" notice (a missing star
            // database, exit 32/33, reads as that and not as a generic failure): a hint that fails and
            // a later stage that solves is a success, and the rig inbox showed an error per failed
            // stage. Every earlier stage runs quiet; a blind-only or staged-off solve is its own last
            // stage and keeps the caller's setting.
            PlateSolveResult result;
            if (parameter.Coordinates == null) {
                // A solve that starts blind gets the blind budget too (x5), not the hinted one.
                result = await RunStage("blind", BlindBudget(parameter), source, progress, ct);
            } else {
                // #1136 — staged search. A hinted solve fails for one of three reasons, each with a
                // cheaper fix than a whole-sky search: the mount lost sync but the scope hasn't moved
                // far (search around the last good solve), the profile's optics are wrong (search the
                // scale), or the mount is further off than the radius (widen it). Only then go blind.
                var stages = Stages(parameter, plateSolver is IScaleSearchSolver).ToList();
                result = await RunStage("hint", Quiet(parameter, isLast: stages.Count == 0), source, progress, ct);
                for (var i = 0; i < stages.Count && !result.Success; i++) {
                    result = await RunStage(stages[i].Name, Quiet(stages[i].Parameter, isLast: i == stages.Count - 1), source, progress, ct);
                }
            }

            if (result.Success) {
                Logger.Info($"Platesolve successful: Coordinates: {result.Coordinates} - Position Angle: {result.PositionAngle}");
                SolveMemory.Remember(result.Coordinates);
                ReportScaleMismatch(parameter, result);
            } else {
                Logger.Info($"Platesolve failed");
            }

            progress?.Report(new ApplicationStatus() { Status = string.Empty });

            return result;
        }

        /// <summary>A copy of <paramref name="parameter"/> with the whole-sky budget
        /// (<see cref="SolveTuning.TimeoutFor"/> at 180°) when a timeout is set.</summary>
        private static PlateSolveParameter BlindBudget(PlateSolveParameter parameter) {
            var blind = parameter.Clone();
            if (parameter.Timeout is TimeSpan t) {
                blind.Timeout = SolveTuning.TimeoutFor(t, 180);
            }
            return blind;
        }

        /// <summary>The parameter for one stage: notifications off unless it is the last stage.</summary>
        private static PlateSolveParameter Quiet(PlateSolveParameter parameter, bool isLast) {
            if (isLast) {
                return parameter;
            }
            var quiet = parameter.Clone();
            quiet.DisableNotifications = true;
            quiet.KeepFailedFrame = false;
            return quiet;
        }

        /// <summary>One search stage of the staged search: a name for the log and the parameter it runs with.</summary>
        internal readonly record struct SearchStage(string Name, PlateSolveParameter Parameter);

        /// <summary>The stages that follow a failed hinted solve, in order. Pure, so tests can pin the
        /// plan without a solver: the remembered position (when it is outside the hinted radius; a
        /// known-scale solve there costs a few seconds) → scale search at the hinted radius (only for a
        /// solver that honours it, <paramref name="scaleSearch"/>; with a hint that is simply wrong it
        /// runs to its timeout, which is why it comes second — CM4 rig, 2026-10-19: 61 s) → 30° → 90°
        /// (each only when wider than the last) → blind.</summary>
        internal static IEnumerable<SearchStage> Stages(PlateSolveParameter parameter, bool scaleSearch) {
            if (parameter.Coordinates == null) {
                yield break;
            }
            var radius = parameter.SearchRadius;
            if (parameter.StagedSearchEnabled) {
                var fallback = parameter.FallbackCoordinates ?? SolveMemory.Recall();
                if (fallback != null && SolveTuning.SeparationDeg(fallback, parameter.Coordinates) > radius) {
                    var near = parameter.Clone();
                    near.Coordinates = fallback;
                    near.FallbackCoordinates = null;
                    yield return new SearchStage("last good solve", near);
                }

                if (scaleSearch) {
                    var scale = parameter.Clone();
                    scale.SearchScale = true;
                    yield return new SearchStage("scale search", scale);
                }

                foreach (var wider in SolveTuning.WidenedRadiiDeg) {
                    if (wider <= radius) {
                        continue;
                    }
                    var widened = parameter.Clone();
                    widened.SearchRadius = wider;
                    if (parameter.Timeout is TimeSpan baseTimeout) {
                        widened.Timeout = SolveTuning.TimeoutFor(baseTimeout, wider);
                    }
                    radius = wider;
                    yield return new SearchStage($"radius {wider}°", widened);
                }
            }
            if (parameter.BlindFailoverEnabled) {
                var blind = BlindBudget(parameter);
                blind.Coordinates = null;
                yield return new SearchStage("blind", blind);
            }
        }

        private async Task<PlateSolveResult> RunStage(string name, PlateSolveParameter parameter, IImageData source, IProgress<ApplicationStatus>? progress, CancellationToken ct) {
            var solver = GetSolver(parameter);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await solver.SolveAsync(source, parameter, progress, ct);
            if (!result.Success) {
                var hint = parameter.Coordinates != null ? $" around {parameter.Coordinates} within {parameter.SearchRadius}°" : string.Empty;
                Logger.Info($"Plate solve stage '{name}' failed after {watch.ElapsedMilliseconds} ms{hint}");
            } else {
                Logger.Info($"Plate solve stage '{name}' solved in {watch.ElapsedMilliseconds} ms");
            }
            return result;
        }

        /// <summary>Compare the solved pixel scale with the one the profile's optics predict: a mismatch
        /// over <see cref="SolveTuning.ScaleMismatchTolerance"/> means every future hinted solve starts
        /// from a wrong field of view, so say which focal length the sky measured.</summary>
        private static void ReportScaleMismatch(PlateSolveParameter parameter, PlateSolveResult result) {
            var expected = AstroUtil.ArcsecPerPixel(parameter.PixelSize, parameter.FocalLength);
            if (!SolveTuning.ScaleMismatch(expected, result.Pixscale)) {
                return;
            }
            // Once per ten minutes: on a rig whose optics are wrong every center-after-drift solve
            // would otherwise log it, one warning per light frame.
            var now = DateTime.UtcNow;
            lock (MismatchGate) {
                if (now - lastMismatchWarningUtc < MismatchWarningInterval) {
                    return;
                }
                lastMismatchWarningUtc = now;
            }
            var measured = SolveTuning.FocalLengthFromScale(parameter.PixelSize, result.Pixscale);
            Logger.Warning($"Plate solve - the solved scale {result.Pixscale:0.00}\"/px disagrees with the profile's {expected:0.00}\"/px: the optics imply a focal length of about {measured:0} mm, the profile says {parameter.FocalLength:0} mm (binning {parameter.Binning}). Fix Options → Imaging → Optics so hinted solves start from the right field.");
        }
        private IPlateSolver plateSolver;
        private IPlateSolver blindSolver;

        private static readonly object MismatchGate = new();
        private static DateTime lastMismatchWarningUtc = DateTime.MinValue;
        private static readonly TimeSpan MismatchWarningInterval = TimeSpan.FromMinutes(10);

        protected IPlateSolver GetSolver(PlateSolveParameter parameter) {
            if (parameter.Coordinates == null) {
                return blindSolver;
            } else {
                return plateSolver;
            }
        }

        /// <summary>
        /// Validates general prerequisites that need to be set up to use the plate solvers
        /// </summary>
        protected static void ValidatePrerequisites(PlateSolveParameter parameter) {
            ArgumentNullException.ThrowIfNull(parameter);

            double focalLength = parameter.FocalLength;

            // Check to make sure user has supplied the telescope's effective focal length (in mm)
            if (double.IsNaN(focalLength) || focalLength <= 0) {
                throw new PlateSolverConfigurationException(Loc.Instance["LblPlateSolveNoFocalLength"]);
            }
        }
    }
}