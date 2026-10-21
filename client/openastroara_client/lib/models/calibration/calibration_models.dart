/// §39 calibration wire models (`/api/v1/calibration/*`), snake_case per the
/// daemon convention. Parsed leniently for optional fields, strictly for ids.
library;

class CalibrationSession {
  final String id;
  final String targetName;
  final DateTime sessionStartUtc;
  final DateTime sessionEndUtc;
  final int lightFrameCount;
  final List<CalibrationFilterSummary> filtersUsed;
  final bool matchingFlatsAvailable;
  final bool matchingDarksAvailable;

  /// Every (gain, offset, binning) the lights used has bias frames.
  final bool matchingBiasAvailable;

  /// The cooler set-point most of the lights ran at with the cooler on; null
  /// when they were taken warm (or before the daemon recorded it). Matching
  /// flats cool to it.
  final double? coolerSetpointC;

  const CalibrationSession({
    required this.id,
    required this.targetName,
    required this.sessionStartUtc,
    required this.sessionEndUtc,
    required this.lightFrameCount,
    required this.filtersUsed,
    required this.matchingFlatsAvailable,
    required this.matchingDarksAvailable,
    this.matchingBiasAvailable = false,
    this.coolerSetpointC,
  });

  factory CalibrationSession.fromJson(Map<String, dynamic> json) {
    return CalibrationSession(
      id: (json['id'] as String?) ?? '',
      targetName: (json['target_name'] as String?) ?? '(unknown)',
      sessionStartUtc: DateTime.tryParse(json['session_start_utc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      sessionEndUtc: DateTime.tryParse(json['session_end_utc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      lightFrameCount: (json['light_frame_count'] as num?)?.toInt() ?? 0,
      filtersUsed: ((json['filters_used'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(CalibrationFilterSummary.fromJson)
          .toList(growable: false),
      matchingFlatsAvailable: json['matching_flats_available'] == true,
      matchingDarksAvailable: json['matching_darks_available'] == true,
      matchingBiasAvailable: json['matching_bias_available'] == true,
      coolerSetpointC: (json['cooler_setpoint_c'] as num?)?.toDouble(),
    );
  }
}

class CalibrationFilterSummary {
  final String filterName;
  final int lightFrameCount;
  final double meanExposureSeconds;

  /// Catalogued flats that calibrate this filter's lights: same filter, same
  /// rotator angle, from any night (0 = this filter needs flats).
  final int matchingFlatFrames;
  final DateTime? matchingFlatsNewestUtc;

  /// Why existing flats don't count (another rotator angle), or that the match
  /// is assumed because an angle wasn't recorded.
  final String? flatsNote;

  const CalibrationFilterSummary({
    required this.filterName,
    required this.lightFrameCount,
    required this.meanExposureSeconds,
    this.matchingFlatFrames = 0,
    this.matchingFlatsNewestUtc,
    this.flatsNote,
  });

  bool get hasFlats => matchingFlatFrames > 0;

  factory CalibrationFilterSummary.fromJson(Map<String, dynamic> json) {
    return CalibrationFilterSummary(
      filterName: (json['filter_name'] as String?) ?? '(no filter)',
      lightFrameCount: (json['light_frame_count'] as num?)?.toInt() ?? 0,
      meanExposureSeconds:
          (json['mean_exposure_seconds'] as num?)?.toDouble() ?? 0,
      matchingFlatFrames:
          (json['matching_flat_frames'] as num?)?.toInt() ?? 0,
      matchingFlatsNewestUtc: DateTime.tryParse(
        json['matching_flats_newest_utc'] as String? ?? '',
      ),
      flatsNote: json['flats_note'] as String?,
    );
  }
}

/// Result of POST `/calibration/sessions/{id}/matching-flats`.
/// [generatedSequenceId] is null for a plan-only (generate_only) call.
class GeneratedFlatSequence {
  final String? generatedSequenceId;
  final String generatedSequenceName;
  final int totalFlatFrames;

  /// Bias frames planned after the flats.
  final int totalBiasFrames;

  const GeneratedFlatSequence({
    required this.generatedSequenceId,
    required this.generatedSequenceName,
    required this.totalFlatFrames,
    this.totalBiasFrames = 0,
  });

  factory GeneratedFlatSequence.fromJson(Map<String, dynamic> json) {
    final id = json['generated_sequence_id'];
    return GeneratedFlatSequence(
      generatedSequenceId: id is String && id.isNotEmpty ? id : null,
      generatedSequenceName: (json['generated_sequence_name'] as String?) ?? '',
      totalFlatFrames: (json['total_flat_frames'] as num?)?.toInt() ?? 0,
      totalBiasFrames: (json['total_bias_frames'] as num?)?.toInt() ?? 0,
    );
  }
}

class DarkLibraryEntry {
  final String id;
  final double exposureSeconds;
  final int? gain;
  final double temperatureC;
  final int frameCount;
  final DateTime capturedUtc;
  final int fileSizeBytes;

  const DarkLibraryEntry({
    required this.id,
    required this.exposureSeconds,
    required this.gain,
    required this.temperatureC,
    required this.frameCount,
    required this.capturedUtc,
    required this.fileSizeBytes,
  });

  factory DarkLibraryEntry.fromJson(Map<String, dynamic> json) {
    return DarkLibraryEntry(
      id: (json['id'] as String?) ?? '',
      exposureSeconds: (json['exposure_seconds'] as num?)?.toDouble() ?? 0,
      gain: (json['gain'] as num?)?.toInt(),
      temperatureC: (json['temperature_c'] as num?)?.toDouble() ?? 0,
      frameCount: (json['frame_count'] as num?)?.toInt() ?? 0,
      capturedUtc: DateTime.tryParse(json['captured_utc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      fileSizeBytes: (json['file_size_bytes'] as num?)?.toInt() ?? 0,
    );
  }
}

/// One group of catalogued bias frames: a distinct (gain, offset, binning),
/// the key bias frames match lights by.
class BiasLibraryEntry {
  final String id;
  final int? gain;
  final int? offset;
  final int bin;
  final int frameCount;
  final DateTime capturedUtc;
  final int fileSizeBytes;

  const BiasLibraryEntry({
    required this.id,
    required this.gain,
    required this.offset,
    required this.bin,
    required this.frameCount,
    required this.capturedUtc,
    required this.fileSizeBytes,
  });

  factory BiasLibraryEntry.fromJson(Map<String, dynamic> json) {
    return BiasLibraryEntry(
      id: (json['id'] as String?) ?? '',
      gain: (json['gain'] as num?)?.toInt(),
      offset: (json['offset'] as num?)?.toInt(),
      bin: (json['bin'] as num?)?.toInt() ?? 1,
      frameCount: (json['frame_count'] as num?)?.toInt() ?? 0,
      capturedUtc: DateTime.tryParse(json['captured_utc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      fileSizeBytes: (json['file_size_bytes'] as num?)?.toInt() ?? 0,
    );
  }
}

/// GET `/calibration/dark-library/status`. Coverage semantics per the §39.8
/// server contract: non-reuse builds count only frames captured since the
/// build request.
class DarkLibraryState {
  final String status; // idle | pending | complete
  final int totalCombinations;
  final int completedCombinations;
  final String? generatedSequenceId;
  final List<DarkLibraryEntry> entries;
  final List<BiasLibraryEntry> biasEntries;

  const DarkLibraryState({
    required this.status,
    required this.totalCombinations,
    required this.completedCombinations,
    required this.generatedSequenceId,
    required this.entries,
    this.biasEntries = const [],
  });

  factory DarkLibraryState.fromJson(Map<String, dynamic> json) {
    final id = json['generated_sequence_id'];
    return DarkLibraryState(
      status: (json['status'] as String?) ?? 'idle',
      totalCombinations: (json['total_combinations'] as num?)?.toInt() ?? 0,
      completedCombinations:
          (json['completed_combinations'] as num?)?.toInt() ?? 0,
      generatedSequenceId: id is String && id.isNotEmpty ? id : null,
      entries: ((json['entries'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(DarkLibraryEntry.fromJson)
          .toList(growable: false),
      biasEntries: ((json['bias_entries'] as List?) ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(BiasLibraryEntry.fromJson)
          .toList(growable: false),
    );
  }
}

/// POST `/calibration/dark-library/build` body.
class DarkLibraryBuildRequest {
  final List<double> exposureSecondsList;
  final List<int> gainList;
  final List<double> targetTemperatureCList;
  final int framesPerCombination;
  final bool reuseExistingFrames;

  const DarkLibraryBuildRequest({
    required this.exposureSecondsList,
    required this.gainList,
    required this.targetTemperatureCList,
    required this.framesPerCombination,
    required this.reuseExistingFrames,
  });

  Map<String, dynamic> toJson() => <String, dynamic>{
        'exposure_seconds_list': exposureSecondsList,
        'gain_list': gainList,
        'target_temperature_c_list': targetTemperatureCList,
        'frames_per_combination': framesPerCombination,
        'reuse_existing_frames': reuseExistingFrames,
      };
}
