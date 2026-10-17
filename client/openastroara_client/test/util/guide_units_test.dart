import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/guide_units.dart';

void main() {
  // The CM4 rig of 2026-10-16: 200 mm guide scope + ASI290 (2.99″/px), RedCat 91
  // at 448 mm + ASI2600 (1.73″/px).
  const guide = 2.99;

  test('imaging pixel scale from the main optics', () {
    final scale = imagingPixelScaleArcsec(focalLengthMm: 448, reducerFactor: 1.0, pixelSizeUm: 3.76)!;
    expect(scale, closeTo(1.73, 0.01));
    expect(imagingPixelScaleArcsec(focalLengthMm: 0, reducerFactor: 1.0, pixelSizeUm: 3.76), isNull);
    expect(imagingPixelScaleArcsec(focalLengthMm: 448, reducerFactor: 0, pixelSizeUm: 3.76), closeTo(1.73, 0.01),
        reason: 'a zero reducer means none');
  });

  test('guide pixels to arcsec and imaging pixels, and back', () {
    expect(guidePixelsToArcsec(5, guide), closeTo(14.95, 0.01));
    expect(guidePixelsToImagingPixels(5, guide, 1.73), closeTo(8.64, 0.01));
    expect(arcsecToGuidePixels(15, guide), closeTo(5.02, 0.01));
    expect(guidePixelsToArcsec(5, null), isNull);
    expect(guidePixelsToArcsec(5, 0), isNull);
    expect(guidePixelsToImagingPixels(5, guide, null), isNull);
    expect(roundGuidePixels(5.0167), 5.02);
  });

  test('the hint says as much as the scales allow', () {
    expect(describeGuidePixels(5, guide, 1.73), '≈ 15.0″ · 8.6 imaging px');
    expect(describeGuidePixels(1.5, guide, null), '≈ 4.5″');
    expect(describeGuidePixels(5, null, 1.73), isNull, reason: 'no guider scale, nothing to say');
  });
}
