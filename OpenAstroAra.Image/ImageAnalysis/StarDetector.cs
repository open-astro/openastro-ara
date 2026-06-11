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
        public static StarDetectionResult Detect(ReadOnlySpan<ushort> pixels, int width, int height, StarDetectionParams parameters) {
            if (width <= 0 || height <= 0) {
                throw new ArgumentException($"Invalid frame dimensions {width}×{height}.");
            }
            if (pixels.Length != (long)width * height) {
                throw new ArgumentException(
                    $"pixel length ({pixels.Length}) doesn't match {width}×{height} = {(long)width * height}.");
            }
            if (pixels.Length == 0) {
                return new StarDetectionResult { DetectedStars = 0, AverageHFR = 0, HFRStdDev = 0 };
            }

            // Optional denoise into a working buffer so the centroid/flux read post-filter values.
            ushort[] work = parameters.NoiseReduction > 0
                ? Median3x3(pixels, width, height)
                : pixels.ToArray();

            var (median, sigma) = BackgroundStats(work);
            // k·σ above the background. Sensitivity carries k directly (8≈Normal, 5≈High, 3≈Highest);
            // guard a non-positive/garbage value to the Normal default so detection never floods.
            double k = parameters.Sensitivity > 0 ? parameters.Sensitivity : 8.0;
            double threshold = median + k * Math.Max(1.0, sigma);

            var stars = FindStars(work, width, height, threshold, background: median);

            // Highest-quality stars first, then honour an explicit cap (0 = no cap).
            stars.Sort((a, b) => b.MaxBrightness.CompareTo(a.MaxBrightness));
            if (parameters.MaxNumberOfStars > 0 && stars.Count > parameters.MaxNumberOfStars) {
                stars.RemoveRange(parameters.MaxNumberOfStars, stars.Count - parameters.MaxNumberOfStars);
            }

            return Summarize(stars);
        }

        // Median + MAD-derived Gaussian-equivalent sigma. MAD is the median of |x - median|; scaling by
        // 1.4826 makes it a consistent σ estimator for Gaussian noise while ignoring the star outliers.
        private static (double median, double sigma) BackgroundStats(ushort[] pixels) {
            var sorted = (ushort[])pixels.Clone();
            Array.Sort(sorted);
            double median = sorted[sorted.Length / 2];

            var dev = new int[sorted.Length];
            for (int i = 0; i < sorted.Length; i++) {
                dev[i] = Math.Abs(sorted[i] - (int)median);
            }
            Array.Sort(dev);
            double mad = dev[dev.Length / 2];
            return (median, mad * 1.4826);
        }

        // 8-connected flood-fill over every above-threshold pixel not yet claimed. Each blob is reduced
        // to a measured star (or rejected). Iterative stack — recursion would blow the stack on a big blob.
        private static List<DetectedStar> FindStars(ushort[] pixels, int width, int height, double threshold, double background) {
            var stars = new List<DetectedStar>();
            var visited = new bool[pixels.Length];
            int maxArea = Math.Max(MaxStarAreaFloor, (int)(MaxStarAreaFraction * pixels.Length));
            var stack = new Stack<int>();
            var blob = new List<int>();

            for (int start = 0; start < pixels.Length; start++) {
                if (visited[start] || pixels[start] <= threshold) {
                    continue;
                }

                blob.Clear();
                stack.Push(start);
                visited[start] = true;
                bool touchesEdge = false;
                bool oversized = false;

                while (stack.Count > 0) {
                    int p = stack.Pop();
                    blob.Add(p);
                    if (blob.Count > maxArea) {
                        // Drain the rest of this component so its pixels stay marked, then drop it.
                        oversized = true;
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

        // Flux-weighted centroid + Half-Flux-Radius. Flux is background-subtracted (f = max(0, v - bg));
        // HFR = Σ f·dist / Σ f, the radius that flux-weights to the star's spread. A saturated peak is
        // rejected — its clipped-flat core biases both centroid and HFR.
        private static DetectedStar? Measure(List<int> blob, ushort[] pixels, int width, double background) {
            double sumF = 0, sumX = 0, sumY = 0;
            ushort peak = 0;
            foreach (int p in blob) {
                ushort v = pixels[p];
                if (v > peak) peak = v;
                double f = v - background;
                if (f <= 0) continue;
                int x = p % width, y = p / width;
                sumF += f;
                sumX += f * x;
                sumY += f * y;
            }
            if (sumF <= 0 || peak >= SaturationLevel) {
                return null;
            }
            double cx = sumX / sumF, cy = sumY / sumF;

            double sumFR = 0;
            foreach (int p in blob) {
                double f = pixels[p] - background;
                if (f <= 0) continue;
                int x = p % width, y = p / width;
                double dx = x - cx, dy = y - cy;
                sumFR += f * Math.Sqrt(dx * dx + dy * dy);
            }
            // A single-pixel-tight star yields HFR 0; clamp to a small floor so it's a usable focus metric.
            double hfr = Math.Max(0.5, sumFR / sumF);

            return new DetectedStar {
                Position = Math.Round(cy) * width + Math.Round(cx),
                HFR = hfr,
                AverageBrightness = sumF / blob.Count + background,
                MaxBrightness = peak,
                Background = background,
            };
        }

        private static StarDetectionResult Summarize(List<DetectedStar> stars) {
            if (stars.Count == 0) {
                return new StarDetectionResult { DetectedStars = 0, AverageHFR = 0, HFRStdDev = 0, StarList = stars };
            }
            double mean = 0;
            foreach (var s in stars) mean += s.HFR;
            mean /= stars.Count;

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
        private static ushort[] Median3x3(ReadOnlySpan<ushort> pixels, int width, int height) {
            var outp = new ushort[pixels.Length];
            Span<ushort> window = stackalloc ushort[9];
            for (int y = 0; y < height; y++) {
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
