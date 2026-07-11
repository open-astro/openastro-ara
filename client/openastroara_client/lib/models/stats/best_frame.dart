/// Client model for §50 Stats Best Frames — the daemon's `BestFrameDto` from
/// `GET /api/v1/stats/best-frames`. Snake_case wire; defensive parse — missing
/// / wrong-typed fields degrade rather than throw, EXCEPT `frame_id`: it keys
/// the row (and the coming per-frame drill-down), so a row without one is
/// unusable and is dropped by [listFromJson] instead of degrading to `''`.
library;

import 'package:flutter/foundation.dart';

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

  /// Parses the `{ "frames": [...] }` envelope, dropping non-object rows and
  /// rows without a usable `frame_id`.
  static List<BestFrame> listFromJson(Map<String, dynamic> json) {
    final raw = json['frames'];
    if (raw is! List) return const [];
    final frames = [
      for (final e in raw)
        if (e is Map<String, dynamic>) BestFrame.fromJson(e),
    ];
    final usable = frames.where((f) => f.frameId.isNotEmpty).toList(growable: false);
    final dropped = raw.length - usable.length;
    if (dropped > 0) {
      debugPrint('[stats] best-frames dropped $dropped malformed/unkeyed '
          'row${dropped == 1 ? '' : 's'} of ${raw.length}');
    }
    return usable;
  }
}
