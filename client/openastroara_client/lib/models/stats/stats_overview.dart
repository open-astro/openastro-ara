/// Client model for §50 Stats Overview — the daemon's `StatsOverviewDto` from
/// `GET /api/v1/stats/overview`. Snake_case wire; defensive parse — missing /
/// wrong-typed fields degrade rather than throw.
library;

import 'stats_time.dart';

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;

/// Headline catalog totals for the active profile.
class StatsOverview {
  final int totalSessions;
  final int totalFrames;
  final int totalLightFrames;
  final double totalIntegrationHours;
  final int uniqueTargetsImaged;
  final DateTime? firstImageUtc;
  final DateTime? lastImageUtc;

  const StatsOverview({
    this.totalSessions = 0,
    this.totalFrames = 0,
    this.totalLightFrames = 0,
    this.totalIntegrationHours = 0.0,
    this.uniqueTargetsImaged = 0,
    this.firstImageUtc,
    this.lastImageUtc,
  });

  /// True when the catalog holds no frames yet — the view shows an empty state.
  /// Keyed on frames, not sessions: a session that exists but has written no
  /// frames has nothing to summarize, so the empty state is the right view.
  bool get isEmpty => totalFrames == 0;

  factory StatsOverview.fromJson(Map<String, dynamic> json) => StatsOverview(
        totalSessions: _int(json['total_sessions']),
        totalFrames: _int(json['total_frames']),
        totalLightFrames: _int(json['total_light_frames']),
        totalIntegrationHours: _dbl(json['total_integration_hours']),
        uniqueTargetsImaged: _int(json['unique_targets_imaged']),
        firstImageUtc: parseStatsUtc(json['first_image_utc']),
        lastImageUtc: parseStatsUtc(json['last_image_utc']),
      );
}
