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

  const CalibrationSession({
    required this.id,
    required this.targetName,
    required this.sessionStartUtc,
    required this.sessionEndUtc,
    required this.lightFrameCount,
    required this.filtersUsed,
    required this.matchingFlatsAvailable,
    required this.matchingDarksAvailable,
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
    );
  }
}

class CalibrationFilterSummary {
  final String filterName;
  final int lightFrameCount;
  final double meanExposureSeconds;

  const CalibrationFilterSummary({
    required this.filterName,
    required this.lightFrameCount,
    required this.meanExposureSeconds,
  });

  factory CalibrationFilterSummary.fromJson(Map<String, dynamic> json) {
    return CalibrationFilterSummary(
      filterName: (json['filter_name'] as String?) ?? '(no filter)',
      lightFrameCount: (json['light_frame_count'] as num?)?.toInt() ?? 0,
      meanExposureSeconds:
          (json['mean_exposure_seconds'] as num?)?.toDouble() ?? 0,
    );
  }
}

/// Result of POST `/calibration/sessions/{id}/matching-flats`.
/// [generatedSequenceId] is null for a plan-only (generate_only) call.
class GeneratedFlatSequence {
  final String? generatedSequenceId;
  final String generatedSequenceName;
  final int totalFlatFrames;

  const GeneratedFlatSequence({
    required this.generatedSequenceId,
    required this.generatedSequenceName,
    required this.totalFlatFrames,
  });

  factory GeneratedFlatSequence.fromJson(Map<String, dynamic> json) {
    final id = json['generated_sequence_id'];
    return GeneratedFlatSequence(
      generatedSequenceId: id is String && id.isNotEmpty ? id : null,
      generatedSequenceName: (json['generated_sequence_name'] as String?) ?? '',
      totalFlatFrames: (json['total_flat_frames'] as num?)?.toInt() ?? 0,
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

/// GET `/calibration/dark-library/status`. Coverage semantics per the §39.8
/// server contract: non-reuse builds count only frames captured since the
/// build request.
class DarkLibraryState {
  final String status; // idle | pending | complete
  final int totalCombinations;
  final int completedCombinations;
  final String? generatedSequenceId;
  final List<DarkLibraryEntry> entries;

  const DarkLibraryState({
    required this.status,
    required this.totalCombinations,
    required this.completedCombinations,
    required this.generatedSequenceId,
    required this.entries,
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
