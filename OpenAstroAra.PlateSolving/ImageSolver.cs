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

            PlateSolveResult result;
            if (parameter.Coordinates == null) {
                result = await RunStage("blind", parameter, source, progress, ct);
            } else {
                result = await RunStage("hint", parameter, source, progress, ct);
                // #1136 — staged search. A hinted solve fails for one of three reasons, each with a
                // cheaper fix than a whole-sky search: the profile's optics are wrong (search the scale),
                // the mount lost sync but the scope hasn't moved far (search around the last good solve),
                // or the mount is further off than the radius (widen it). Only then go blind.
                foreach (var stage in Stages(parameter)) {
                    if (result.Success) {
                        break;
                    }
                    result = await RunStage(stage.Name, stage.Parameter, source, progress, ct);
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

        /// <summary>One search stage of the staged search: a name for the log and the parameter it runs with.</summary>
        internal readonly record struct SearchStage(string Name, PlateSolveParameter Parameter);

        /// <summary>The stages that follow a failed hinted solve, in order. Pure, so tests can pin the
        /// plan without a solver: scale search at the hinted radius → the remembered position (when it
        /// is outside the hinted radius) → 30° → 90° (each only when wider than the last) → blind.</summary>
        internal static IEnumerable<SearchStage> Stages(PlateSolveParameter parameter) {
            if (parameter.Coordinates == null) {
                yield break;
            }
            var radius = parameter.SearchRadius;
            if (parameter.StagedSearchEnabled) {
                var scale = parameter.Clone();
                scale.SearchScale = true;
                yield return new SearchStage("scale search", scale);

                var fallback = parameter.FallbackCoordinates ?? SolveMemory.Recall();
                if (fallback != null && SolveTuning.SeparationDeg(fallback, parameter.Coordinates) > radius) {
                    var near = parameter.Clone();
                    near.Coordinates = fallback;
                    near.FallbackCoordinates = null;
                    yield return new SearchStage("last good solve", near);
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
                var blind = parameter.Clone();
                blind.Coordinates = null;
                if (parameter.Timeout is TimeSpan t) {
                    blind.Timeout = SolveTuning.TimeoutFor(t, 180);
                }
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
            var measured = SolveTuning.FocalLengthFromScale(parameter.PixelSize, result.Pixscale);
            Logger.Warning($"Plate solve - the solved scale {result.Pixscale:0.00}\"/px disagrees with the profile's {expected:0.00}\"/px: the optics imply a focal length of about {measured:0} mm, the profile says {parameter.FocalLength:0} mm (binning {parameter.Binning}). Fix Options → Imaging → Optics so hinted solves start from the right field.");
        }
        private IPlateSolver plateSolver;
        private IPlateSolver blindSolver;

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