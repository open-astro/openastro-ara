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
            var (median, sigma) = BackgroundStats(work, width, height);
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

        // Background is a sky-wide statistic, so a subsample (~BackgroundSampleTarget pixels) gives the same
        // median/MAD as the full frame at a fraction of the cost — two full-frame sorts on a 20-50 MP frame
        // would be 1-3 s and a full clone; the sample is a few thousand entries and a sub-ms sort. MAD is the
        // median of |x - median|; ×1.4826 makes it a consistent σ estimator for Gaussian noise while ignoring
        // the star outliers. Frames at/under the target sample in full (step 1).
        private const int BackgroundSampleTarget = 20000;

        private static (double median, double sigma) BackgroundStats(ReadOnlySpan<ushort> pixels, int width, int height) {
            // Sample on a 2D grid (equal x/y step), NOT a linear memory stride. A linear stride i += N through
            // row-major pixels aliases onto a fixed set of columns whenever gcd(N, width) > 1 (e.g. any
            // power-of-two width) — it then samples only every gcd-th column, so a vignette or any column-varying
            // structure biases the median/MAD and thus the detection threshold. An isotropic grid step of
            // √(length/target) spreads the samples evenly over the whole field. Frames at/under the target give
            // step 1 (√(≤1) → 0 → floored to 1) and sample in full.
            int step = Math.Max(1, (int)Math.Sqrt((double)pixels.Length / BackgroundSampleTarget));
            int nx = (width + step - 1) / step;   // ceil(width/step): x positions 0, step, 2·step, … < width
            int ny = (height + step - 1) / step;  // ceil(height/step): likewise for rows
            var sample = new ushort[nx * ny];
            int j = 0;
            for (int y = 0; y < height; y += step) {
                int rowBase = y * width;
                for (int x = 0; x < width; x += step) {
                    sample[j++] = pixels[rowBase + x];
                }
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

            var (outerDiameter, innerDiameter, shadowDepth, centroidOffsetX, centroidOffsetY, radialSkew) =
                DonutGeometry(blob, pixels, width, cx, cy, background);

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
                DonutOuterDiameter = outerDiameter,
                DonutInnerDiameter = innerDiameter,
                DonutShadowDepth = shadowDepth,
                DonutCentroidOffsetX = centroidOffsetX,
                DonutCentroidOffsetY = centroidOffsetY,
                RadialProfileSkew = radialSkew,
            };
        }

        // §59.3/§59.10 — donut (annulus) geometry for obstructed scopes. A defocused star on a scope with a
        // central obstruction images as a ring: bright at some radius, dark in the middle (the obstruction
        // shadow). Build the radial surface-brightness profile of the blob about its centroid — mean
        // background-subtracted flux per integer-radius bin — and read the ring's half-max edges: the outer
        // edge is the full donut diameter, the inner edge the obstruction-shadow diameter. Because the blob
        // holds only above-threshold pixels, a donut's dark hole is absent (count-0 inner bins the scan skips),
        // so the inner edge lands on the ring's inner rim. A refractor / in-focus star peaks at the centre, so
        // its inner edge is bin 0 (inner diameter 0) and this degrades continuously to "outer ≈ FWHM, inner 0"
        // — no branch on scope type. Diameters are 2·(bin radius) in pixels; a sub-pixel blob yields (0, 0, 0).
        //
        // §59.4 also wants the obstruction-shadow DEPTH (how dark the hole is relative to the ring), which
        // distinguishes a well-obstructed scope / heavier defocus from a shallow one. The hole's pixels are
        // below the detection threshold, so they're absent from the blob — its brightness is sampled directly
        // from the frame over the inner-rim disk: ShadowDepth = clamp((ringPeak − holeMean) / ringPeak, 0, 1),
        // 1 for a background-dark hole, →0 as the hole fills in (and exactly 0 for a filled star with no hole).
        //
        // §59.10 collimation: over the OUTER disk, take the brightness-DEFICIT-weighted centroid of the dark
        // (sub-half-max) pixels — the obstruction shadow — each weight clamped to (0, peakSb] so one dead/cold
        // pixel can't dominate, and return its offset from the ring's flux centroid (cx, cy). Scanning the full
        // outer disk (not just the inner-rim disk) keeps the shadow locatable once it has decentered off the flux
        // centroid — the collimation case. A concentric shadow → offset ≈ 0; a decentered secondary → the offset
        // points toward the displacement. 0 for a star with no hole. Raw image-frame pixels; the verdict slice
        // vector-averages many stars and maps direction to a clock position.
        private const double DonutRingHalfMaxFraction = 0.5;
        private const int DonutProfileMaxStackBins = 96;
        // A real central-obstruction shadow spans many pixels; an inner rim of only a pixel or two is centre-bin
        // noise (bin 0 averages very few pixels), so it's floored to a filled centre (inner 0).
        private const int DonutMinInnerRadiusBins = 2;

        private static (double OuterDiameter, double InnerDiameter, double ShadowDepth,
                double CentroidOffsetX, double CentroidOffsetY, double RadialProfileSkew) DonutGeometry(
                List<int> blob, ReadOnlySpan<ushort> pixels, int width, double cx, double cy, double background) {
            double maxR = 0;
            foreach (int p in blob) {
                int x = p % width, y = p / width;
                double dx = x - cx, dy = y - cy;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > maxR) {
                    maxR = r;
                }
            }
            int bins = (int)maxR + 1;
            // Stack the profile for the common small blob; spill to the heap only for a large defocused donut.
            Span<double> flux = bins <= DonutProfileMaxStackBins ? stackalloc double[bins] : new double[bins];
            Span<int> count = bins <= DonutProfileMaxStackBins ? stackalloc int[bins] : new int[bins];
            foreach (int p in blob) {
                int x = p % width, y = p / width;
                double dx = x - cx, dy = y - cy;
                int b = (int)Math.Sqrt(dx * dx + dy * dy);
                if (b >= bins) {
                    b = bins - 1;
                }
                flux[b] += pixels[p] - background;
                count[b]++;
            }
            double peakSb = 0;
            int peakBin = 0;
            for (int b = 0; b < bins; b++) {
                if (count[b] == 0) {
                    continue;
                }
                double sb = flux[b] / count[b];
                if (sb > peakSb) {
                    peakSb = sb;
                    peakBin = b;
                }
            }
            if (peakSb <= 0) {
                return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            }

            // §59.3 — the radial-profile SKEW: the third standardized moment of the blob's radial flux
            // distribution (per-bin TOTAL flux, so outer bins weigh by their real light share). Skew reads
            // the TAIL direction: positive = mass concentrated inward with a soft outward halo (a long
            // right tail), negative = flux packed against a hard bright outer shell (tail pointing inward).
            // For a rig with spherical aberration this signature flips sign across focus — the
            // intra/extra-focal asymmetry the §59.3 side-classifier learns from the calibration sweep's
            // labelled arms. The SIGN CONVENTION is rig-specific (depends on the SA sign), so nothing here
            // assumes which side is which; the classifier learns it. The detection threshold truncates
            // faint wings, but the truncation is identical for every star of a frame and consistent across
            // a rig's frames, so the learned separation survives it.
            double radialSkew = 0.0;
            {
                double w = 0, sumB = 0;
                for (int b = 0; b < bins; b++) {
                    double f = Math.Max(0.0, flux[b]);
                    w += f;
                    sumB += f * b;
                }
                if (w > 0) {
                    double meanR = sumB / w;
                    double m2 = 0, m3 = 0;
                    for (int b = 0; b < bins; b++) {
                        double f = Math.Max(0.0, flux[b]);
                        double d = b - meanR;
                        m2 += f * d * d;
                        m3 += f * d * d * d;
                    }
                    m2 /= w;
                    m3 /= w;
                    // A sub-pixel-spread profile has no meaningful shape; the floor also guards the σ³ division.
                    if (m2 > 1e-6) {
                        radialSkew = m3 / Math.Pow(m2, 1.5);
                    }
                }
            }
            double half = DonutRingHalfMaxFraction * peakSb;
            // Walk the ring OUT from the peak, contiguously: the first bin that dips below half-max (or is
            // empty) ends it, so a lone noisy outlier bin past a gap can't stretch the outer diameter.
            int outerBin = peakBin;
            for (int b = peakBin + 1; b < bins; b++) {
                if (count[b] == 0 || flux[b] / count[b] < half) {
                    break;
                }
                outerBin = b;
            }
            // Walk the ring IN from the peak, contiguously: the first sub-half / empty bin is the inner rim
            // (the obstruction-shadow edge). For a centre-peaked star the ring reaches bin 0, so the rim is 0.
            int innerBin = peakBin;
            for (int b = peakBin - 1; b >= 0; b--) {
                if (count[b] == 0 || flux[b] / count[b] < half) {
                    break;
                }
                innerBin = b;
            }
            // A hole only a pixel or two across is not a physical central-obstruction shadow — it's centre-bin
            // noise (on a real frame bin 0's few-pixel mean can dip below half-max even for a filled star, and
            // noise can nudge the peak off bin 0). Floor a sub-threshold inner rim to 0 so an in-focus /
            // refractor star robustly reports inner diameter 0 with no branch on scope type; a genuine defocused
            // donut's obstruction shadow spans many pixels and clears the floor.
            if (innerBin < DonutMinInnerRadiusBins) {
                innerBin = 0;
            }

            // Shadow depth + §59.10 centroid, in ONE pass over the outer disk (the inner-rim disk the shadow
            // depth needs is contained in it, so a second scan would just re-read the same pixels). Only a
            // genuine hole (innerBin > 0 after the floor) has either.
            double shadowDepth = 0.0;
            double offsetX = 0.0, offsetY = 0.0;
            if (innerBin > 0) {
                int height = pixels.Length / width;
                double innerR = innerBin, outerR = outerBin;
                double innerR2 = innerR * innerR, outerR2 = outerR * outerR; // hoisted out of the pixel loop
                double holeSum = 0;               // Σ flux over the inner-rim disk → shadow depth
                int holeCount = 0;
                double wSum = 0, wX = 0, wY = 0;   // deficit-weighted centroid over the outer disk's dark pixels
                int ox0 = Math.Max(0, (int)(cx - outerR)), ox1 = Math.Min(width - 1, (int)(cx + outerR));
                int oy0 = Math.Max(0, (int)(cy - outerR)), oy1 = Math.Min(height - 1, (int)(cy + outerR));
                for (int yy = oy0; yy <= oy1; yy++) {
                    for (int xx = ox0; xx <= ox1; xx++) {
                        double dx = xx - cx, dy = yy - cy;
                        double r2 = (dx * dx) + (dy * dy);
                        if (r2 >= outerR2) {
                            continue;
                        }
                        double localFlux = pixels[(yy * width) + xx] - background;
                        // Shadow depth: mean brightness over the inner-rim disk (sampled straight from the frame —
                        // the hole's pixels are below the detection threshold, so absent from the blob).
                        if (r2 < innerR2) {
                            holeSum += localFlux;
                            holeCount++;
                        }
                        // §59.10 decentering: deficit-weight the DARK (sub-half-max) pixels — the obstruction
                        // shadow plus the gap just inside the rim. Bright ring pixels (flux ≥ half-max) are
                        // excluded so the ring can't wash out the signal. Clamp the flux to ≥ 0 before weighting
                        // (as the shadow-depth mean does): a dead/cold pixel's large-negative flux would otherwise
                        // carry an outsized weight and let one outlier dominate the centroid, defeating the
                        // uncorrelated-per-star-noise-cancels assumption the verdict relies on. Clamped, the
                        // darkest a pixel can weigh is peakSb, same as a background-dark shadow pixel.
                        if (localFlux < half) {
                            double w = peakSb - Math.Max(0.0, localFlux); // ∈ (0, peakSb]
                            wSum += w;
                            wX += w * xx;
                            wY += w * yy;
                        }
                    }
                }
                if (holeCount > 0) {
                    double holeMean = Math.Max(0.0, holeSum / holeCount);
                    shadowDepth = Math.Clamp((peakSb - holeMean) / peakSb, 0.0, 1.0);
                }
                if (wSum > 0) {
                    // Offset from the ring's FLUX centroid (cx, cy) — not the geometric ring centre. For a
                    // symmetric ring they coincide, but an intrinsically asymmetric ring (coma / tilt / heavy
                    // elongation) pulls (cx, cy) off centre and can read a nonzero offset even on a well-collimated
                    // star. That per-star false signal is why the §59.10 verdict vector-averages many near-centre
                    // stars: uncorrelated ring asymmetry cancels, a real secondary decentering adds coherently.
                    offsetX = (wX / wSum) - cx;
                    offsetY = (wY / wSum) - cy;
                }
            }
            return (2.0 * outerBin, 2.0 * innerBin, shadowDepth, offsetX, offsetY, radialSkew);
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
