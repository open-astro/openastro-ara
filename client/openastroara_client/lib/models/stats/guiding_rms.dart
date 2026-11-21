/// Client model for §50.7 Guiding RMS — the daemon's `StatsGuidingDto` from
/// `GET /api/v1/stats/guiding`: a chronological series of per-frame total
/// guiding RMS (arcsec), plus mean and p95 summary stats. RA/Dec components are
/// optional (the daemon nulls them until separated columns exist). Snake_case
/// wire; defensive parse.
library;

import 'stats_time.dart';

double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
double? _dblOrNull(dynamic v) => v is num ? v.toDouble() : null;

/// One guiding sample: the capture instant and the total RMS, with optional
/// RA/Dec breakdown when the daemon provides it.
class GuidingRmsPoint {
  final DateTime? timestamp;
  final double rmsArcsec;
  final double? raRms;
  final double? decRms;

  const GuidingRmsPoint({
    this.timestamp,
    this.rmsArcsec = 0.0,
    this.raRms,
    this.decRms,
  });

  factory GuidingRmsPoint.fromJson(Map<String, dynamic> json) => GuidingRmsPoint(
        timestamp: parseStatsUtc(json['timestamp']),
        rmsArcsec: _dbl(json['rms_arcsec']),
        raRms: _dblOrNull(json['ra_rms']),
        decRms: _dblOrNull(json['dec_rms']),
      );
}

/// The active profile's guiding-RMS trend.
class GuidingRmsSeries {
  final List<GuidingRmsPoint> samples;
  final double? meanRmsArcsec;
  final double? p95RmsArcsec;

  const GuidingRmsSeries({
    this.samples = const [],
    this.meanRmsArcsec,
    this.p95RmsArcsec,
  });

  /// True when no guiding sample exists yet — the view shows an empty state.
  bool get isEmpty => samples.isEmpty;

  factory GuidingRmsSeries.fromJson(Map<String, dynamic> json) {
    final raw = json['samples'];
    // Drop any sample without a numeric `rms_arcsec`: it's the only plotted
    // value, and degrading a missing one to 0.0 would render an indistinguishable
    // "perfect guiding" spike. The daemon never sends null here (its query
    // filters `guiding_rms_arcsec IS NOT NULL`), so this only guards schema drift.
    final samples = raw is List
        ? [
            for (final e in raw)
              if (e is Map<String, dynamic> && e['rms_arcsec'] is num)
                GuidingRmsPoint.fromJson(e),
          ]
        : const <GuidingRmsPoint>[];
    return GuidingRmsSeries(
      samples: samples,
      meanRmsArcsec: _dblOrNull(json['mean_rms_arcsec']),
      p95RmsArcsec: _dblOrNull(json['p95_rms_arcsec']),
    );
  }
}
