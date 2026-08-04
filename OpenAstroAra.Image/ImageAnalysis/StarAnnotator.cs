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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.ImageAnalysis;

/// <summary>Converts managed detector results into source-safe JPEG marker overlays.</summary>
public sealed class StarAnnotator : IStarAnnotator {
    public Task<StarAnnotationResult> AnnotateAsync(
            StarAnnotationRequest request, CancellationToken token) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Detection);
        ArgumentNullException.ThrowIfNull(request.Options);
        Validate(request);
        token.ThrowIfCancellationRequested();

        var options = request.Options;
        var candidates = request.Detection.StarList;
        var markers = new List<StarMarker>(Math.Min(candidates.Count, options.MaxAnnotations));
        for (var index = 0; index < candidates.Count && markers.Count < options.MaxAnnotations; index++) {
            if ((index & 0xff) == 0) token.ThrowIfCancellationRequested();
            var star = candidates[index];
            if (!double.IsFinite(star.Position) || star.Position < 0
                || star.Position >= (long)request.Width * request.Height) {
                continue;
            }
            var (x, y) = star.Unpack(request.Width);
            if (x < 0 || x >= request.Width || y < 0 || y >= request.Height
                || !double.IsFinite(star.HFR) || star.HFR <= 0) {
                continue;
            }
            var radius = Math.Max(options.MinimumSourceRadius, star.HFR * options.RadiusScale);
            if (!double.IsFinite(radius) || radius <= 0 || radius > float.MaxValue) continue;
            markers.Add(new StarMarker(x, y, (float)radius,
                options.ShowLabels ? (markers.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }

        var style = new StarMarkerStyle(
            options.Red, options.Green, options.Blue, options.StrokeWidth,
            options.MinimumOutputRadius, options.FontSize, options.FontFamily);
        var source = request.SourcePixels.Span;
        var output = request.IsColor
            ? JpegEncoder.EncodeColorAnnotated(source, request.Width, request.Height, markers,
                maxDim: request.MaxDimension, style: style)
            : JpegEncoder.EncodeGrayAnnotated(source, request.Width, request.Height, markers,
                maxDim: request.MaxDimension, style: style);
        token.ThrowIfCancellationRequested();
        var detectedCount = Math.Max(request.Detection.DetectedStars, candidates.Count);
        return Task.FromResult(new StarAnnotationResult(
            output, markers.Count, Math.Max(0, detectedCount - markers.Count)));
    }

    private static void Validate(StarAnnotationRequest request) {
        if (request.Width <= 0 || request.Height <= 0) {
            throw new ArgumentException("Annotation dimensions must be positive.", nameof(request));
        }
        var channels = request.IsColor ? 3 : 1;
        var expected = checked(request.Width * request.Height * channels);
        if (request.SourcePixels.Length != expected) {
            throw new ArgumentException(
                $"Annotation pixel length ({request.SourcePixels.Length}) does not match {request.Width}x{request.Height}x{channels} ({expected}).",
                nameof(request));
        }
        if (request.MaxDimension <= 0) {
            throw new ArgumentOutOfRangeException(nameof(request), "Annotation maximum dimension must be positive.");
        }
        var options = request.Options;
        if (options.MaxAnnotations <= 0 || options.MaxAnnotations > 10000) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "Maximum annotations must be between 1 and 10000.");
        }
        if (!double.IsFinite(options.RadiusScale) || options.RadiusScale <= 0 || options.RadiusScale > 100) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "Annotation radius scale must be in (0, 100].");
        }
        if (!double.IsFinite(options.MinimumSourceRadius)
            || options.MinimumSourceRadius <= 0 || options.MinimumSourceRadius > 1000) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "Annotation minimum source radius must be in (0, 1000].");
        }
    }
}