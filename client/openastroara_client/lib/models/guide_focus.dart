/// Client mirror of the daemon's guide-camera focus loop
/// (`GET /api/v1/equipment/guider/focus`, `GuideFocusStatusDto`). Wire format is
/// snake_case.
library;

abstract final class GuideFocusStates {
  static const idle = 'idle';
  static const running = 'running';
  static const stopped = 'stopped';
  /// The daemon's own stop once the median HFR held under the target.
  static const stopReasonInFocus = 'in_focus';
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

/// How far above the expected in-focus HFR still counts as in focus: seeing
/// wanders and the target assumes a typical night.
const double kGuideFocusTargetTolerance = 1.3;

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
  /// Why a stopped loop stopped: [GuideFocusStates.stopReasonInFocus] when
  /// the daemon ended it itself, null for a user stop.
  final String? stopReason;

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
    this.stopReason,
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
      stopReason: json['stop_reason'] is String ? json['stop_reason'] as String : null,
    );
  }
}

/// The in-focus verdict against the daemon's expected HFR (null target = no verdict).
extension GuideFocusVerdict on GuideFocusStatus {
  bool get hasTarget => expectedHfr != null && expectedHfr! > 0;

  /// [hfr] is at or under the target (× [kGuideFocusTargetTolerance]).
  bool hfrInFocus(double hfr) => hasTarget && hfr > 0 && hfr <= expectedHfr! * kGuideFocusTargetTolerance;

  /// The best HFR this session reached the target, or the daemon stopped the
  /// loop itself on a held in-focus reading: the guide-camera step is done.
  bool get focusedThisSession =>
      stopReason == GuideFocusStates.stopReasonInFocus || (bestHfr != null && hfrInFocus(bestHfr!));
}
