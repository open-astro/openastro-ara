using OpenAstroAra.Astrometry;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.PlateSolving {

    /// <summary>
    /// #1136 — per-solve tuning derived from the rig's geometry instead of fixed profile numbers. The
    /// solver's inputs that matter for speed are the image scale (focal length ÷ pixel size), the
    /// downsample factor and the search radius; the profile already knows the first, so the other two
    /// follow from it. Pure functions, pinned by <c>SolveTuningTest</c>.
    /// </summary>
    public static class SolveTuning {

        /// <summary>Arcsec per (downsampled) pixel the solver works best at: star detection wants a
        /// few pixels per star, and ASTAP's quad matching gets no better below this but gets slower.</summary>
        public const double TargetArcsecPerPixel = 2.0;

        /// <summary>Never downsample a frame below this height; a tiny image loses the faint stars a
        /// narrow field needs.</summary>
        public const double MinimumHeightPx = 600;

        public const int MaxDownsample = 4;

        /// <summary>Downsample factor for a frame at <paramref name="arcsecPerPixel"/> native scale: the
        /// largest factor that keeps the downsampled scale at or under <see cref="TargetArcsecPerPixel"/>
        /// and the height at or over <see cref="MinimumHeightPx"/>, clamped to 1..<see cref="MaxDownsample"/>.
        /// A RedCat (3.1"/px) solves at 1, a 1000 mm refractor (0.8"/px) at 2, a C11 (0.3"/px) at 4.</summary>
        public static int DownsampleFor(double arcsecPerPixel, double imageHeightPx) {
            if (!(arcsecPerPixel > 0) || double.IsNaN(arcsecPerPixel) || double.IsInfinity(arcsecPerPixel)) {
                return 1;
            }
            var factor = (int)Math.Floor(TargetArcsecPerPixel / arcsecPerPixel);
            factor = Math.Clamp(factor, 1, MaxDownsample);
            if (imageHeightPx > 0) {
                while (factor > 1 && imageHeightPx / factor < MinimumHeightPx) {
                    factor--;
                }
            }
            return factor;
        }

        /// <summary>Search radii the staged search widens through after the hinted radius fails, before
        /// a blind solve. Each stage only runs when it is wider than the previous one.</summary>
        public static readonly IReadOnlyList<double> WidenedRadiiDeg = new[] { 30.0, 90.0 };

        /// <summary>The budget for one solver run at <paramref name="searchRadiusDeg"/>, scaled from the
        /// profile's per-solve timeout (meant for the hinted radius): ×1 up to 30°, ×2 up to 90°, ×5 for a
        /// blind (whole-sky) search. A fixed timeout either cuts a legitimate blind solve short or lets a
        /// hopeless hinted one run for minutes.</summary>
        public static TimeSpan TimeoutFor(TimeSpan baseTimeout, double searchRadiusDeg) {
            var factor = searchRadiusDeg switch {
                <= 30 => 1,
                <= 90 => 2,
                _ => 5,
            };
            return TimeSpan.FromTicks(baseTimeout.Ticks * factor);
        }

        /// <summary>Focal length (mm) implied by a solved pixel scale, for the "your optics are wrong"
        /// warning and the write-back after a scale search: <c>pixelSizeUm × 206.265 / arcsecPerPixel</c>.
        /// <paramref name="pixelSizeUm"/> is the binned pixel size the solve ran with.</summary>
        public static double FocalLengthFromScale(double pixelSizeUm, double arcsecPerPixel) {
            if (!(pixelSizeUm > 0) || !(arcsecPerPixel > 0)) {
                return double.NaN;
            }
            return pixelSizeUm * 206.265 / arcsecPerPixel;
        }

        /// <summary>Relative pixel-scale mismatch above which a solve says the profile's focal length is
        /// wrong (10%: a reducer the profile doesn't know about is 20–40%; seeing or a slight
        /// focal-length rounding is well under 5%).</summary>
        public const double ScaleMismatchTolerance = 0.10;

        /// <summary>True when the solved scale disagrees with the expected one by more than
        /// <see cref="ScaleMismatchTolerance"/>.</summary>
        public static bool ScaleMismatch(double expectedArcsecPerPixel, double solvedArcsecPerPixel) {
            if (!(expectedArcsecPerPixel > 0) || !(solvedArcsecPerPixel > 0) || double.IsNaN(solvedArcsecPerPixel)) {
                return false;
            }
            return Math.Abs(solvedArcsecPerPixel - expectedArcsecPerPixel) / expectedArcsecPerPixel > ScaleMismatchTolerance;
        }

        /// <summary>Angular distance in degrees between two positions (null-safe: NaN when either is null).</summary>
        public static double SeparationDeg(Coordinates? a, Coordinates? b) {
            if (a is null || b is null) {
                return double.NaN;
            }
            return (a.Transform(Epoch.J2000) - b.Transform(Epoch.J2000)).Distance.Degree;
        }
    }
}
