/// Client model for §50.10 Frame Quality — the daemon's `StatsFrameQualityDto`
/// from `GET /api/v1/stats/frame-quality`: a histogram of composite
/// frame-quality scores (0–1, higher = better) over 10 fixed buckets, plus the
/// mean and standard deviation. Snake_case wire; defensive parse.
library;

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
double? _dblOrNull(dynamic v) => v is num ? v.toDouble() : null;

/// One histogram bucket: the half-open score range `[rangeLow, rangeHigh)` and
/// how many frames scored within it.
class FrameQualityBucket {
  final double rangeLow;
  final double rangeHigh;
  final int count;

  const FrameQualityBucket({
    this.rangeLow = 0.0,
    this.rangeHigh = 0.0,
    this.count = 0,
  });

  factory FrameQualityBucket.fromJson(Map<String, dynamic> json) =>
      FrameQualityBucket(
        rangeLow: _dbl(json['range_low']),
        rangeHigh: _dbl(json['range_high']),
        count: _int(json['count']),
      );
}

/// The full composite-quality distribution for the active profile.
class FrameQualityDistribution {
  final List<FrameQualityBucket> buckets;
  final double meanScore;
  final double? stdDev;

  const FrameQualityDistribution({
    this.buckets = const [],
    this.meanScore = 0.0,
    this.stdDev,
  });

  /// True when no frame has been quality-scored yet — the view shows an empty
  /// state. Keyed on total count, not bucket presence: the server always
  /// returns 10 buckets even for an empty catalog.
  bool get isEmpty => buckets.fold<int>(0, (sum, b) => sum + b.count) == 0;

  factory FrameQualityDistribution.fromJson(Map<String, dynamic> json) {
    final raw = json['distribution'];
    final buckets = raw is List
        ? [
            for (final e in raw)
              if (e is Map<String, dynamic>) FrameQualityBucket.fromJson(e),
          ]
        : const <FrameQualityBucket>[];
    return FrameQualityDistribution(
      buckets: buckets,
      meanScore: _dbl(json['mean_score']),
      stdDev: _dblOrNull(json['std_dev']),
    );
  }
}
