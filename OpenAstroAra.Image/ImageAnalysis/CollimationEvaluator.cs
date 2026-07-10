#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>§59.10 collimation verdict severity, from the aggregate centroid offset as a % of donut diameter:
    /// <see cref="Good"/> &lt; 5%, <see cref="Slight"/> 5–15%, <see cref="Significant"/> &gt; 15%. <see cref="Insufficient"/>
    /// means too few near-centre donut stars for a confident read (no verdict, not "collimation is fine").</summary>
    public enum CollimationSeverity {
        Insufficient,
        Good,
        Slight,
        Significant,
    }

    /// <summary>§59.10 collimation health verdict aggregated over a calibration's donut stars.</summary>
    /// <param name="Severity">The banded verdict (see <see cref="CollimationSeverity"/>).</param>
    /// <param name="OffsetPercent">Magnitude of the vector-averaged obstruction-shadow decentering, as a percent of
    /// the donut diameter. 0 when <see cref="CollimationSeverity.Insufficient"/>.</param>
    /// <param name="DirectionDegrees">Angle of the averaged offset vector in the RAW IMAGE frame (0° = +x, CCW
    /// positive, y-down as pixels are stored), or <see cref="double.NaN"/> when insufficient. This is a
    /// well-defined image-frame direction; mapping it to the §59.10 "clock position viewed from behind the
    /// eyepiece" needs the optical train's mirror flips / camera angle and is the presentation slice's concern.</param>
    /// <param name="StarsUsed">How many near-centre donut stars fed the average.</param>
    public readonly record struct CollimationVerdict(
        CollimationSeverity Severity,
        double OffsetPercent,
        double DirectionDegrees,
        int StarsUsed);

    /// <summary>
    /// §59.10 collimation health — a free byproduct of a Smart Focus calibration (PORT_PLAYBOOK.md §59.10). A
    /// defocused star on an obstructed scope (SCT/Mak/RC/Newtonian) images as a donut; a decentered secondary
    /// (mirror tilt) shifts the central obstruction shadow off the ring centre. <see cref="StarDetector"/> already
    /// measures that per-star shift as <see cref="DetectedStar.DonutCentroidOffsetX"/>/<c>Y</c>; this reduces a
    /// calibration's worth of those into one verdict.
    /// <para><b>False-positive resistance is the priority</b> (§59.10): only stars near the FOV centre are trusted
    /// (off-axis stars carry coma / field curvature that mimics miscollimation), each star's pixel offset is
    /// normalised by its own donut diameter (so stars — and defocus steps — of different sizes are comparable and
    /// the result is a scope-independent percent), and the offsets are <b>vector</b>-averaged: uncorrelated
    /// per-star and atmospheric offsets cancel while a real, fixed secondary tilt adds coherently.</para>
    /// <para>Pure math over an already-measured star list — no hardware — so it is fully unit-testable. This slice
    /// computes magnitude + severity + the image-frame direction; wiring it into the sweep, surfacing the
    /// notification, and the eyepiece-referenced clock-position mapping are later slices.</para>
    /// </summary>
    public static class CollimationEvaluator {

        /// <summary>Lower edge of the 5–15% "slight" band; below this is <see cref="CollimationSeverity.Good"/>.</summary>
        public const double SlightThresholdPercent = 5.0;

        /// <summary>Upper edge of the "slight" band; above this is <see cref="CollimationSeverity.Significant"/>.</summary>
        public const double SignificantThresholdPercent = 15.0;

        /// <summary>Only stars within this fraction of the frame half-diagonal from the centre are trusted — off-axis
        /// stars carry coma / field curvature that masquerades as miscollimation (§59.10 "near the center of the FOV").</summary>
        public const double NearCentreRadiusFraction = 0.3;

        /// <summary>Minimum near-centre donut stars for a confident verdict; below it the read is
        /// <see cref="CollimationSeverity.Insufficient"/> so a two-star fluke can't raise a false alarm.</summary>
        public const int MinStars = 5;

        /// <summary>Reduce a calibration's donut stars (one or more defocus steps, flattened) into a collimation
        /// verdict. Stars without a resolved hole (<see cref="DetectedStar.DonutInnerDiameter"/> ≤ 0 — refractors,
        /// in-focus stars) and off-axis stars are ignored.</summary>
        public static CollimationVerdict Evaluate(IReadOnlyList<DetectedStar> stars, int width, int height) {
            ArgumentNullException.ThrowIfNull(stars);
            if (width <= 0 || height <= 0) {
                return Insufficient(0);
            }
            double cx = width / 2.0, cy = height / 2.0;
            double nearR = NearCentreRadiusFraction * Math.Sqrt((cx * cx) + (cy * cy));
            double nearR2 = nearR * nearR;

            double sumFx = 0, sumFy = 0; // Σ of per-star offset vectors normalised by donut diameter (image frame)
            int used = 0;
            foreach (var s in stars) {
                // Only a genuine donut with a resolved obstruction shadow carries a collimation signal.
                if (s.DonutInnerDiameter <= 0 || s.DonutOuterDiameter <= 0) {
                    continue;
                }
                var (x, y) = s.Unpack(width);
                double dx = x - cx, dy = y - cy;
                if ((dx * dx) + (dy * dy) > nearR2) {
                    continue; // off-axis — coma / field curvature, not collimation
                }
                // Normalise by this star's donut diameter so a fixed tilt reads the same percent regardless of how
                // far out of focus the step is (a wider donut has a proportionally wider absolute shadow shift).
                double inv = 1.0 / s.DonutOuterDiameter;
                sumFx += s.DonutCentroidOffsetX * inv;
                sumFy += s.DonutCentroidOffsetY * inv;
                used++;
            }
            if (used < MinStars) {
                return Insufficient(used);
            }

            double meanFx = sumFx / used, meanFy = sumFy / used;
            double percent = Math.Sqrt((meanFx * meanFx) + (meanFy * meanFy)) * 100.0;
            double direction = Math.Atan2(meanFy, meanFx) * (180.0 / Math.PI);
            CollimationSeverity severity =
                percent < SlightThresholdPercent ? CollimationSeverity.Good
                : percent <= SignificantThresholdPercent ? CollimationSeverity.Slight
                : CollimationSeverity.Significant;
            return new CollimationVerdict(severity, percent, direction, used);
        }

        private static CollimationVerdict Insufficient(int used) =>
            new(CollimationSeverity.Insufficient, 0.0, double.NaN, used);
    }
}
