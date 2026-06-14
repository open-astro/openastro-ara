/// Client model for one row of `GET /api/v1/stats/targets` (the daemon's
/// `StatsTargetSummaryDto`). Snake_case wire; defensive parse — missing/
/// wrong-typed fields degrade rather than throw.
library;

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
double? _dblOrNull(dynamic v) => v is num ? v.toDouble() : null;
String _str(dynamic v) => v is String ? v : '';
DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;

/// A target the catalog has frames for, with its frame count + total
/// integration. Drives both the AstroBin per-target export picker and the §50
/// dashboard Targets section.
class StatsTarget {
  final String targetName;
  final int frameCount;
  final double integrationHours;

  /// Mean composite frame-quality score for the target, or `null` when the
  /// catalog has no scored frames for it yet (e.g. unrated imports).
  final double? compositeQualityScore;
  final DateTime? lastImagedUtc;

  const StatsTarget({
    required this.targetName,
    this.frameCount = 0,
    this.integrationHours = 0.0,
    this.compositeQualityScore,
    this.lastImagedUtc,
  });

  factory StatsTarget.fromJson(Map<String, dynamic> json) => StatsTarget(
        targetName: _str(json['target_name']),
        frameCount: _int(json['frame_count']),
        integrationHours: _dbl(json['integration_hours']),
        compositeQualityScore: _dblOrNull(json['composite_quality_score']),
        lastImagedUtc: _dt(json['last_imaged_utc']),
      );
}
