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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A flat frame's brightness as calibration cares about it: the median of the central half of the
/// frame (vignetting darkens the corners, hot pixels drag a mean up). On a colour sensor each of the
/// four Bayer positions is measured on its own and the brightest wins, so green never clips while
/// the average looks fine. <c>Brightest</c> is the hottest pixel in that area, for the range check;
/// <c>BitsSet</c> is every bit any of its pixels had, for <see cref="LooksScaledAbove"/>.
/// </summary>
internal readonly record struct FlatLevel(double Level, int Brightest, int BitsSet = 0) {

    /// <summary>
    /// True when every pixel sits on a coarser grid than <paramref name="fullScale"/> implies: a
    /// 12-bit camera reporting MaxADU 4095 whose driver delivers data scaled to 16 bits has every
    /// value a multiple of 16. A dim probe from such a camera never exceeds 4095, so the range
    /// check alone would accept it, and the flats would come out at a sixteenth of the real range.
    /// Native data at that depth always has noise in its low bits. Null when the frame has no
    /// signal at all (every pixel 0): it can't tell either way (#1438).
    /// </summary>
    internal bool? LooksScaledAbove(int fullScale) {
        if (fullScale is <= 0 or >= ushort.MaxValue) {
            return false;
        }
        if (BitsSet == 0) {
            return null;
        }
        var bits = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)fullScale);
        var lowBits = (1 << Math.Max(0, 16 - bits)) - 1;
        return lowBits != 0 && (BitsSet & lowBits) == 0;
    }

    internal static FlatLevel Measure(ReadOnlySpan<ushort> pixels, int width, int height, string? bayerPattern) {
        if (width <= 0 || height <= 0 || pixels.Length < width * height) {
            return new FlatLevel(0, 0);
        }
        // Central half in each axis; tiny frames (tests, thumbnails) use the whole frame. Even
        // origins keep the Bayer phase of (x0, y0) equal to the pattern's.
        var x0 = width >= 8 ? (width / 4) & ~1 : 0;
        var y0 = height >= 8 ? (height / 4) & ~1 : 0;
        var x1 = width >= 8 ? width - x0 : width;
        var y1 = height >= 8 ? height - y0 : height;
        var histogram = new int[ushort.MaxValue + 1];
        var brightest = 0;
        var bitsSet = 0;
        if (string.IsNullOrEmpty(bayerPattern)) {
            var level = Median(pixels, width, x0, y0, x1, y1, 1, histogram, ref brightest, ref bitsSet);
            return new FlatLevel(level, brightest, bitsSet);
        }
        var max = 0.0;
        for (var dy = 0; dy < 2; dy++) {
            for (var dx = 0; dx < 2; dx++) {
                Array.Clear(histogram);
                max = Math.Max(max, Median(pixels, width, x0 + dx, y0 + dy, x1, y1, 2, histogram, ref brightest, ref bitsSet));
            }
        }
        return new FlatLevel(max, brightest, bitsSet);
    }

    private static double Median(ReadOnlySpan<ushort> pixels, int width, int x0, int y0, int x1, int y1, int step,
            int[] histogram, ref int brightest, ref int bitsSet) {
        long count = 0;
        for (var y = y0; y < y1; y += step) {
            var row = y * width;
            for (var x = x0; x < x1; x += step) {
                var v = pixels[row + x];
                histogram[v]++;
                bitsSet |= v;
                if (v > brightest) {
                    brightest = v;
                }
                count++;
            }
        }
        if (count == 0) {
            return 0;
        }
        var half = (count + 1) / 2;
        long seen = 0;
        for (var v = 0; v < histogram.Length; v++) {
            seen += histogram[v];
            if (seen >= half) {
                return v;
            }
        }
        return 0;
    }
}

/// <summary>The bounds one flat solve works inside. <c>MaxBrightness</c> 0 = the panel brightness
/// cannot be set (manual panel, no panel), so only the exposure moves.</summary>
internal sealed record FlatSolveLimits(
    double TargetLevel,
    double ToleranceLevel,
    double MinExposureSec,
    double MaxExposureSec,
    double SaturationLevel,
    int MinBrightness = 1,
    int MaxBrightness = 0,
    double PreferredMinExposureSec = FlatExposureSolver.PreferredMinExposureSec);

/// <summary>Where the solve landed; <c>Exposure</c> is null when it failed, with the reason.</summary>
internal sealed record FlatSolveResult(double? ExposureSec, int? Brightness, double Level, int Probes, string? Failure);

/// <summary>Expose one probe at <paramref name="exposureSec"/> with the panel at
/// <paramref name="brightness"/> (null = leave it) and return the frame's <see cref="FlatLevel"/>.</summary>
internal delegate Task<double> FlatProbe(double exposureSec, int? brightness, CancellationToken ct);

/// <summary>
/// Solves the flat exposure instead of searching for it. A sensor answers light in a straight
/// line: <c>level = pedestal + slope × exposure</c>, where the pedestal is the camera offset (the
/// level with no light) and the slope is how fast this filter fills at this panel brightness. Two
/// probes at different exposures give both, without a bias frame; the exposure for the target is
/// then <c>(target − pedestal) / slope</c>, and one check frame confirms it. The slope scales with
/// panel brightness, so when the solved exposure is too short (LED flicker, rolling-shutter
/// gradients below a second) or longer than allowed, the brightness is solved instead and the
/// exposure follows. Every further frame re-reads the slope with the pedestal known, which absorbs
/// a panel whose brightness steps aren't quite linear.
/// </summary>
internal static class FlatExposureSolver {

    /// <summary>Probe frames one solve may take, the two-point fit and its checks included.</summary>
    internal const int MaxProbes = 8;

    /// <summary>Shortest exposure that's still a good flat: below it LED panels flicker and a
    /// rolling shutter leaves a gradient. The panel is dimmed to stay above it when it can be.</summary>
    internal const double PreferredMinExposureSec = 1.0;

    internal static async Task<FlatSolveResult> SolveAsync(
            FlatSolveLimits limits, double startExposureSec, int? startBrightness, FlatProbe probe, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(probe);
        var canAdjust = limits.MaxBrightness > 0 && startBrightness is not null;
        int? brightness = canAdjust ? Math.Clamp(startBrightness!.Value, limits.MinBrightness, limits.MaxBrightness) : startBrightness;
        var probes = 0;
        var level = 0.0;

        double Clamp(double t) => Math.Clamp(t, limits.MinExposureSec, limits.MaxExposureSec);
        bool OnTarget(double l) => Math.Abs(l - limits.TargetLevel) <= limits.ToleranceLevel;
        bool Saturated(double l) => l >= limits.SaturationLevel;
        FlatSolveResult Done(double t) => new(t, brightness, level, probes, null);
        FlatSolveResult Fail(string why) => new(null, brightness, level, probes, why);
        async Task<double> Measure(double t) {
            ct.ThrowIfCancellationRequested();
            probes++;
            level = await probe(t, brightness, ct).ConfigureAwait(false);
            return level;
        }
        // Brightness that gives `slope × factor`; false when it can't move (at its limit).
        bool ScaleBrightness(double factor, ref double slope) {
            if (!canAdjust) {
                return false;
            }
            var current = brightness!.Value;
            var wanted = factor > 1 ? Math.Ceiling(current * factor) : Math.Floor(current * factor);
            var next = (int)Math.Clamp(wanted, limits.MinBrightness, limits.MaxBrightness);
            if (next == current) {
                return false;
            }
            slope *= (double)next / current;
            brightness = next;
            return true;
        }

        // 1. First point, backed out of saturation (a clipped frame says nothing about the slope).
        var t1 = Clamp(startExposureSec);
        var l1 = await Measure(t1).ConfigureAwait(false);
        while (Saturated(l1)) {
            if (probes >= MaxProbes) {
                return Fail("every probe saturated");
            }
            if (t1 > limits.MinExposureSec) {
                t1 = Clamp(t1 / 8);
            } else {
                var unused = 1.0;
                if (!ScaleBrightness(0.25, ref unused)) {
                    return Fail("too bright even at the shortest exposure and the dimmest panel");
                }
            }
            l1 = await Measure(t1).ConfigureAwait(false);
        }
        if (OnTarget(l1)) {
            return Done(t1);
        }

        // 2. Second point at the same brightness, on the target's side of the first.
        var t2 = Clamp(l1 < limits.TargetLevel ? t1 * 2 : t1 / 2);
        if (t2 == t1) {
            t2 = Clamp(l1 < limits.TargetLevel ? t1 / 2 : t1 * 2); // pinned at a bound: step the other way
        }
        if (t2 == t1) {
            return Fail("the exposure bounds leave no room to measure");
        }
        var l2 = await Measure(t2).ConfigureAwait(false);
        while (Saturated(l2)) {
            if (probes >= MaxProbes) {
                return Fail("probes kept saturating");
            }
            t2 = t1 + ((t2 - t1) / 2);
            l2 = await Measure(t2).ConfigureAwait(false);
        }
        if (OnTarget(l2)) {
            return Done(t2);
        }
        var slope = (l2 - l1) / (t2 - t1);
        if (!(slope > 0)) {
            return Fail("the level does not rise with exposure — no light is reaching the sensor");
        }
        var pedestal = Math.Clamp(l1 - (slope * t1), 0, Math.Min(l1, l2));
        var signal = limits.TargetLevel - pedestal;
        if (!(signal > 0)) {
            return Fail("the target sits below the camera's offset level");
        }

        // 3. Solve. Outside the usable window, solve the brightness for a 2 s (too short) or
        //    half-maximum (too long) exposure instead, then let the exposure follow it.
        var solved = signal / slope;
        if (solved < limits.PreferredMinExposureSec) {
            var goal = Math.Min(limits.MaxExposureSec, 2 * limits.PreferredMinExposureSec);
            ScaleBrightness(signal / (slope * goal), ref slope);
        } else if (solved > limits.MaxExposureSec) {
            var goal = Math.Max(limits.PreferredMinExposureSec, limits.MaxExposureSec / 2);
            ScaleBrightness(signal / (slope * goal), ref slope);
        }
        var t = Clamp(signal / slope);

        // 4. Check, re-reading the slope with the pedestal known.
        while (probes < MaxProbes) {
            var l = await Measure(t).ConfigureAwait(false);
            if (OnTarget(l)) {
                return Done(t);
            }
            if (Saturated(l)) {
                var shorter = Clamp(t / 2);
                if (shorter == t) {
                    var unused = 1.0;
                    if (!ScaleBrightness(0.5, ref unused)) {
                        return Fail("too bright even at the shortest exposure and the dimmest panel");
                    }
                }
                t = shorter;
                continue;
            }
            slope = (l - pedestal) / t;
            if (!(slope > 0)) {
                return Fail("the level fell to the offset — the panel went dark");
            }
            var next = Clamp(signal / slope);
            if (Math.Abs(next - t) < 1e-9) {
                // Pinned at an exposure bound: only the panel can still close the gap.
                if (!ScaleBrightness(signal / (slope * t), ref slope)) {
                    return Fail(l < limits.TargetLevel
                        ? $"too dim even at {t:0.###} s and the brightest panel setting"
                        : $"too bright even at {t:0.###} s and the dimmest panel setting");
                }
                next = Clamp(signal / slope);
            }
            t = next;
        }
        return Fail($"no convergence in {MaxProbes} probes");
    }
}
