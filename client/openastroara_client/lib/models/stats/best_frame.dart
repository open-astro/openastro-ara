/// Client model for §50 Stats Best Frames — the daemon's `BestFrameDto` from
/// `GET /api/v1/stats/best-frames`. Snake_case wire; defensive parse — missing
/// / wrong-typed fields degrade rather than throw.
library;

import 'stats_time.dart';

double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
String _str(dynamic v) => v is String ? v : '';
String? _strOrNull(dynamic v) => v is String && v.isNotEmpty ? v : null;

/// One of the top-ranked frames in the catalog, ordered by composite quality
/// score (best first) on the server.
class BestFrame {
  final String frameId;
  final String targetName;
  final DateTime? capturedUtc;
  final double compositeScore;
  final String? filterName;

  const BestFrame({
    required this.frameId,
    this.targetName = '',
    this.capturedUtc,
    this.compositeScore = 0.0,
    this.filterName,
  });

  factory BestFrame.fromJson(Map<String, dynamic> json) => BestFrame(
        frameId: _str(json['frame_id']),
        targetName: _str(json['target_name']),
        capturedUtc: parseStatsUtc(json['captured_utc']),
        compositeScore: _dbl(json['composite_score']),
        filterName: _strOrNull(json['filter_name']),
      );

  /// Parses the `{ "frames": [...] }` envelope, dropping any non-object rows.
  static List<BestFrame> listFromJson(Map<String, dynamic> json) {
    final raw = json['frames'];
    if (raw is! List) return const [];
    return [
      for (final e in raw)
        if (e is Map<String, dynamic>) BestFrame.fromJson(e),
    ];
  }
}
