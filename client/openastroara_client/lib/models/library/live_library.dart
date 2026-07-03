/// §40 live library wire models (`/api/v1/sessions` + `/api/v1/frames`),
/// snake_case per the daemon convention. These are the wire-truth models the
/// Image Library renders in 12f.2; the richer demo models in `frame.dart`
/// remain for the Stats dashboard until §50 live-wiring.
library;

class LibrarySession {
  final String id;
  final String targetName;
  final DateTime sessionStartUtc;
  final DateTime? sessionEndUtc;
  final int totalFrames;
  final int lightFrames;
  final int calibrationFrames;
  final List<String> filtersUsed;

  const LibrarySession({
    required this.id,
    required this.targetName,
    required this.sessionStartUtc,
    required this.sessionEndUtc,
    required this.totalFrames,
    required this.lightFrames,
    required this.calibrationFrames,
    required this.filtersUsed,
  });

  factory LibrarySession.fromJson(Map<String, dynamic> json) {
    return LibrarySession(
      id: (json['id'] as String?) ?? '',
      targetName: (json['target_name'] as String?) ?? '(unknown)',
      sessionStartUtc:
          DateTime.tryParse(json['session_start_utc'] as String? ?? '') ??
              DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      sessionEndUtc:
          DateTime.tryParse(json['session_end_utc'] as String? ?? ''),
      totalFrames: (json['total_frames'] as num?)?.toInt() ?? 0,
      lightFrames: (json['light_frames'] as num?)?.toInt() ?? 0,
      calibrationFrames: (json['calibration_frames'] as num?)?.toInt() ?? 0,
      filtersUsed: ((json['filters_used'] as List?) ?? const [])
          .whereType<String>()
          .toList(growable: false),
    );
  }
}

class LibraryFrameItem {
  final String id;
  final String frameType; // light | dark | flat | bias (wire enum, lowercase)
  final String? filterName;
  final double exposureSeconds;
  final DateTime capturedUtc;
  final double? hfr;
  final int? starCount;
  final int rating;

  const LibraryFrameItem({
    required this.id,
    required this.frameType,
    required this.filterName,
    required this.exposureSeconds,
    required this.capturedUtc,
    required this.hfr,
    required this.starCount,
    required this.rating,
  });

  factory LibraryFrameItem.fromJson(Map<String, dynamic> json) {
    return LibraryFrameItem(
      id: (json['id'] as String?) ?? '',
      frameType: (json['frame_type'] as String?) ?? 'light',
      filterName: json['filter_name'] as String?,
      exposureSeconds: (json['exposure_seconds'] as num?)?.toDouble() ?? 0,
      capturedUtc: DateTime.tryParse(json['captured_utc'] as String? ?? '') ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      hfr: (json['hfr'] as num?)?.toDouble(),
      starCount: (json['star_count'] as num?)?.toInt(),
      rating: (json['rating'] as num?)?.toInt() ?? 0,
    );
  }
}
