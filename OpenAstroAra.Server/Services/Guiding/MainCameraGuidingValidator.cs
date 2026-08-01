#region "copyright"

/* Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors. */

#endregion

using OpenAstroAra.Image.ImageAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Guiding;

/// <summary>
/// Captures a small, bounded set of main-camera frames and measures star elongation.
/// The validator never changes camera settings. The camera service owns its capture gate.
/// </summary>
public sealed class MainCameraGuidingValidator {
    private readonly IAnalysisFrameSource _frames;

    public MainCameraGuidingValidator(IAnalysisFrameSource frames) =>
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));

    public async Task<double> CaptureMedianEccentricityAsync(
        double exposureSeconds, int binning, int frameCount, CancellationToken ct) {
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(binning, 1);

        var values = new List<double>(Math.Clamp(frameCount, 1, 8));
        for (var index = 0; index < Math.Clamp(frameCount, 1, 8); index++) {
            ct.ThrowIfCancellationRequested();
            var frame = await _frames.CaptureForAnalysisAsync(exposureSeconds, binning, ct)
                .ConfigureAwait(false);
            var detected = StarDetector.Detect(frame.Pixels.Span, frame.Width, frame.Height,
                new StarDetectionParams {
                    Sensitivity = 8,
                    MaxNumberOfStars = 32,
                    NoiseReduction = 0,
                }, ct);
            var frameValues = detected.StarList
                .Select(star => 1 - Math.Clamp(star.Roundness, 0, 1))
                .Where(value => double.IsFinite(value))
                .OrderBy(value => value)
                .ToArray();
            if (frameValues.Length > 0)
                values.Add(Percentile(frameValues, .5));
        }

        if (values.Count == 0)
            throw new InvalidOperationException("main-camera validation found no usable stars");
        return Percentile(values.OrderBy(value => value).ToArray(), .5);
    }

    private static double Percentile(double[] values, double percentile) {
        if (values.Length == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (values.Length - 1);
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        return values[low] + (values[high] - values[low]) * (position - low);
    }
}
