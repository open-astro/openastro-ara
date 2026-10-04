#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A development-only star field for exercising the focus instruments without a sky. The Alpaca camera
/// simulator renders noise, so neither the autofocus sweep nor the guide-camera focus loop can be seen
/// working against it; with <c>OPENASTROARA_SYNTHETIC_SKY</c> set (Development only) the daemon swaps in
/// frames rendered here instead: the SAME detector, fit, confirmation frame and rendering run on them,
/// only the photons are fake. Never registered in a packaged daemon.
///
/// <c>OPENASTROARA_SYNTHETIC_SKY=best=24500,hfr=1.4,scale=400</c> — best-focus focuser position, the HFR
/// at best focus (px) and how many focuser steps add one pixel of HFR (defocus grows with distance from
/// best like a V with a rounded bottom). All three optional.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness",
    Justification = "A seeded, reproducible star field for a development fixture; nothing here is security-relevant.")]
public static class SyntheticSky {

    public const string EnvVar = "OPENASTROARA_SYNTHETIC_SKY";

    /// <summary>Parse the env var's <c>key=value,key=value</c> list; unknown keys ignored. Pure.</summary>
    public static SyntheticSkySettings Parse(string? spec) {
        var o = new SyntheticSkySettings();
        if (string.IsNullOrWhiteSpace(spec)) {
            return o;
        }
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) {
                continue;
            }
            var value = kv[1].Trim();
            switch (kv[0].Trim().ToLowerInvariant()) {
                case "best" when int.TryParse(value, out var b): o = o with { BestPosition = b }; break;
                case "hfr" when double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0: o = o with { HfrAtFocus = h }; break;
                case "scale" when double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0: o = o with { StepsPerPixel = s }; break;
            }
        }
        return o;
    }

    /// <summary>The HFR a probe at <paramref name="position"/> measures: a hyperbola with its minimum at
    /// best focus (the shape the §59.8 fit expects). Pure.</summary>
    public static double HfrAt(int position, SyntheticSkySettings o) {
        var defocus = Math.Abs(position - o.BestPosition) / o.StepsPerPixel;
        return Math.Sqrt(o.HfrAtFocus * o.HfrAtFocus + defocus * defocus);
    }

    /// <summary>
    /// Render a <paramref name="width"/>×<paramref name="height"/> frame of Gaussian stars at the given
    /// HFR (half-flux radius ≈ 1.177 σ for a Gaussian) over a noisy sky background. Star positions and
    /// brightnesses are fixed by <paramref name="seed"/> so a sweep sees the same field at every probe;
    /// the noise differs per frame. Flux is conserved as the stars blur, so a defocused star is dimmer and
    /// wider — the detector loses the faint ones first, as a real sky does.
    /// </summary>
    public static ushort[] Render(int width, int height, double hfr, int seed = 7, int stars = 70, int frameSeed = 0) {
        var frame = new ushort[width * height];
        const ushort background = 1200;
        var noise = new Random(unchecked(seed * 31 + frameSeed));
        for (var i = 0; i < frame.Length; i++) {
            frame[i] = (ushort)(background + noise.Next(-40, 41));
        }
        var sigma = Math.Max(0.6, hfr / 1.177);
        var field = new Random(seed);
        var margin = 16;
        for (var s = 0; s < stars; s++) {
            var cx = field.Next(margin, width - margin);
            var cy = field.Next(margin, height - margin);
            // Flux spread over the Gaussian: peak = flux / (2πσ²). Bright stars reach ~40k ADU in focus.
            var flux = 2_000.0 * Math.Pow(10, field.NextDouble() * 1.6); // 2k…80k e-
            var peak = flux / (2 * Math.PI * sigma * sigma);
            var r = (int)Math.Ceiling(sigma * 4);
            for (var dy = -r; dy <= r; dy++) {
                var y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (var dx = -r; dx <= r; dx++) {
                    var x = cx + dx;
                    if (x < 0 || x >= width) continue;
                    var v = peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
                    var idx = y * width + x;
                    frame[idx] = (ushort)Math.Min(ushort.MaxValue, frame[idx] + v);
                }
            }
        }
        return frame;
    }
}

/// <summary>Settings for <see cref="SyntheticSky"/>: best-focus focuser position, the HFR there and how many
/// focuser steps add one pixel of HFR.</summary>
public sealed record SyntheticSkySettings(int BestPosition = 24_500, double HfrAtFocus = 1.4, double StepsPerPixel = 400.0);

/// <summary>The autofocus probe source for the synthetic sky: blur follows the (simulator) focuser's
/// distance from the configured best position; the exposure is honoured up to one second so a sweep
/// feels like a sweep without taking minutes.</summary>
public sealed partial class SyntheticSkyFrameSource : IAnalysisFrameSource {
    private readonly IFocuserMediator _focuser;
    private readonly SyntheticSkySettings _options;
    private readonly ILogger _logger;
    private int _frame;

    public const int Width = 800;
    public const int Height = 600;

    public SyntheticSkyFrameSource(IFocuserMediator focuser, SyntheticSkySettings options, ILogger? logger = null) {
        _focuser = focuser ?? throw new ArgumentNullException(nameof(focuser));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
        var info = _focuser.GetInfo();
        if (info is not { Connected: true }) {
            throw new InvalidOperationException("synthetic sky: the focuser is not connected");
        }
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(exposureSec, 0.05, 1.0)), ct).ConfigureAwait(false);
        var hfr = SyntheticSky.HfrAt(info.Position, _options);
        var pixels = SyntheticSky.Render(Width, Height, hfr, frameSeed: Interlocked.Increment(ref _frame));
        LogProbe(_logger, info.Position, hfr);
        return new AnalysisFrame(pixels, Width, Height, DateTimeOffset.UtcNow);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Synthetic sky: probe at {Position} rendered at HFR {Hfr:0.00}")]
    private static partial void LogProbe(ILogger logger, int position, double hfr);
}

/// <summary>Guide-camera frames for the synthetic sky: the field's focus drifts through best focus and
/// back on a slow cycle, as if someone were turning a helical focuser past the sweet spot — so the HFR
/// readout, the trend and the best-so-far line all move.</summary>
public sealed class SyntheticGuideFrames {
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private int _frame;
    public const int Width = 640;
    public const int Height = 480;

    public double HfrAt(TimeSpan elapsed) =>
        1.6 + 2.0 * Math.Abs(Math.Sin(elapsed.TotalSeconds / 90.0 * Math.PI));

    public (ushort[] Pixels, int Width, int Height) Next() {
        var hfr = HfrAt(DateTimeOffset.UtcNow - _started);
        return (SyntheticSky.Render(Width, Height, hfr, seed: 11, stars: 25, frameSeed: Interlocked.Increment(ref _frame)), Width, Height);
    }
}

/// <summary>
/// Development only (SyntheticSky): the by-hand rotation readout's solver without a sky. The "camera
/// angle" is read from <c>synthetic-position-angle</c> in the profile directory on every solve (one
/// number, degrees); editing the file stands in for turning the camera. Missing or unreadable → the
/// solve "fails", which exercises the readout's failure path too.
/// </summary>
public sealed class SyntheticPositionAngleSolver : IPositionAngleSolver {
    public const string FileName = "synthetic-position-angle";
    private readonly string _path;

    public SyntheticPositionAngleSolver(string profileDir) {
        _path = System.IO.Path.Combine(profileDir, FileName);
    }

    private int _frameSeed;

    // A dev camera that bins to 4×4, so the auto-binned loop and the 1×1 confirmation both exercise.
    public int MaxBinning => 4;

    public async Task<RotationSolve?> SolvePositionAngleAsync(double exposureSeconds, int binning, CancellationToken ct) {
        // The "exposure" plus a moment for the solve — longer at 1×1, as a full frame is on a real rig —
        // so the panel's cadence feels like the real thing.
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(exposureSeconds, 10) + (binning <= 1 ? 2.5 : 0.8)), ct).ConfigureAwait(false);
        string text;
        try {
            text = (await System.IO.File.ReadAllTextAsync(_path, ct).ConfigureAwait(false)).Trim();
        } catch (System.IO.IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pa)) {
            return null;
        }
        // A rendered star field stands in for the capture (new noise each solve so the picture visibly updates).
        // Binned frames are smaller, as the camera's would be (the pixel scale grows to match).
        var b = Math.Clamp(binning, 1, 4);
        int width = 1024 / b, height = 683 / b;
        var pixels = SyntheticSky.Render(width, height, hfr: Math.Max(0.9, 1.6 / b), seed: 11, stars: 140, frameSeed: ++_frameSeed);
        var frame = new AnalysisFrame(pixels, width, height, DateTimeOffset.UtcNow);
        // A pixel scale that gives the rendered 1024 px frame a RedCat-sized field (≈2.6° × 1.7°), so the
        // scope box on the planetarium is the size a real frame's would be.
        return new RotationSolve(pa, RaDeg: 314.82, DecDeg: 44.53, PixelScaleArcsec: 9.0 * b, Flipped: false, frame);
    }
}
