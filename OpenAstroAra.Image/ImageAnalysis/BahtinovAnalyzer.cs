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

    /// <summary>One fitted diffraction spike: a straight line through the star crop.</summary>
    /// <param name="AngleDeg">The line's direction in [0, 180), image frame (0° = +x, y down as pixels are stored).</param>
    /// <param name="RhoPx">Signed distance of the line from the crop centre along its normal
    /// (−sin φ, cos φ), in pixels.</param>
    /// <param name="Strength">The spike's peak line integral over the median of all angles: how far it
    /// stands out of the halo and noise (≥ <see cref="BahtinovAnalyzer.MinStrength"/> to be found at all).</param>
    public readonly record struct BahtinovSpike(double AngleDeg, double RhoPx, double Strength);

    /// <summary>
    /// A Bahtinov pattern measured on one frame. Crop coordinates are continuous pixel coordinates inside the
    /// <see cref="CropSize"/>-square crop (pixel i covers [i, i+1)), so they map straight onto a picture of
    /// the crop.
    /// </summary>
    /// <param name="StarX">The star's pixel column in the full frame.</param>
    /// <param name="StarY">The star's pixel row in the full frame.</param>
    /// <param name="CropLeft">The crop's left edge in the full frame.</param>
    /// <param name="CropTop">The crop's top edge in the full frame.</param>
    /// <param name="CropSize">The crop's side in pixels.</param>
    /// <param name="OuterA">One of the two crossing spikes (the X).</param>
    /// <param name="Central">The spike between them, the one that moves with focus.</param>
    /// <param name="OuterB">The other crossing spike.</param>
    /// <param name="IntersectionX">Where the outer spikes cross, crop coordinates.</param>
    /// <param name="IntersectionY">Where the outer spikes cross, crop coordinates.</param>
    /// <param name="OffsetPx">The central spike's signed distance from that crossing, measured along
    /// (<see cref="NormalX"/>, <see cref="NormalY"/>): zero at best focus, changing sign through it.</param>
    /// <param name="NormalX">The unit normal the sign is measured along (see <see cref="BahtinovAnalyzer.Analyze"/>).</param>
    /// <param name="NormalY">The unit normal the sign is measured along.</param>
    /// <param name="SpreadDeg">The angle between the two outer spikes.</param>
    /// <param name="PeakAdu">The star's brightest pixel.</param>
    public sealed record BahtinovMeasurement(
        int StarX, int StarY,
        int CropLeft, int CropTop, int CropSize,
        BahtinovSpike OuterA, BahtinovSpike Central, BahtinovSpike OuterB,
        double IntersectionX, double IntersectionY,
        double OffsetPx,
        double NormalX, double NormalY,
        double SpreadDeg,
        double PeakAdu);

    /// <summary>Why a frame gave no measurement.</summary>
    public enum BahtinovProblem {
        None,
        /// <summary>No star stands clear of the noise.</summary>
        NoStar,
        /// <summary>The star sits too close to the frame edge for its spikes to fit in the crop.</summary>
        NearEdge,
        /// <summary>A star, but not three clear spikes in a Bahtinov arrangement (no mask, too faint, clouds).</summary>
        NoPattern,
    }

    /// <summary>
    /// Bahtinov-mask focus analysis, headless (#1299). The mask turns a bright star into three diffraction
    /// spikes: two cross in an X and the third runs between them, passing through the crossing only at best
    /// focus. This finds the brightest star, crops a square around it, and measures each spike as a straight
    /// line:
    /// <list type="number">
    /// <item>The star's halo is removed ring by ring (the median of each one-pixel annulus around the star),
    /// leaving the spikes, which fill only a small part of each ring; the saturated core is masked.</item>
    /// <item>The remaining light is projected onto the normal of every candidate angle (a Radon transform
    /// over a disc, so every angle sees the same chord length). A spike's angle is where its projection
    /// collapses into one sharp peak; the three strongest well-separated angles are the spikes.</item>
    /// <item>Each angle is refined to 0.05° and each line's offset to a sub-pixel centroid of its ridge.</item>
    /// <item>The spike whose neighbours sit either side of it is the central one; the other two cross at
    /// the X, and the focus error is the central line's signed distance from that crossing.</item>
    /// </list>
    /// Pure arithmetic over a pixel buffer, so it runs on the server and in unit tests alike. NINA's original
    /// (Canny + Hough over a WPF bitmap, unsigned distance) was deleted in the net10 conversion; this is a
    /// rewrite, not a port.
    /// </summary>
    public static class BahtinovAnalyzer {

        /// <summary>The crop side used when the caller gives none: wide enough for the spikes of a bright
        /// star at typical image scales, small enough to analyse well inside a frame time on a Pi 4.</summary>
        public const int DefaultCropSize = 256;

        /// <summary>A spike must stand this many times above the median of all angles' scores.</summary>
        public const double MinStrength = 3.0;

        private const double CoarseStepDeg = 0.5;
        private const double MinSeparationDeg = 4.0;
        private const int MinRadius = 24;
        private const int BlockSize = 8;
        private const double OverlapRadius = 24;

        /// <summary>
        /// The brightest star in the frame, or in the <paramref name="searchRadius"/> window around
        /// <paramref name="near"/> when given (the previous frame's star, so a hot pixel or a passing satellite
        /// elsewhere never steals the lock). Blocks of 8×8 pixels are summed first, so a lone hot pixel loses
        /// to a star's many bright pixels; the peak is then the light-weighted centroid of the star's core.
        /// Null when nothing stands at least 10 noise sigmas above the background.
        /// </summary>
        public static (int X, int Y)? FindBrightStar(ReadOnlySpan<ushort> pixels, int width, int height,
                (int X, int Y)? near = null, int searchRadius = 96) {
            if (width < BlockSize * 2 || height < BlockSize * 2 || pixels.Length < width * height) {
                return null;
            }
            int x0 = 0, y0 = 0, x1 = width, y1 = height;
            if (near is { } n) {
                x0 = Math.Clamp(n.X - searchRadius, 0, width - 1);
                x1 = Math.Clamp(n.X + searchRadius + 1, 1, width);
                y0 = Math.Clamp(n.Y - searchRadius, 0, height - 1);
                y1 = Math.Clamp(n.Y + searchRadius + 1, 1, height);
            }
            var (background, sigma) = BackgroundOf(pixels, width, x0, y0, x1, y1);

            long bestSum = -1;
            int bestBx = x0, bestBy = y0;
            for (var by = y0; by < y1; by += BlockSize) {
                var byEnd = Math.Min(by + BlockSize, y1);
                for (var bx = x0; bx < x1; bx += BlockSize) {
                    var bxEnd = Math.Min(bx + BlockSize, x1);
                    long sum = 0;
                    for (var y = by; y < byEnd; y++) {
                        var row = pixels.Slice(y * width, width);
                        for (var x = bx; x < bxEnd; x++) {
                            sum += row[x];
                        }
                    }
                    if (sum > bestSum) {
                        bestSum = sum;
                        bestBx = bx;
                        bestBy = by;
                    }
                }
            }

            // The brightest 3×3 neighbourhood in and around the winning block.
            int px = bestBx, py = bestBy;
            long best3 = -1;
            for (var y = Math.Max(1, bestBy - BlockSize); y < Math.Min(height - 1, bestBy + 2 * BlockSize); y++) {
                for (var x = Math.Max(1, bestBx - BlockSize); x < Math.Min(width - 1, bestBx + 2 * BlockSize); x++) {
                    long s = 0;
                    for (var dy = -1; dy <= 1; dy++) {
                        var row = (y + dy) * width;
                        s += pixels[row + x - 1] + pixels[row + x] + pixels[row + x + 1];
                    }
                    if (s > best3) {
                        best3 = s;
                        px = x;
                        py = y;
                    }
                }
            }
            var peak = best3 / 9.0;
            if (peak < background + 10 * sigma) {
                return null;
            }

            // Centroid of the core (everything above half the peak within 12 px), twice, so a saturated plateau
            // gives its middle rather than the corner where the 3×3 search first met it.
            var half = background + (peak - background) / 2;
            for (var pass = 0; pass < 2; pass++) {
                double sx = 0, sy = 0, sw = 0;
                for (var y = Math.Max(0, py - 12); y <= Math.Min(height - 1, py + 12); y++) {
                    for (var x = Math.Max(0, px - 12); x <= Math.Min(width - 1, px + 12); x++) {
                        double v = pixels[y * width + x];
                        if (v > half) {
                            var w = v - background;
                            sx += w * x;
                            sy += w * y;
                            sw += w;
                        }
                    }
                }
                if (sw <= 0) {
                    break;
                }
                px = (int)Math.Round(sx / sw);
                py = (int)Math.Round(sy / sw);
            }
            return (px, py);
        }

        /// <summary>
        /// Measure the Bahtinov pattern on the brightest star (or the one near <paramref name="near"/>).
        /// <paramref name="referenceNormal"/> fixes the sign: the central line's normal is turned to agree
        /// with it, so a session keeps one sign convention even as the spike's angle wobbles across 0°/180°.
        /// Without one, the normal points down the image (+y), or right for a horizontal normal. Returns the
        /// measurement, or null with the reason in <paramref name="problem"/>.
        /// </summary>
        public static BahtinovMeasurement? Analyze(ReadOnlySpan<ushort> pixels, int width, int height,
                out BahtinovProblem problem,
                (int X, int Y)? near = null, (double X, double Y)? referenceNormal = null,
                int cropSize = DefaultCropSize) {
            problem = BahtinovProblem.NoStar;
            if (FindBrightStar(pixels, width, height, near) is not { } star) {
                return null;
            }
            var size = Math.Min(cropSize, Math.Min(width, height));
            var left = Math.Clamp(star.X - size / 2, 0, width - size);
            var top = Math.Clamp(star.Y - size / 2, 0, height - size);
            var crop = new double[size * size];
            double peakAdu = 0;
            for (var y = 0; y < size; y++) {
                var row = pixels.Slice((top + y) * width + left, size);
                for (var x = 0; x < size; x++) {
                    crop[y * size + x] = row[x];
                    peakAdu = Math.Max(peakAdu, row[x]);
                }
            }

            // The star's centre in crop coordinates (pixel centres at i + 0.5) and the largest disc around it
            // that stays inside the crop.
            var cx = star.X - left + 0.5;
            var cy = star.Y - top + 0.5;
            var radius = (int)Math.Floor(Math.Min(Math.Min(cx, size - cx), Math.Min(cy, size - cy))) - 1;
            if (radius < MinRadius) {
                problem = BahtinovProblem.NearEdge;
                return null;
            }

            var points = SpikeLight(crop, size, cx, cy, radius);
            problem = BahtinovProblem.NoPattern;
            if (points.Count < 30) {
                return null;
            }

            // Coarse scan over [0, 180).
            var steps = (int)Math.Round(180 / CoarseStepDeg);
            var scores = new double[steps];
            var hist = new double[2 * radius + 8];
            for (var i = 0; i < steps; i++) {
                scores[i] = Score(points, i * CoarseStepDeg, radius, 1.0, hist, out _);
            }
            var sorted = (double[])scores.Clone();
            Array.Sort(sorted);
            var median = Math.Max(sorted[steps / 2], 1e-9);

            var angles = PickPeaks(scores, median);
            if (angles is null) {
                return null;
            }

            // Fit the three lines away from the middle, where the ridges run within a few pixels of each
            // other and pull each other's centroids (fitted from the core out, every offset read ~7% long).
            // The ridges part at r·sin(spread/2) ± offset, so a large offset needs a wider exclusion: fit
            // once, then again with the exclusion sized from the first fit.
            if (Fit(points, angles, radius, median, Math.Max(OverlapRadius, radius / 6.0), referenceNormal) is not { } first) {
                return null;
            }
            var inner = Math.Max(OverlapRadius, (Math.Abs(first.Offset) + 8) / Math.Sin(first.Spread / 2 * Math.PI / 180));
            var fit = inner <= radius * 0.6
                ? Fit(points, angles, radius, median, inner, referenceNormal) ?? first
                : first;

            problem = BahtinovProblem.None;
            return new BahtinovMeasurement(
                star.X, star.Y, left, top, size,
                fit.OuterA, fit.Central, fit.OuterB,
                cx + fit.Ix, cy + fit.Iy,
                fit.Offset, fit.Nx, fit.Ny, fit.Spread, peakAdu);
        }

        private readonly record struct LineFit(
            BahtinovSpike OuterA, BahtinovSpike Central, BahtinovSpike OuterB,
            double Ix, double Iy, double Offset, double Nx, double Ny, double Spread);

        // Refine the three coarse angles on the light beyond `inner` px, name the central spike, cross the
        // outer two and measure the central one from the crossing. Null when the lines do not make a
        // Bahtinov pattern.
        private static LineFit? Fit(List<Light> all, double[] angles, int radius, double median, double inner,
                (double X, double Y)? referenceNormal) {
            var points = all.FindAll(p => p.Dx * p.Dx + p.Dy * p.Dy >= inner * inner);
            if (points.Count < 30) {
                points = all;
            }
            var spikes = new BahtinovSpike[3];
            for (var k = 0; k < 3; k++) {
                spikes[k] = Refine(points, angles[k], radius, median);
            }

            // The central spike is the one opposite the widest gap between the three angles.
            Array.Sort(spikes, (a, b) => a.AngleDeg.CompareTo(b.AngleDeg));
            var g0 = spikes[1].AngleDeg - spikes[0].AngleDeg;
            var g1 = spikes[2].AngleDeg - spikes[1].AngleDeg;
            var g2 = 180 - (spikes[2].AngleDeg - spikes[0].AngleDeg);
            int c = g2 >= g0 && g2 >= g1 ? 1 : (g0 >= g1 ? 2 : 0);
            var central = spikes[c];
            var outerA = spikes[(c + 1) % 3];
            var outerB = spikes[(c + 2) % 3];

            var dA = FoldedDiff(central.AngleDeg, outerA.AngleDeg);
            var dB = FoldedDiff(central.AngleDeg, outerB.AngleDeg);
            var spread = FoldedDiff(outerA.AngleDeg, outerB.AngleDeg);
            // A real mask's central spike bisects the X; three arbitrary bright lines rarely do.
            if (spread < MinSeparationDeg || spread > 80 || Math.Abs(dA - dB) > Math.Max(3.0, 0.35 * spread)) {
                return null;
            }

            // Where the outer spikes cross (relative to the star, the point the rhos are measured from).
            var (nax, nay) = Normal(outerA.AngleDeg);
            var (nbx, nby) = Normal(outerB.AngleDeg);
            var det = nax * nby - nay * nbx;
            if (Math.Abs(det) < Math.Sin(MinSeparationDeg * Math.PI / 180)) {
                return null;
            }
            var ix = (outerA.RhoPx * nby - outerB.RhoPx * nay) / det;
            var iy = (nax * outerB.RhoPx - nbx * outerA.RhoPx) / det;
            if (Math.Sqrt(ix * ix + iy * iy) > radius / 2.0) {
                return null;
            }

            var (ncx, ncy) = Normal(central.AngleDeg);
            var flip = referenceNormal is { } r
                ? ncx * r.X + ncy * r.Y < 0
                : ncy < -1e-12 || (Math.Abs(ncy) <= 1e-12 && ncx < 0);
            var rhoC = central.RhoPx;
            if (flip) {
                ncx = -ncx;
                ncy = -ncy;
                rhoC = -rhoC;
            }
            return new LineFit(outerA, central, outerB, ix, iy, rhoC - (ncx * ix + ncy * iy), ncx, ncy, spread);
        }

        /// <summary>A line's unit normal (−sin φ, cos φ) for a direction φ in degrees.</summary>
        public static (double X, double Y) Normal(double angleDeg) {
            var a = angleDeg * Math.PI / 180;
            return (-Math.Sin(a), Math.Cos(a));
        }

        /// <summary>The acute angle between two line directions, in [0, 90].</summary>
        internal static double FoldedDiff(double a, double b) {
            var d = Math.Abs(a - b) % 180;
            return d > 90 ? 180 - d : d;
        }

        private readonly record struct Light(double Dx, double Dy, double W);

        // The light left once the halo is gone: each pixel inside the disc minus the median of its
        // one-pixel ring and a noise allowance, kept where positive. The core (everything brighter than half
        // the peak, plus a margin) is masked — saturated there, it carries no line.
        private static List<Light> SpikeLight(double[] crop, int size, double cx, double cy, int radius) {
            var rings = new List<double>[radius + 1];
            for (var i = 0; i <= radius; i++) {
                rings[i] = [];
            }
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    var r = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                    if (r <= radius) {
                        rings[r].Add(crop[y * size + x]);
                    }
                }
            }
            var ringMedian = new double[radius + 1];
            for (var i = 0; i <= radius; i++) {
                var ring = rings[i];
                ring.Sort();
                ringMedian[i] = ring.Count == 0 ? 0 : ring[ring.Count / 2];
            }

            // Noise from the outermost rings, where the halo is flat: MAD of the residuals.
            var residuals = new List<double>();
            for (var i = Math.Max(1, radius - 6); i <= radius; i++) {
                foreach (var v in rings[i]) {
                    residuals.Add(Math.Abs(v - ringMedian[i]));
                }
            }
            residuals.Sort();
            var sigma = residuals.Count == 0 ? 1 : Math.Max(1, 1.4826 * residuals[residuals.Count / 2]);

            // The core: rings whose median is above half the way from the edge to the peak, plus a margin.
            var peak = ringMedian[0];
            var floor = ringMedian[radius];
            var core = 0;
            while (core < radius && ringMedian[core] > floor + (peak - floor) / 2) {
                core++;
            }
            core = Math.Min(radius / 3, core * 3 / 2 + 3);

            var points = new List<Light>();
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var dx = x + 0.5 - cx;
                    var dy = y + 0.5 - cy;
                    var rr = Math.Sqrt(dx * dx + dy * dy);
                    var r = (int)Math.Round(rr);
                    if (r <= core || r > radius) {
                        continue;
                    }
                    var w = crop[y * size + x] - ringMedian[r] - 2 * sigma;
                    if (w > 0) {
                        points.Add(new Light(dx, dy, w));
                    }
                }
            }
            return points;
        }

        // The projection's sharpest peak at one angle: the light summed into bins of `bin` px along the
        // normal, scored as the best 3-bin window. `centre` is that window's middle, in px.
        private static double Score(List<Light> points, double angleDeg, int radius, double bin, double[] hist, out double centre) {
            var (nx, ny) = Normal(angleDeg);
            var bins = (int)Math.Ceiling(2 * (radius + 2) / bin) + 1;
            Array.Clear(hist, 0, Math.Min(bins, hist.Length));
            var offset = radius + 2;
            foreach (var p in points) {
                var d = p.Dx * nx + p.Dy * ny;
                var b = (int)Math.Round((d + offset) / bin);
                if ((uint)b < (uint)bins) {
                    hist[b] += p.W;
                }
            }
            double best = 0;
            var at = 0;
            for (var b = 1; b < bins - 1; b++) {
                var s = hist[b - 1] + hist[b] + hist[b + 1];
                if (s > best) {
                    best = s;
                    at = b;
                }
            }
            centre = at * bin - offset;
            return best;
        }

        // The three strongest local maxima of the coarse scan, at least MinSeparationDeg apart (circularly),
        // each MinStrength × the median. Null when there are fewer than three.
        private static double[]? PickPeaks(double[] scores, double median) {
            var n = scores.Length;
            var candidates = new List<int>();
            for (var i = 0; i < n; i++) {
                var v = scores[i];
                var isMax = true;
                for (var k = 1; k <= 2 && isMax; k++) {
                    if (scores[(i + k) % n] > v || scores[(i - k + n) % n] > v) {
                        isMax = false;
                    }
                }
                if (isMax && v >= MinStrength * median) {
                    candidates.Add(i);
                }
            }
            candidates.Sort((a, b) => scores[b].CompareTo(scores[a]));
            var picked = new List<double>();
            foreach (var i in candidates) {
                var angle = i * CoarseStepDeg;
                var clear = true;
                foreach (var p in picked) {
                    if (FoldedDiff(angle, p) < MinSeparationDeg) {
                        clear = false;
                        break;
                    }
                }
                if (clear) {
                    picked.Add(angle);
                    if (picked.Count == 3) {
                        return [.. picked];
                    }
                }
            }
            return null;
        }

        // ±1° around the coarse angle in 0.05° steps on half-pixel bins, then the ridge's centroid (light
        // within 1.5 px of the peak) for the offset.
        private static BahtinovSpike Refine(List<Light> points, double coarseDeg, int radius, double median) {
            var hist = new double[4 * radius + 16];
            double bestScore = -1, bestAngle = coarseDeg, bestCentre = 0;
            for (var k = -20; k <= 20; k++) {
                var angle = coarseDeg + k * 0.05;
                var s = Score(points, angle, radius, 0.5, hist, out var centre);
                if (s > bestScore) {
                    bestScore = s;
                    bestAngle = angle;
                    bestCentre = centre;
                }
            }
            var (nx, ny) = Normal(bestAngle);
            var rho = bestCentre;
            for (var iteration = 0; iteration < 3; iteration++) {
                double sw = 0, sd = 0;
                foreach (var p in points) {
                    var d = p.Dx * nx + p.Dy * ny;
                    if (Math.Abs(d - rho) <= 1.5) {
                        sw += p.W;
                        sd += p.W * d;
                    }
                }
                if (sw <= 0) {
                    break;
                }
                rho = sd / sw;
            }
            // Keep the angle in [0, 180) — the normal (and so the rho's sign) flips with it.
            if (bestAngle < 0) {
                bestAngle += 180;
                rho = -rho;
            } else if (bestAngle >= 180) {
                bestAngle -= 180;
                rho = -rho;
            }
            // Strength on the coarse scan's scale (1 px bins), so it compares with MinStrength.
            var strength = Score(points, bestAngle, radius, 1.0, hist, out _) / median;
            return new BahtinovSpike(bestAngle, rho, strength);
        }

        private static (double Background, double Sigma) BackgroundOf(ReadOnlySpan<ushort> pixels, int width, int x0, int y0, int x1, int y1) {
            var sample = new List<ushort>();
            var stride = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 20_000));
            for (var y = y0; y < y1; y += stride) {
                for (var x = x0; x < x1; x += stride) {
                    sample.Add(pixels[y * width + x]);
                }
            }
            if (sample.Count == 0) {
                return (0, 1);
            }
            sample.Sort();
            double median = sample[sample.Count / 2];
            var deviations = new double[sample.Count];
            for (var i = 0; i < sample.Count; i++) {
                deviations[i] = Math.Abs(sample[i] - median);
            }
            Array.Sort(deviations);
            return (median, Math.Max(1, 1.4826 * deviations[deviations.Length / 2]));
        }
    }
}
