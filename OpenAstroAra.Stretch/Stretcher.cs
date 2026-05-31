#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Stretch;

/// <summary>§65.1 stretch palette. Lowercase wire values match the API contract.</summary>
public enum StretchAlgorithm {
    AutoStf,      // wire: "auto_stf"
    Linear,       // wire: "linear"
    Log,          // wire: "log"
    Asinh,        // wire: "asinh"
    Sqrt,         // wire: "sqrt"
    Equalized,    // wire: "equalized"
    Manual,       // wire: "manual"
}

/// <summary>
/// §65 stretch parameters. Not every field applies to every algorithm —
/// see <see cref="Stretcher.Apply"/> for the per-algorithm contract.
/// </summary>
public sealed record StretchParams(
    double Blackpoint = 0.0,
    double Midpoint = 0.5,
    double Whitepoint = 1.0,
    double Beta = 3.0,
    double LinearClipLow = 0.005,
    double LinearClipHigh = 0.995);

/// <summary>
/// Pure-math implementations of the §65.1 stretch palette. Each algorithm
/// maps a <c>ReadOnlySpan&lt;ushort&gt;</c> (raw FITS pixels, 0–65535)
/// to a <c>byte[]</c> (display pixels, 0–255). No native dependencies —
/// pure managed code, AOT-safe, allocation-bounded (one output buffer
/// + at most one histogram array per call).
///
/// Output is single-channel grayscale. JPEG encoding is the next layer
/// up; this class only does the photometric transform.
/// </summary>
public static class Stretcher {
    private const int MaxValue = 65535;

    public static byte[] Apply(StretchAlgorithm algorithm, ReadOnlySpan<ushort> input, StretchParams? parameters = null) {
        if (input.Length == 0) return Array.Empty<byte>();
        var p = parameters ?? new StretchParams();
        return algorithm switch {
            StretchAlgorithm.Linear => Linear(input, p),
            StretchAlgorithm.Log => Log(input, p),
            StretchAlgorithm.Asinh => Asinh(input, p),
            StretchAlgorithm.Sqrt => Sqrt(input, p),
            StretchAlgorithm.Equalized => Equalized(input),
            StretchAlgorithm.Manual => Manual(input, p),
            StretchAlgorithm.AutoStf => AutoStf(input),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
        };
    }

    // ── linear ─────────────────────────────────────────────────────────────
    // Black/white-point clip via percentiles; rescale to 0–255. Defaults
    // 0.5% / 99.5% per §65.1.
    private static byte[] Linear(ReadOnlySpan<ushort> input, StretchParams p) {
        var (bp, wp) = Percentiles(input, p.LinearClipLow, p.LinearClipHigh);
        return RescaleClip(input, bp, wp);
    }

    // ── log ────────────────────────────────────────────────────────────────
    // log(x - bp + 1) scaled to 0-255. bp defaults to 5th percentile if
    // not explicitly set via params (Blackpoint == 0).
    private static byte[] Log(ReadOnlySpan<ushort> input, StretchParams p) {
        var bp = p.Blackpoint > 0 ? p.Blackpoint * MaxValue : Percentile(input, 0.05);
        var maxIn = Math.Log(MaxValue - bp + 1);
        if (maxIn <= 0) maxIn = 1;
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            var v = input[i] - bp;
            if (v < 0) v = 0;
            var stretched = Math.Log(v + 1) / maxIn;
            output[i] = ToByte(stretched);
        }
        return output;
    }

    // ── asinh ──────────────────────────────────────────────────────────────
    // Lupton: asinh(beta * (x - bp)) / asinh(beta). Default beta = 3.0.
    private static byte[] Asinh(ReadOnlySpan<ushort> input, StretchParams p) {
        var bp = p.Blackpoint > 0 ? p.Blackpoint * MaxValue : Percentile(input, 0.05);
        var beta = p.Beta > 0 ? p.Beta : 3.0;
        var denom = Math.Asinh(beta);
        if (denom == 0) denom = 1;
        var range = MaxValue - bp;
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            var v = (input[i] - bp) / range;
            if (v < 0) v = 0;
            var stretched = Math.Asinh(beta * v) / denom;
            output[i] = ToByte(stretched);
        }
        return output;
    }

    // ── sqrt ───────────────────────────────────────────────────────────────
    // Gamma 0.5 after black/white-point clip.
    private static byte[] Sqrt(ReadOnlySpan<ushort> input, StretchParams p) {
        var (bp, wp) = Percentiles(input, p.LinearClipLow, p.LinearClipHigh);
        var range = wp - bp;
        if (range <= 0) range = 1;
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            var v = (input[i] - bp) / range;
            if (v < 0) v = 0;
            else if (v > 1) v = 1;
            output[i] = ToByte(Math.Sqrt(v));
        }
        return output;
    }

    // ── equalized ──────────────────────────────────────────────────────────
    // Full histogram equalization. Build cumulative histogram over the
    // input; remap each pixel to its CDF percentile.
    private static byte[] Equalized(ReadOnlySpan<ushort> input) {
        Span<int> hist = stackalloc int[256];
        // Down-sample to 8-bit buckets for the histogram pass. Loses some
        // detail vs. a full 16-bit histogram but keeps allocation bounded.
        for (var i = 0; i < input.Length; i++) {
            hist[input[i] >> 8]++;
        }
        Span<int> cdf = stackalloc int[256];
        cdf[0] = hist[0];
        for (var i = 1; i < 256; i++) cdf[i] = cdf[i - 1] + hist[i];
        // First nonzero index (cdf_min); avoids mapping the darkest bin to 0.
        var cdfMin = 0;
        for (var i = 0; i < 256; i++) {
            if (cdf[i] > 0) { cdfMin = cdf[i]; break; }
        }
        var denom = input.Length - cdfMin;
        if (denom <= 0) denom = 1;
        Span<byte> lut = stackalloc byte[256];
        for (var i = 0; i < 256; i++) {
            var num = cdf[i] - cdfMin;
            if (num < 0) num = 0;
            var scaled = (int)Math.Round(255.0 * num / denom);
            lut[i] = (byte)Math.Clamp(scaled, 0, 255);
        }
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            output[i] = lut[input[i] >> 8];
        }
        return output;
    }

    // ── manual ─────────────────────────────────────────────────────────────
    // User-supplied bp / mp / wp (0–1). Linear remap with midpoint controls
    // the gamma curve: at mp = 0.5 it's straight linear; mp < 0.5 brightens
    // shadows (gamma > 1), mp > 0.5 darkens them (gamma < 1).
    private static byte[] Manual(ReadOnlySpan<ushort> input, StretchParams p) {
        if (p.Whitepoint <= p.Blackpoint) {
            throw new ArgumentException(
                $"manual stretch requires whitepoint > blackpoint (got bp={p.Blackpoint}, wp={p.Whitepoint})");
        }
        var bp = p.Blackpoint * MaxValue;
        var wp = p.Whitepoint * MaxValue;
        var range = wp - bp;
        // Gamma from midpoint: if mp is the input value that should map to
        // 0.5 output (after linear scaling), gamma = log(0.5) / log(mp).
        // Convert mp from 0-1 input-domain to fraction of (bp..wp) range.
        var mpFraction = (p.Midpoint * MaxValue - bp) / range;
        if (mpFraction <= 0) mpFraction = 0.001;
        if (mpFraction >= 1) mpFraction = 0.999;
        var gamma = Math.Log(0.5) / Math.Log(mpFraction);
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            var v = (input[i] - bp) / range;
            if (v < 0) v = 0;
            else if (v > 1) v = 1;
            output[i] = ToByte(Math.Pow(v, gamma));
        }
        return output;
    }

    // ── auto_stf ───────────────────────────────────────────────────────────
    // PixInsight-style STF per §65.1: shadows clip at median − 2.8 × MAD,
    // highlights clip at 99.998 percentile, midtone targets background 0.25.
    // Implementation: compute median + MAD via Quickselect (O(N) avg);
    // derive bp/mp/wp; apply Manual stretch with those params.
    private static byte[] AutoStf(ReadOnlySpan<ushort> input) {
        if (input.Length == 0) return Array.Empty<byte>();

        // Median.
        var values = new ushort[input.Length];
        input.CopyTo(values);
        var median = Quickselect(values, values.Length / 2);

        // MAD = median(|xi - median|). Reuse the buffer for absolute diffs.
        for (var i = 0; i < values.Length; i++) {
            var diff = input[i] - median;
            if (diff < 0) diff = -diff;
            values[i] = (ushort)Math.Min(diff, MaxValue);
        }
        var mad = Quickselect(values, values.Length / 2);

        // §65.1 adopted defaults: shadows = median − 2.8 σ_MAD, where
        // σ_MAD = MAD / 0.6745 (Gaussian-equivalent). Highlights = 99.998
        // percentile via a separate quickselect on the original values.
        const double madToSigma = 1.0 / 0.6745;
        var sigma = mad * madToSigma;
        var bp = Math.Max(0, median - 2.8 * sigma);

        input.CopyTo(values);
        var wpIndex = (int)Math.Floor(0.99998 * (values.Length - 1));
        double wp = Quickselect(values, wpIndex);
        if (wp <= bp) wp = bp + 1;

        // Target background 0.25: midpoint = bp + 0.25 × (median - bp).
        var mp = bp + 0.25 * (median - bp);
        return Manual(input, new StretchParams(
            Blackpoint: bp / MaxValue,
            Midpoint: mp / MaxValue,
            Whitepoint: wp / MaxValue));
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static byte[] RescaleClip(ReadOnlySpan<ushort> input, double bp, double wp) {
        var range = wp - bp;
        if (range <= 0) range = 1;
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) {
            var v = (input[i] - bp) / range;
            if (v < 0) v = 0;
            else if (v > 1) v = 1;
            output[i] = ToByte(v);
        }
        return output;
    }

    private static byte ToByte(double v01) {
        if (double.IsNaN(v01) || v01 <= 0) return 0;
        if (v01 >= 1) return 255;
        return (byte)Math.Round(v01 * 255.0);
    }

    private static (double Bp, double Wp) Percentiles(ReadOnlySpan<ushort> input, double lo, double hi) {
        var values = new ushort[input.Length];
        input.CopyTo(values);
        var loIdx = Math.Clamp((int)Math.Floor(lo * (values.Length - 1)), 0, values.Length - 1);
        var hiIdx = Math.Clamp((int)Math.Floor(hi * (values.Length - 1)), 0, values.Length - 1);
        var bp = (double)Quickselect(values, loIdx);
        // Quickselect rearranges in-place; second call sees the post-partition
        // state. That's fine because we just need a value-at-index, not a
        // fully sorted array — quickselect doesn't break the partial-order
        // invariant the second pass depends on.
        var wp = (double)Quickselect(values, hiIdx);
        return (bp, wp);
    }

    private static double Percentile(ReadOnlySpan<ushort> input, double pct) {
        var values = new ushort[input.Length];
        input.CopyTo(values);
        var idx = Math.Clamp((int)Math.Floor(pct * (values.Length - 1)), 0, values.Length - 1);
        return Quickselect(values, idx);
    }

    /// <summary>
    /// In-place quickselect — find the kth smallest element. O(N) average,
    /// O(N²) worst case (unlikely on natural image data; median-of-three
    /// pivot would harden if we ever observe pathological inputs).
    /// </summary>
    private static ushort Quickselect(ushort[] arr, int k) {
        var left = 0;
        var right = arr.Length - 1;
        while (left < right) {
            var pivot = arr[(left + right) / 2];
            var i = left;
            var j = right;
            while (i <= j) {
                while (arr[i] < pivot) i++;
                while (arr[j] > pivot) j--;
                if (i <= j) {
                    (arr[i], arr[j]) = (arr[j], arr[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) right = j;
            else if (k >= i) left = i;
            else break;
        }
        return arr[k];
    }
}
