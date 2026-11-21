/// Client mirror of the daemon's guider calibration-files status
/// (`CalibrationFilesStatusDto`, returned inside `CalibrationFilesStatusResponseDto`
/// from `GET /api/v1/equipment/guider/darklibrary/status`). Snake_case wire.
library;

/// Which dark-library / defect-map files exist for the active guider profile,
/// whether they're loaded/compatible, the auto-load flags, and the loaded-dark
/// count + exposure range when available. Defensive parse — missing/wrong-typed
/// fields degrade rather than throw.
class CalibrationStatus {
  /// Daemon profile id; `null` when the payload omitted it or it was wrong-typed
  /// (don't coerce to 0 — that's a valid profile id, so a parse failure must not
  /// silently target the wrong profile if this is ever sent back to the server).
  final int? profileId;
  final String? darkLibraryPath;
  final String? defectMapPath;
  final bool darkLibraryExists;
  final bool defectMapExists;
  final bool darkLibraryCompatible;
  final bool defectMapCompatible;
  final bool darkLibraryLoaded;
  final bool defectMapLoaded;
  final bool autoLoadDarks;
  final bool autoLoadDefectMap;
  final int? darkCountLoaded;
  final double? darkMinExposureSecondsLoaded;
  final double? darkMaxExposureSecondsLoaded;

  const CalibrationStatus({
    this.profileId,
    this.darkLibraryPath,
    this.defectMapPath,
    this.darkLibraryExists = false,
    this.defectMapExists = false,
    this.darkLibraryCompatible = false,
    this.defectMapCompatible = false,
    this.darkLibraryLoaded = false,
    this.defectMapLoaded = false,
    this.autoLoadDarks = false,
    this.autoLoadDefectMap = false,
    this.darkCountLoaded,
    this.darkMinExposureSecondsLoaded,
    this.darkMaxExposureSecondsLoaded,
  });

  factory CalibrationStatus.fromJson(Map<String, dynamic> json) {
    return CalibrationStatus(
      // Strict int (no num→toInt truncation): a fractional profile_id is
      // malformed, and silently truncating it to a valid-looking id could target
      // the wrong profile — so degrade to null, same rationale as not coercing to 0.
      profileId: json['profile_id'] is int ? json['profile_id'] as int : null,
      darkLibraryPath: _str(json['dark_library_path']),
      defectMapPath: _str(json['defect_map_path']),
      darkLibraryExists: _bool(json['dark_library_exists']),
      defectMapExists: _bool(json['defect_map_exists']),
      darkLibraryCompatible: _bool(json['dark_library_compatible']),
      defectMapCompatible: _bool(json['defect_map_compatible']),
      darkLibraryLoaded: _bool(json['dark_library_loaded']),
      defectMapLoaded: _bool(json['defect_map_loaded']),
      autoLoadDarks: _bool(json['auto_load_darks']),
      autoLoadDefectMap: _bool(json['auto_load_defect_map']),
      darkCountLoaded: _int(json['dark_count_loaded']),
      darkMinExposureSecondsLoaded: _double(json['dark_min_exposure_seconds_loaded']),
      darkMaxExposureSecondsLoaded: _double(json['dark_max_exposure_seconds_loaded']),
    );
  }

  static String? _str(dynamic v) => v is String ? v : null;
  static bool _bool(dynamic v) => v is bool ? v : false;
  static int? _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : null);
  static double? _double(dynamic v) {
    final d = v is num ? v.toDouble() : null;
    return (d != null && d.isFinite) ? d : null;
  }

  // Value equality so an unchanged poll/toggle result doesn't churn the widgets
  // watching the provider (matches the project's other models).
  @override
  bool operator ==(Object other) =>
      other is CalibrationStatus &&
      other.profileId == profileId &&
      other.darkLibraryPath == darkLibraryPath &&
      other.defectMapPath == defectMapPath &&
      other.darkLibraryExists == darkLibraryExists &&
      other.defectMapExists == defectMapExists &&
      other.darkLibraryCompatible == darkLibraryCompatible &&
      other.defectMapCompatible == defectMapCompatible &&
      other.darkLibraryLoaded == darkLibraryLoaded &&
      other.defectMapLoaded == defectMapLoaded &&
      other.autoLoadDarks == autoLoadDarks &&
      other.autoLoadDefectMap == autoLoadDefectMap &&
      other.darkCountLoaded == darkCountLoaded &&
      other.darkMinExposureSecondsLoaded == darkMinExposureSecondsLoaded &&
      other.darkMaxExposureSecondsLoaded == darkMaxExposureSecondsLoaded;

  @override
  int get hashCode => Object.hashAll([
        profileId,
        darkLibraryPath,
        defectMapPath,
        darkLibraryExists,
        defectMapExists,
        darkLibraryCompatible,
        defectMapCompatible,
        darkLibraryLoaded,
        defectMapLoaded,
        autoLoadDarks,
        autoLoadDefectMap,
        darkCountLoaded,
        darkMinExposureSecondsLoaded,
        darkMaxExposureSecondsLoaded,
      ]);
}

/// The status read's envelope: [connected] distinguishes "guider not connected"
/// ([status] null) from "connected, here's the status".
class CalibrationStatusResponse {
  final bool connected;
  final CalibrationStatus? status;
  const CalibrationStatusResponse({required this.connected, this.status});

  factory CalibrationStatusResponse.fromJson(Map<String, dynamic> json) {
    final connected = json['connected'];
    final s = json['status'];
    return CalibrationStatusResponse(
      connected: connected is bool && connected,
      status: s is Map<String, dynamic> ? CalibrationStatus.fromJson(s) : null,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is CalibrationStatusResponse &&
      other.connected == connected &&
      other.status == status;

  @override
  int get hashCode => Object.hash(connected, status);
}
