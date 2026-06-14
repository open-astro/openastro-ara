/// Client models for §50.19 Stats Achievements — the daemon's
/// `StatsAchievementsDto` / `StatsMilestoneDto` from
/// `GET /api/v1/stats/achievements`. Snake_case wire; defensive parse —
/// missing/wrong-typed fields degrade rather than throw.
library;

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
bool _bool(dynamic v) => v is bool ? v : false;
String _str(dynamic v) => v is String ? v : '';
DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;

/// One milestone badge — a fixed [threshold] against a cumulative metric, with
/// the user's [current] value and whether it's been [achieved].
class StatsMilestone {
  final String id;
  final String title;
  final String description;
  final bool achieved;
  final double threshold;
  final double current;

  const StatsMilestone({
    required this.id,
    required this.title,
    required this.description,
    required this.achieved,
    required this.threshold,
    required this.current,
  });

  /// Progress toward the threshold, clamped to [0, 1]. Returns 1.0 for a
  /// non-positive threshold so a degenerate badge reads as complete rather than
  /// dividing by zero.
  double get progress {
    if (threshold <= 0) return 1.0;
    return (current / threshold).clamp(0.0, 1.0);
  }

  factory StatsMilestone.fromJson(Map<String, dynamic> json) => StatsMilestone(
        id: _str(json['id']),
        title: _str(json['title']),
        description: _str(json['description']),
        achieved: _bool(json['achieved']),
        threshold: _dbl(json['threshold']),
        current: _dbl(json['current']),
      );
}

/// Cumulative imaging records + milestone badges for the active profile.
class StatsAchievements {
  final int totalNightsImaged;
  final int longestStreakNights;
  final int currentStreakNights;
  final double longestNightHours;
  final double totalIntegrationHours;
  final int uniqueTargetsImaged;
  final int totalLightFrames;
  final DateTime? firstLightUtc;
  final List<StatsMilestone> milestones;

  const StatsAchievements({
    this.totalNightsImaged = 0,
    this.longestStreakNights = 0,
    this.currentStreakNights = 0,
    this.longestNightHours = 0.0,
    this.totalIntegrationHours = 0.0,
    this.uniqueTargetsImaged = 0,
    this.totalLightFrames = 0,
    this.firstLightUtc,
    this.milestones = const <StatsMilestone>[],
  });

  /// True when the catalog has no light frames — the view shows an empty state
  /// rather than a wall of zeros.
  bool get isEmpty => totalLightFrames == 0;

  factory StatsAchievements.fromJson(Map<String, dynamic> json) {
    final raw = json['milestones'];
    return StatsAchievements(
      totalNightsImaged: _int(json['total_nights_imaged']),
      longestStreakNights: _int(json['longest_streak_nights']),
      currentStreakNights: _int(json['current_streak_nights']),
      longestNightHours: _dbl(json['longest_night_hours']),
      totalIntegrationHours: _dbl(json['total_integration_hours']),
      uniqueTargetsImaged: _int(json['unique_targets_imaged']),
      totalLightFrames: _int(json['total_light_frames']),
      firstLightUtc: _dt(json['first_light_utc']),
      milestones: raw is List
          ? raw
              .whereType<Map<String, dynamic>>()
              .map(StatsMilestone.fromJson)
              .toList(growable: false)
          : const <StatsMilestone>[],
    );
  }
}
