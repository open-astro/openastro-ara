/// §36.10 — the daemon's answer to "which ASTAP database does this rig need"
/// (`POST /api/v1/data-manager/solver-recommendation`). Null [packageId] with
/// [tooNarrow] false means the field is not known yet; with [tooNarrow] true
/// the narrowest field is below the solver's floor and nothing will solve it.
/// One camera's place on the ladder: its field, band and the database that
/// covers it (null [packageId] with [tooNarrow] when nothing will solve it).
class SolverDatabasePick {
  final String role; // 'camera' | 'guide camera'
  final double fovDeg;
  final String band;
  final String? packageId;
  final String? packageName;
  final int sizeBytes;
  final bool isInstalled;
  final bool tooNarrow;
  final String reason;

  const SolverDatabasePick({
    required this.role,
    required this.fovDeg,
    this.band = 'unknown',
    this.packageId,
    this.packageName,
    this.sizeBytes = 0,
    this.isInstalled = false,
    this.tooNarrow = false,
    this.reason = '',
  });

  factory SolverDatabasePick.fromJson(Map<String, dynamic> json) => SolverDatabasePick(
        role: json['role'] as String? ?? 'camera',
        fovDeg: (json['fov_deg'] as num?)?.toDouble() ?? 0,
        band: json['band'] as String? ?? 'unknown',
        packageId: json['package_id'] as String?,
        packageName: json['package_name'] as String?,
        sizeBytes: (json['size_bytes'] as num?)?.toInt() ?? 0,
        isInstalled: json['is_installed'] as bool? ?? false,
        tooNarrow: json['too_narrow'] as bool? ?? false,
        reason: json['reason'] as String? ?? '',
      );
}

class SolverRecommendation {
  final double? mainFovDeg;
  final double? guideFovDeg;
  final double? narrowestFovDeg;
  final String band;
  final List<SolverDatabasePick> packages;
  final bool tooNarrow;
  final String reason;

  const SolverRecommendation({
    this.mainFovDeg,
    this.guideFovDeg,
    this.narrowestFovDeg,
    this.band = 'unknown',
    this.packages = const [],
    this.tooNarrow = false,
    this.reason = '',
  });

  factory SolverRecommendation.fromJson(Map<String, dynamic> json) {
    double? d(String k) {
      final v = (json[k] as num?)?.toDouble();
      return (v != null && v.isFinite) ? v : null;
    }

    return SolverRecommendation(
      mainFovDeg: d('main_fov_deg'),
      guideFovDeg: d('guide_fov_deg'),
      narrowestFovDeg: d('narrowest_fov_deg'),
      band: json['band'] as String? ?? 'unknown',
      packages: (json['packages'] as List?)
              ?.whereType<Map<String, dynamic>>()
              .map(SolverDatabasePick.fromJson)
              .toList() ??
          const [],
      tooNarrow: json['too_narrow'] as bool? ?? false,
      reason: json['reason'] as String? ?? '',
    );
  }
}

/// The optics the wizard holds before the profile is saved; every field is
/// optional and the daemon falls back to the active profile for what is missing.
class SolverRecommendationRequest {
  final double? focalLengthMm;
  final double? reducerFactor;
  final int? sensorWidthPx;
  final int? sensorHeightPx;
  final double? pixelSizeUm;
  final double? guideFocalLengthMm;
  final double? guidePixelSizeUm;
  final int? guideSensorWidthPx;
  final int? guideSensorHeightPx;
  // 'oag' | 'guide_scope': with an OAG the guide camera looks through the
  // main optics, so its field uses the telescope's effective focal length.
  final String? guideSetupType;

  const SolverRecommendationRequest({
    this.focalLengthMm,
    this.reducerFactor,
    this.sensorWidthPx,
    this.sensorHeightPx,
    this.pixelSizeUm,
    this.guideFocalLengthMm,
    this.guidePixelSizeUm,
    this.guideSensorWidthPx,
    this.guideSensorHeightPx,
    this.guideSetupType,
  });

  Map<String, dynamic> toJson() => <String, dynamic>{
        if (focalLengthMm != null) 'focal_length_mm': focalLengthMm,
        if (reducerFactor != null) 'reducer_factor': reducerFactor,
        if (sensorWidthPx != null) 'sensor_width_px': sensorWidthPx,
        if (sensorHeightPx != null) 'sensor_height_px': sensorHeightPx,
        if (pixelSizeUm != null) 'pixel_size_um': pixelSizeUm,
        if (guideFocalLengthMm != null) 'guide_focal_length_mm': guideFocalLengthMm,
        if (guidePixelSizeUm != null) 'guide_pixel_size_um': guidePixelSizeUm,
        if (guideSensorWidthPx != null) 'guide_sensor_width_px': guideSensorWidthPx,
        if (guideSensorHeightPx != null) 'guide_sensor_height_px': guideSensorHeightPx,
        if (guideSetupType != null && guideSetupType!.isNotEmpty) 'guide_setup_type': guideSetupType,
      };
}

/// Frame height in degrees for a sensor [heightPx] tall of [pixelSizeUm]
/// pixels behind [focalLengthMm] × [reducerFactor]; null until every input is
/// positive. The same formula the daemon uses, so the equipment step can show
/// the field live while the user types.
double? fieldOfViewHeightDeg({
  required double? focalLengthMm,
  required double? reducerFactor,
  required int? heightPx,
  required double? pixelSizeUm,
}) {
  if (focalLengthMm == null || focalLengthMm <= 0) return null;
  if (heightPx == null || heightPx <= 0) return null;
  if (pixelSizeUm == null || pixelSizeUm <= 0) return null;
  final reducer = (reducerFactor != null && reducerFactor > 0) ? reducerFactor : 1.0;
  final deg = 206.265 * pixelSizeUm / (focalLengthMm * reducer) * heightPx / 3600.0;
  return deg.isFinite && deg > 0 ? deg : null;
}
