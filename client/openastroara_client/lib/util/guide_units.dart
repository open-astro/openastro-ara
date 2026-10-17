/// Guide-camera pixels in the units a person can judge: arcsec on the sky and
/// pixels on the imaging sensor. The guider works in its own pixels, and "5 px"
/// means 15″ on a 200 mm guide scope with an ASI290 and 60″ on a short finder;
/// on 2026-10-16 a 5 px dither against a 1.5 px settle tolerance timed out 12
/// settles, and nothing on screen said how big either figure was.
library;

/// Arcsec per imaging pixel from the main optics; null when the optics are
/// not configured.
double? imagingPixelScaleArcsec(
    {required double focalLengthMm,
    required double reducerFactor,
    required double pixelSizeUm}) {
  final focal = focalLengthMm * (reducerFactor > 0 ? reducerFactor : 1.0);
  if (focal <= 0 || pixelSizeUm <= 0) return null;
  return 206.265 * pixelSizeUm / focal;
}

/// Guide pixels → arcsec; null when the guider has not reported its scale.
double? guidePixelsToArcsec(double guidePx, double? guideScaleArcsec) =>
    guideScaleArcsec == null || guideScaleArcsec <= 0
        ? null
        : guidePx * guideScaleArcsec;

/// Arcsec → guide pixels (the inverse, for entering a value in arcsec).
double? arcsecToGuidePixels(double arcsec, double? guideScaleArcsec) =>
    guideScaleArcsec == null || guideScaleArcsec <= 0
        ? null
        : arcsec / guideScaleArcsec;

/// Guide pixels → imaging pixels through both scales; null when either is
/// unknown.
double? guidePixelsToImagingPixels(
    double guidePx, double? guideScaleArcsec, double? imagingScaleArcsec) {
  final arcsec = guidePixelsToArcsec(guidePx, guideScaleArcsec);
  if (arcsec == null || imagingScaleArcsec == null || imagingScaleArcsec <= 0) {
    return null;
  }
  return arcsec / imagingScaleArcsec;
}

/// One line for a settings hint: "≈ 15.0″ · 8.6 imaging px" (both scales
/// known), "≈ 15.0″" (guider scale only), null (nothing to say yet).
String? describeGuidePixels(
    double guidePx, double? guideScaleArcsec, double? imagingScaleArcsec) {
  final arcsec = guidePixelsToArcsec(guidePx, guideScaleArcsec);
  if (arcsec == null) return null;
  final imaging =
      guidePixelsToImagingPixels(guidePx, guideScaleArcsec, imagingScaleArcsec);
  final sky = '≈ ${arcsec.toStringAsFixed(1)}″';
  return imaging == null
      ? sky
      : '$sky · ${imaging.toStringAsFixed(1)} imaging px';
}

/// Rounds a guide-pixel figure entered through a conversion to a sensible
/// precision for the profile (hundredths).
double roundGuidePixels(double guidePx) => (guidePx * 100).roundToDouble() / 100;
