#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Image.ImageAnalysis;
using SkiaSharp;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class StarAnnotatorTest {
    [Test]
    public async Task Annotate_is_source_safe_styled_and_reports_cap_rejections() {
        const int width = 80;
        const int height = 60;
        var source = Enumerable.Repeat((byte)45, width * height).ToArray();
        var original = source.ToArray();
        var detection = new StarDetectionResult {
            DetectedStars = 3,
            StarList = new[] {
                Star(width, 40, 30, 2.5),
                Star(width, 20, 20, 2.0),
                Star(width, 60, 40, 1.5),
            },
        };
        var options = new StarAnnotationOptions(
            Red: 255, Green: 0, Blue: 0, ShowLabels: true, MaxAnnotations: 1);

        var result = await new StarAnnotator().AnnotateAsync(new StarAnnotationRequest(
            source, width, height, IsColor: false, MaxDimension: 80, detection, options),
            CancellationToken.None);
        using var decoded = SKBitmap.Decode(result.Image);

        var redPixels = 0;
        for (var y = 0; y < decoded.Height; y++) {
            for (var x = 0; x < decoded.Width; x++) {
                var color = decoded.GetPixel(x, y);
                if (color.Red > color.Green + 40 && color.Red > color.Blue + 40) redPixels++;
            }
        }
        Assert.Multiple(() => {
            Assert.That(source, Is.EqualTo(original), "annotation must not mutate the display plane");
            Assert.That(result.AnnotationCount, Is.EqualTo(1));
            Assert.That(result.RejectedCount, Is.EqualTo(2));
            Assert.That(decoded.Width, Is.EqualTo(width));
            Assert.That(decoded.Height, Is.EqualTo(height));
            Assert.That(redPixels, Is.GreaterThan(10));
        });
    }

    [Test]
    public void Annotate_rejects_dimension_mismatch() {
        var request = new StarAnnotationRequest(new byte[10], 4, 4, IsColor: false,
            MaxDimension: 4, new StarDetectionResult(), new StarAnnotationOptions());

        var error = Assert.ThrowsAsync<ArgumentException>(() =>
            new StarAnnotator().AnnotateAsync(request, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("does not match"));
    }

    [Test]
    public void Annotate_honors_pre_cancelled_token() {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new StarAnnotationRequest(new byte[16], 4, 4, IsColor: false,
            MaxDimension: 4, new StarDetectionResult(), new StarAnnotationOptions());

        Assert.ThrowsAsync<OperationCanceledException>(() =>
            new StarAnnotator().AnnotateAsync(request, cts.Token));
    }

    private static DetectedStar Star(int width, int x, int y, double hfr) => new() {
        Position = y * width + x,
        HFR = hfr,
    };
}