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
using System.Threading;

namespace OpenAstroAra.Image.ImageAnalysis {

    /// <summary>
    /// §2105 headless star detection + Half-Flux-Radius measurement on a raw 16-bit grayscale frame.
    /// This is the from-scratch, dependency-free replacement for NINA's OpenCvSharp4-backed detector
    /// (the §26 decision ruled OpenCvSharp4 out — its native runtimes don't align across our target
    /// RIDs). The pipeline is the standard one:
    ///
    ///   1. Robust background estimate — median + MAD (σ ≈ 1.4826·MAD), immune to the star pixels.
    ///   2. Threshold at median + k·σ; k comes from <see cref="StarDetectionParams.Sensitivity"/>
    ///      (lower k ⇒ more sensitive ⇒ more stars).
    ///   3. Optional 3×3 median pre-filter for noisy frames (driven by NoiseReduction).
    ///   4. 8-connected flood-fill to group above-threshold pixels into blobs.
    ///   5. Per blob: flux-weighted centroid, total background-subtracted flux, peak; reject blobs that
    ///      are too small (noise), saturated/bloomed, touch the frame edge (truncated), or too large
    ///      (not a star). HFR = flux-weighted mean distance from the centroid (the NINA convention).
    ///
    /// CPU-bound (a full-resolution flood-fill); callers on a UI/event thread must offload.
    /// </summary>
    public static class StarDetector {

        // A blob smaller than this is almost certainly a hot pixel / noise speck, not a star.
        private const int MinStarPixels = 5;
        // A blob larger than this (relative to frame area) is a nebula/gradient/cluster, not a single star.
        // Generous on purpose: a bright in-focus star can span a few hundred pixels, and a low-noise frame
        // pushes the threshold near the noise floor, fattening every blob — the cap is a "spans much of the
        // frame" guard, not a tight size filter.
        private const double MaxStarAreaFraction = 0.02;
        // Floor for the area cap so small frames (tests, ROIs) don't reject normal stars.
        private const int MaxStarAreaFloor = 500;
        // Peak at/above this counts as saturated — its HFR is unreliable (the core is clipped flat).
        private const ushort SaturationLevel = 65000;

        /// <summary>
        /// Detect stars in a 16-bit grayscale frame and return their count + HFR statistics.
        /// <paramref name="pixels"/> is row-major <c>width·height</c>; out-of-range dimensions throw.
        /// </summary>
        public static StarDetectionResult Detect(ReadOnlySpan<ushort> pixels, int width, int height, StarDetectionParams parameters, CancellationToken token = default) {
            if (width <= 0 || height <= 0) {
                throw new ArgumentException($"Invalid frame dimensions {width}×{height}.");
            }
            if (pixels.Length != (long)width * height) {
                throw new ArgumentException(
                    $"pixel length ({pixels.Length}) doesn't match {width}×{height} = {(long)width * height}.");
            }
            // width>0 && height>0 && length==width*height ⇒ length>0, so no empty-frame branch is needed.

            // Optional denoise into a working buffer so the centroid/flux read post-filter values.
            // Only the denoise path needs a copy; BackgroundStats/FindStars read the pixels through a span
            // without mutating, so the no-noise-reduction path (the common case, and what §59 autofocus hits
            // per-frame) avoids a full-frame clone entirely.
            ReadOnlySpan<ushort> work = pixels;
            if (parameters.NoiseReduction > 0) {
                work = Median3x3(pixels, width, height, token);
            }

            token.ThrowIfCancellationRequested();
            var (median, sigma) = BackgroundStats(work);
            // k·σ above the background. Sensitivity carries k directly (8≈Normal, 5≈High, 3≈Highest);
            // guard a non-positive/garbage value to the Normal default so detection never floods.
            double k = parameters.Sensitivity > 0 ? parameters.Sensitivity : 8.0;
            double threshold = median + k * Math.Max(1.0, sigma);

            var stars = FindStars(work, width, height, threshold, background: median, token);

            // Highest-quality stars first, then honour an explicit cap (≤ 0 = no cap). With a cap, the
            // returned count + HFR stats reflect the brightest-N, not the whole field — this is the NINA
            // convention (bright stars give the cleanest, least-noisy HFR for focus), but a §59 autofocus
            // caller passing a small cap should know its HFR is a bright-star statistic, not a field average.
            stars.Sort((a, b) => b.MaxBrightness.CompareTo(a.MaxBrightness));
            if (parameters.MaxNumberOfStars > 0 && stars.Count > parameters.MaxNumberOfStars) {
                stars.RemoveRange(parameters.MaxNumberOfStars, stars.Count - parameters.MaxNumberOfStars);
            }

            return Summarize(stars);
        }

        // Background is a sky-wide statistic, so a strided subsample (~BackgroundSampleTarget pixels)
        // gives the same median/MAD as the full frame at a fraction of the cost — two full-frame sorts on
        // a 20-50 MP frame would be 1-3 s and a full clone; the sample is a few thousand entries and a sub-ms
        // sort. MAD is the median of |x - median|; ×1.4826 makes it a consistent σ estimator for Gaussian
        // noise while ignoring the star outliers. Frames at/under the target sample in full (stride 1).
        private const int BackgroundSampleTarget = 20000;

        private static (double median, double sigma) BackgroundStats(ReadOnlySpan<ushort> pixels) {
            int stride = Math.Max(1, pixels.Length / BackgroundSampleTarget);
            int n = (pixels.Length + stride - 1) / stride; // ceil(length/stride) — exact count of i=0,stride,2·stride,… < length
            var sample = new ushort[n];
            for (int i = 0, j = 0; i < pixels.Length; i += stride) {
                sample[j++] = pixels[i];
            }
            Array.Sort(sample);
            int median = sample[sample.Length / 2];

            // Reuse the sample buffer for |x - median| in place — the deviation of a ushort from an
            // integral median is itself ≤ 65535, so it fits, and we avoid a second allocation.
            for (int i = 0; i < sample.Length; i++) {
                sample[i] = (ushort)Math.Abs(sample[i] - median);
            }
            Array.Sort(sample);
            double mad = sample[sample.Length / 2];
            return (median, mad * 1.4826);
        }

        // 8-connected flood-fill over every above-threshold pixel not yet claimed. Each blob is reduced
        // to a measured star (or rejected). Iterative stack — recursion would blow the stack on a big blob.
        private static List<DetectedStar> FindStars(ReadOnlySpan<ushort> pixels, int width, int height, double threshold, double background, CancellationToken token) {
            var stars = new List<DetectedStar>();
            // BitArray (1 bit/pixel) over bool[] (1 byte/pixel): ~6 MB vs ~50 MB for a 50 MP frame —
            // meaningful headroom on the Raspberry Pi target that shares RAM with the OS + FITS I/O.
            var visited = new System.Collections.BitArray(pixels.Length);
            int maxArea = Math.Max(MaxStarAreaFloor, (int)(MaxStarAreaFraction * pixels.Length));
            var stack = new Stack<int>();
            var blob = new List<int>();
            int popsSinceCancelCheck = 0;

            for (int start = 0; start < pixels.Length; start++) {
                // Cheap periodic cancellation so a cancelled DetectStars actually short-circuits the
                // flood-fill (the bulk of the wall-clock) rather than only being checked after it returns.
                if ((start & 0xFFFF) == 0) {
                    token.ThrowIfCancellationRequested();
                }
                if (visited[start] || pixels[start] <= threshold) {
                    continue;
                }

                blob.Clear();
                stack.Push(start);
                visited[start] = true;
                bool touchesEdge = false;
                bool oversized = false;

                while (stack.Count > 0) {
                    // The outer check fires per start-pixel, but a single frame-spanning component drains
                    // entirely inside this while — so check here too, else a cancel during one giant blob
                    // wouldn't be seen until the whole component finished.
                    if ((++popsSinceCancelCheck & 0xFFF) == 0) {
                        token.ThrowIfCancellationRequested();
                    }
                    int p = stack.Pop();
                    // Once oversized, stop GROWING the blob list (it would otherwise reach O(frame) entries
                    // and tens of MB held until FindStars returns) but keep draining the stack so the WHOLE
                    // component is marked visited and discarded as one unit. Draining matters for correctness:
                    // abandoning the component mid-flood would re-seed its remainder as fresh starts, and the
                    // tail fragment could be small enough to masquerade as a star. The transient frontier
                    // stack is bounded by the component (≤ frame) — the same order as the input we already hold.
                    // Caveat for the Pi target: a frame-spanning bright region (sub-half-frame so the median
                    // threshold stays below it) can briefly push the int stack toward O(frame), ~2× the
                    // working set for that one call; it's freed the instant the component is discarded. If
                    // profiling ever flags this, swap to a scanline fill (frontier = spans, not pixels).
                    if (!oversized) {
                        blob.Add(p);
                        if (blob.Count > maxArea) {
                            oversized = true;
                        }
                    }
                    int px = p % width, py = p / width;
                    if (px == 0 || py == 0 || px == width - 1 || py == height - 1) {
                        touchesEdge = true;
                    }
                    for (int dy = -1; dy <= 1; dy++) {
                        int ny = py + dy;
                        if (ny < 0 || ny >= height) continue;
                        for (int dx = -1; dx <= 1; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            int nx = px + dx;
                            if (nx < 0 || nx >= width) continue;
                            int np = ny * width + nx;
                            if (!visited[np] && pixels[np] > threshold) {
                                visited[np] = true;
                                stack.Push(np);
                            }
                        }
                    }
                }

                if (oversized || touchesEdge || blob.Count < MinStarPixels) {
                    continue;
                }
                var star = Measure(blob, pixels, width, background);
                if (star != null) {
                    stars.Add(star);
                }
            }
            return stars;
        }

        // Flux-weighted centroid + Half-Flux-Radius. Flux is background-subtracted (f = v - bg); every blob
        // pixel is above threshold = bg + k·σ > bg, so f > 0 throughout — no per-pixel positivity guard is
        // needed. HFR = Σ f·dist / Σ f, the radius that flux-weights to the star's spread. A saturated peak
        // is rejected — its clipped-flat core biases both centroid and HFR. The same second pass folds in
        // the flux-weighted second-moment tensor that yields the §59 Smart Focus shape features.
        private static DetectedStar? Measure(List<int> blob, ReadOnlySpan<ushort> pixels, int width, double background) {
            double sumF = 0, sumX = 0, sumY = 0, sumV = 0;
            ushort peak = 0;
            foreach (int p in blob) {
                ushort v = pixels[p];
                if (v > peak) peak = v;
                sumV += v;
                double f = v - background;
                int x = p % width, y = p / width;
                sumF += f;
                sumX += f * x;
                sumY += f * y;
            }
            if (peak >= SaturationLevel) {
                return null;
            }
            double cx = sumX / sumF, cy = sumY / sumF;

            // Second pass: HFR (Σ f·dist / Σ f) plus the flux-weighted covariance Mxx/Myy/Mxy about the
            // centroid. The covariance carries both the size (⟨r²⟩ = Mxx + Myy) and the shape (its eigenvalue
            // ratio) of the flux distribution — the raw material for FWHM + roundness below.
            double sumFR = 0, mxx = 0, myy = 0, mxy = 0;
            foreach (int p in blob) {
                double f = pixels[p] - background;
                int x = p % width, y = p / width;
                double dx = x - cx, dy = y - cy;
                sumFR += f * Math.Sqrt(dx * dx + dy * dy);
                mxx += f * dx * dx;
                myy += f * dy * dy;
                mxy += f * dx * dy;
            }
            // A single-pixel-tight star yields HFR 0; clamp to a small floor so it's a usable focus metric.
            double hfr = Math.Max(0.5, sumFR / sumF);

            return new DetectedStar {
                Position = Math.Round(cy) * width + Math.Round(cx),
                HFR = hfr,
                AverageBrightness = sumV / blob.Count, // mean raw brightness over every blob pixel
                MaxBrightness = peak,
                Background = background,
                FWHM = FwhmFromMoments(mxx / sumF, myy / sumF),
                Roundness = RoundnessFromMoments(mxx / sumF, myy / sumF, mxy / sumF),
                // Floor the denominator to 1 ADU (the sensor noise floor) rather than branching on
                // background == 0: a real frame's median background is a non-negative integer that's
                // essentially always ≥ bias, but flooring keeps this a bounded, continuous, scale-invariant
                // ratio even on an all-dark/bias-free synthetic frame — a raw-peak fallback there would
                // inject an outlier of a wholly different magnitude into the §59.3 median the model trains on.
                PeakToBackground = (peak - background) / Math.Max(1.0, background),
            };
        }

        // For a 2D Gaussian the flux-weighted radial second moment ⟨r²⟩ = cxx + cyy equals 2σ², so
        // σ = √(⟨r²⟩/2) and FWHM = 2√(2 ln 2)·σ. The threshold truncates the wings symmetrically, so the
        // absolute value is a slight under-estimate but monotonic in true defocus — which is all the §59
        // inverse map needs. Guarded to 0 for a degenerate single-pixel blob.
        private const double FwhmPerSigma = 2.354820045; // 2·√(2·ln 2)

        private static double FwhmFromMoments(double cxx, double cyy) {
            double meanR2 = cxx + cyy;
            return meanR2 > 0 ? FwhmPerSigma * Math.Sqrt(meanR2 / 2.0) : 0.0;
        }

        // Roundness = √(λ_min / λ_max) of the 2×2 flux covariance — the minor/major principal-axis ratio,
        // 1 for a circularly symmetric star and → 0 as it elongates (drift, tilt, coma). Eigenvalues of a
        // symmetric 2×2 matrix in closed form; the minor is clamped to 0 against float round-off.
        private static double RoundnessFromMoments(double cxx, double cyy, double cxy) {
            double half = (cxx + cyy) / 2.0;
            double diff = (cxx - cyy) / 2.0;
            double common = Math.Sqrt(diff * diff + cxy * cxy);
            double lambdaMax = half + common;
            double lambdaMin = half - common;
            if (lambdaMax <= 0) {
                return 1.0;
            }
            return Math.Sqrt(Math.Max(0.0, lambdaMin) / lambdaMax);
        }

        private static StarDetectionResult Summarize(List<DetectedStar> stars) {
            if (stars.Count == 0) {
                return new StarDetectionResult { DetectedStars = 0, AverageHFR = 0, HFRStdDev = 0, StarList = stars };
            }
            double mean = 0;
            foreach (var s in stars) mean += s.HFR;
            mean /= stars.Count;

            // Population variance (÷ N, not ÷ N−1): HFR spread is a descriptive statistic over the whole
            // detected set, not an inference about a larger sample — and a single star correctly yields 0.
            double variance = 0;
            foreach (var s in stars) {
                double d = s.HFR - mean;
                variance += d * d;
            }
            variance /= stars.Count;

            return new StarDetectionResult {
                DetectedStars = stars.Count,
                AverageHFR = mean,
                HFRStdDev = Math.Sqrt(variance),
                StarList = stars,
            };
        }

        // Light 3×3 median filter (edge pixels mirror-clamped) to knock down salt-and-pepper noise
        // before thresholding — keeps single hot pixels from registering as stars on noisy frames.
        private static ushort[] Median3x3(ReadOnlySpan<ushort> pixels, int width, int height, CancellationToken token) {
            var outp = new ushort[pixels.Length];
            Span<ushort> window = stackalloc ushort[9];
            for (int y = 0; y < height; y++) {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++) {
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++) {
                        int sy = Math.Clamp(y + dy, 0, height - 1);
                        for (int dx = -1; dx <= 1; dx++) {
                            int sx = Math.Clamp(x + dx, 0, width - 1);
                            window[n++] = pixels[sy * width + sx];
                        }
                    }
                    window.Sort();
                    outp[y * width + x] = window[4];
                }
            }
            return outp;
        }
    }
}
