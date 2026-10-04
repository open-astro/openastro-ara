/// Client mirror of the daemon's guide-camera focus loop
/// (`GET /api/v1/equipment/guider/focus`, `GuideFocusStatusDto`). Wire format is
/// snake_case.
library;

abstract final class GuideFocusStates {
  static const idle = 'idle';
  static const running = 'running';
  static const stopped = 'stopped';
  static const error = 'error';
}

double _d(Map<String, dynamic> json, String key) =>
    (json[key] as num?)?.toDouble() ?? 0;

int _i(Map<String, dynamic> json, String key) => (json[key] as num?)?.toInt() ?? 0;

/// One measured guide frame.
class GuideFocusSample {
  final int seq;
  final DateTime? capturedUtc;
  final double hfr;
  final int stars;
  final double peakAdu;
  final double fwhm;

  const GuideFocusSample({
    required this.seq,
    this.capturedUtc,
    required this.hfr,
    required this.stars,
    required this.peakAdu,
    required this.fwhm,
  });

  factory GuideFocusSample.fromJson(Map<String, dynamic> json) => GuideFocusSample(
        seq: _i(json, 'seq'),
        capturedUtc: json['captured_utc'] is String
            ? DateTime.tryParse(json['captured_utc'] as String)
            : null,
        hfr: _d(json, 'hfr'),
        stars: _i(json, 'stars'),
        peakAdu: _d(json, 'peak_adu'),
        fwhm: _d(json, 'fwhm'),
      );
}

/// The loop's snapshot.
class GuideFocusStatus {
  final bool active;
  final String state;
  final double exposureSec;
  final int seq;
  final DateTime? startedUtc;
  final GuideFocusSample? latest;
  final double? bestHfr;
  final int? bestSeq;
  final List<GuideFocusSample> recent;
  final String? error;
  final int consecutiveFailures;
  final bool hasFrame;
  /// What an in-focus star should read on this guide camera (px), from the
  /// profile's guide optics; null when the profile has no guide focal length.
  final double? expectedHfr;
  final double? plateScaleArcsec;

  const GuideFocusStatus({
    this.active = false,
    this.state = GuideFocusStates.idle,
    this.exposureSec = 0,
    this.seq = 0,
    this.startedUtc,
    this.latest,
    this.bestHfr,
    this.bestSeq,
    this.recent = const [],
    this.error,
    this.consecutiveFailures = 0,
    this.hasFrame = false,
    this.expectedHfr,
    this.plateScaleArcsec,
  });

  static const idle = GuideFocusStatus();

  factory GuideFocusStatus.fromJson(Map<String, dynamic> json) {
    final raw = json['recent'];
    final recent = <GuideFocusSample>[];
    if (raw is List) {
      for (final s in raw) {
        if (s is Map<String, dynamic>) recent.add(GuideFocusSample.fromJson(s));
      }
    }
    final latest = json['latest'];
    return GuideFocusStatus(
      active: json['active'] as bool? ?? false,
      state: json['state'] is String ? json['state'] as String : GuideFocusStates.idle,
      exposureSec: _d(json, 'exposure_sec'),
      seq: _i(json, 'seq'),
      startedUtc: json['started_utc'] is String
          ? DateTime.tryParse(json['started_utc'] as String)
          : null,
      latest: latest is Map<String, dynamic> ? GuideFocusSample.fromJson(latest) : null,
      bestHfr: (json['best_hfr'] as num?)?.toDouble(),
      bestSeq: (json['best_seq'] as num?)?.toInt(),
      recent: recent,
      error: json['error'] is String ? json['error'] as String : null,
      consecutiveFailures: _i(json, 'consecutive_failures'),
      hasFrame: json['has_frame'] as bool? ?? false,
      expectedHfr: (json['expected_hfr'] as num?)?.toDouble(),
      plateScaleArcsec: (json['plate_scale_arcsec'] as num?)?.toDouble(),
    );
  }
}
