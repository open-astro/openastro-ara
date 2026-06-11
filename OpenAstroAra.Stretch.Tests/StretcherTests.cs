#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Stretch;
using Xunit;

namespace OpenAstroAra.Stretch.Tests;

public class StretcherTests {
    /// <summary>
    /// A simple gradient 0..max provides a predictable input for all stretches —
    /// monotonic output expected, with the dimmest pixel ≈ 0 and brightest ≈ 255.
    /// </summary>
    private static ushort[] Gradient(int length, int min = 0, int max = 65535) {
        var arr = new ushort[length];
        for (var i = 0; i < length; i++) {
            arr[i] = (ushort)(min + (long)(max - min) * i / (length - 1));
        }
        return arr;
    }

    [Theory]
    [InlineData(StretchAlgorithm.Linear)]
    [InlineData(StretchAlgorithm.Log)]
    [InlineData(StretchAlgorithm.Asinh)]
    [InlineData(StretchAlgorithm.Sqrt)]
    [InlineData(StretchAlgorithm.Equalized)]
    [InlineData(StretchAlgorithm.AutoStf)]
    public void All_stretches_produce_full_dynamic_range_on_gradient(StretchAlgorithm algorithm) {
        var input = Gradient(1024);
        var output = Stretcher.Apply(algorithm, input);
        Assert.Equal(input.Length, output.Length);
        // First pixel should be very dark, last pixel very bright.
        Assert.True(output[0] <= 8, $"{algorithm}: first pixel = {output[0]}, expected <= 8");
        Assert.True(output[^1] >= 248, $"{algorithm}: last pixel = {output[^1]}, expected >= 248");
    }

    [Theory]
    [InlineData(StretchAlgorithm.Linear)]
    [InlineData(StretchAlgorithm.Log)]
    [InlineData(StretchAlgorithm.Asinh)]
    [InlineData(StretchAlgorithm.Sqrt)]
    public void Monotonic_stretches_preserve_pixel_ordering(StretchAlgorithm algorithm) {
        // Skip Equalized (output flat regions may tie) + AutoStf (depends on
        // input distribution; on a strict gradient most pixels can collapse
        // to 255 by the §65.1 99.998-percentile whitepoint).
        var input = Gradient(1024);
        var output = Stretcher.Apply(algorithm, input);
        for (var i = 1; i < output.Length; i++) {
            Assert.True(output[i] >= output[i - 1],
                $"{algorithm}: monotonicity broken at index {i} (input {input[i - 1]}→{input[i]}, output {output[i - 1]}→{output[i]})");
        }
    }

    [Fact]
    public void Manual_stretch_with_default_midpoint_is_linear() {
        // bp=0, mp=0.5, wp=1.0 — gamma should be exactly 1.0 (linear).
        var input = Gradient(256);
        var output = Stretcher.Apply(StretchAlgorithm.Manual, input,
            new StretchParams(Blackpoint: 0.0, Midpoint: 0.5, Whitepoint: 1.0));
        // Linear input → linear output → byte index ≈ input >> 8.
        for (var i = 0; i < input.Length; i++) {
            var expected = (byte)Math.Round(input[i] / 65535.0 * 255.0);
            Assert.InRange((int)output[i], expected - 1, expected + 1);
        }
    }

    [Fact]
    public void AutoStf_maps_the_median_near_quarter_brightness() {
        // A background-dominated frame: ~10k pixels around a 12000 median (8000/12000 split → MAD), plus
        // a few bright "stars" at 60000 for the 99.998-percentile whitepoint. PixInsight STF targets the
        // background (median) at ~0.25 output ≈ 64/255 — NOT ~0.65 (≈166), which the old linear-midpoint
        // formula produced. This is the test the gradient case can't catch (its median maps to 0.25 by
        // coincidence under both formulas).
        var pixels = new ushort[10000];
        for (var i = 0; i < 9980; i++) pixels[i] = (ushort)(i % 2 == 0 ? 8000 : 12000);
        for (var i = 9980; i < 10000; i++) pixels[i] = 60000;

        var output = Stretcher.Apply(StretchAlgorithm.AutoStf, pixels);

        // pixels[1] = 12000 (the median value) → should land near quarter brightness.
        Assert.InRange((int)output[1], 45, 90);
    }

    [Theory]
    [InlineData(0.5, 110, 145)]  // brighter background target → median near mid-grey
    [InlineData(0.1, 15, 40)]    // dimmer target → median near 0.1·255 ≈ 26
    public void Stf_lands_the_median_near_the_requested_target_background(double target, int lo, int hi) {
        // Median 12000 (8000/12000 split → MAD), a few bright "stars" at 60000 for the whitepoint.
        var pixels = new ushort[10000];
        for (var i = 0; i < 9980; i++) pixels[i] = (ushort)(i % 2 == 0 ? 8000 : 12000);
        for (var i = 9980; i < 10000; i++) pixels[i] = 60000;

        var output = Stretcher.Stf(pixels, targetBackground: target, shadowSigma: 2.8);

        // pixels[1] = 12000 (the median value) → lands near target·255.
        Assert.InRange((int)output[1], lo, hi);
    }

    [Fact]
    public void Manual_stretch_rejects_inverted_endpoints() {
        var ex = Assert.Throws<ArgumentException>(() =>
            Stretcher.Apply(StretchAlgorithm.Manual, new ushort[] { 0, 1 },
                new StretchParams(Blackpoint: 0.8, Midpoint: 0.5, Whitepoint: 0.2)));
        Assert.Contains("whitepoint > blackpoint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equalized_stretch_spreads_clustered_input() {
        // Strongly clustered input: 80% in low band, 20% in high band.
        // Equalized output should redistribute these toward 0 and 255.
        var input = new ushort[1000];
        for (var i = 0; i < 800; i++) input[i] = 100;
        for (var i = 800; i < 1000; i++) input[i] = 60000;
        var output = Stretcher.Apply(StretchAlgorithm.Equalized, input);
        // The 60000 inputs should map well above the 100 inputs (separation).
        Assert.True(output[999] - output[0] >= 200,
            $"Equalized: separation = {output[999] - output[0]}, expected >= 200");
    }

    [Fact]
    public void Empty_input_returns_empty_output() {
        Assert.Empty(Stretcher.Apply(StretchAlgorithm.Linear, ReadOnlySpan<ushort>.Empty));
        Assert.Empty(Stretcher.Apply(StretchAlgorithm.AutoStf, ReadOnlySpan<ushort>.Empty));
    }
}