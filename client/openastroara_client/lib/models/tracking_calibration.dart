/// #1264 — the unguided tracking ceiling the daemon measured (or took as a
/// prior) for this rig: `GET /api/v1/tracking/calibration` and the profile's
/// `tracking_calibration` key. Null on the wire means "never measured".
class TrackingCalibration {
  final double ceilingSeconds;
  final DateTime measuredUtc;
  final double? polarAlignErrorArcmin;
  final double? pixelScaleArcsec;
  final double? seeingFwhmArcsec;
  final double? priorSeconds;

  /// `measured` (the probe's ladder decided it) or `prior` (fewer than two
  /// usable rungs, so the spec-sheet / previous-night figure stood).
  final String source;
  final List<TrackingProbeRung> rungs;

  const TrackingCalibration({
    required this.ceilingSeconds,
    required this.measuredUtc,
    this.polarAlignErrorArcmin,
    this.pixelScaleArcsec,
    this.seeingFwhmArcsec,
    this.priorSeconds,
    required this.source,
    this.rungs = const [],
  });

  bool get measured => source == 'measured';

  /// Parse a `TrackingCalibrationDto`; null when the body is not the record
  /// (an empty 200 from a daemon that has nothing).
  static TrackingCalibration? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final ceiling = json['ceiling_seconds'];
    final measured = json['measured_utc'];
    if (ceiling is! num || measured is! String) return null;
    final at = DateTime.tryParse(measured);
    if (at == null) return null;
    final rungsJson = json['rungs'];
    return TrackingCalibration(
      ceilingSeconds: ceiling.toDouble(),
      measuredUtc: at.toUtc(),
      polarAlignErrorArcmin: (json['polar_align_error_arcmin'] as num?)
          ?.toDouble(),
      pixelScaleArcsec: (json['pixel_scale_arcsec'] as num?)?.toDouble(),
      seeingFwhmArcsec: (json['seeing_fwhm_arcsec'] as num?)?.toDouble(),
      priorSeconds: (json['prior_seconds'] as num?)?.toDouble(),
      source: json['source'] is String ? json['source'] as String : 'measured',
      rungs: rungsJson is List
          ? rungsJson
                .map(TrackingProbeRung.fromJson)
                .whereType<TrackingProbeRung>()
                .toList()
          : const [],
    );
  }
}

/// #1264 stage 1 — `GET /api/v1/tracking/prior`: what the next probe would
/// start from and why ("the last probe", "the mount's spec sheet (manufacturer)"),
/// resolved by the daemon's own code so the client never recomputes it. Both
/// null when nothing applies.
class TrackingPrior {
  final double? seconds;
  final String? source;
  const TrackingPrior({this.seconds, this.source});

  static TrackingPrior fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return const TrackingPrior();
    final s = json['seconds'];
    return TrackingPrior(
      seconds: s is num && s.isFinite && s > 0 ? s.toDouble() : null,
      source: json['source'] is String ? json['source'] as String : null,
    );
  }
}

/// One exposure of the probe's ladder and how it was judged.
class TrackingProbeRung {
  final double exposureSeconds;
  final double? eccentricity;
  final double? fwhmPx;
  final int stars;

  /// Enough stars to judge; an unusable rung is neither a pass nor a fail.
  final bool usable;
  final bool passed;

  const TrackingProbeRung({
    required this.exposureSeconds,
    this.eccentricity,
    this.fwhmPx,
    this.stars = 0,
    required this.usable,
    required this.passed,
  });

  static TrackingProbeRung? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final exposure = json['exposure_seconds'];
    if (exposure is! num) return null;
    return TrackingProbeRung(
      exposureSeconds: exposure.toDouble(),
      eccentricity: (json['eccentricity'] as num?)?.toDouble(),
      fwhmPx: (json['fwhm_px'] as num?)?.toDouble(),
      stars: json['stars'] is num ? (json['stars'] as num).toInt() : 0,
      usable: json['usable'] == true,
      passed: json['passed'] == true,
    );
  }
}

/// "2026-10-11" in the device's local day — the readiness fact and the
/// Unguided panel share it.
String formatCalibrationDate(DateTime utc) {
  final d = utc.toLocal();
  return '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
}

/// "45 s" / "2.5 min" — the same shape the sub advisor uses.
String formatExposureSeconds(double seconds) => seconds < 120
    ? '${seconds.round()} s'
    : '${(seconds / 60).toStringAsFixed(seconds >= 600 ? 0 : 1)} min';
