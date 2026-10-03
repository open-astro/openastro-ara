/// Client mirror of the daemon's autofocus run record (`GET /api/v1/autofocus/state`,
/// `AutofocusRunDto`): the probes for the V-curve, the fit, the final measured focus
/// and whether a rendered frame exists. Wire format is snake_case.
library;

/// Run states on the wire.
abstract final class AutofocusRunStates {
  static const idle = 'idle';
  static const running = 'running';
  static const complete = 'complete';
  static const failed = 'failed';
  static const cancelled = 'cancelled';
}

/// Probe phases on the wire.
abstract final class AutofocusProbePhases {
  static const coarse = 'coarse';
  static const fine = 'fine';
  static const smart = 'smart';
}

double? _num(Map<String, dynamic> json, String key) =>
    (json[key] as num?)?.toDouble();

int? _int(Map<String, dynamic> json, String key) => (json[key] as num?)?.toInt();

String? _str(Map<String, dynamic> json, String key) {
  final v = json[key];
  return v is String ? v : null;
}

DateTime? _time(Map<String, dynamic> json, String key) {
  final v = json[key];
  return v is String ? DateTime.tryParse(v) : null;
}

/// One focus probe. [kept] is false for a probe the sweep measured but dropped
/// (too few stars) — drawn hollow on the V-curve.
class AutofocusProbe {
  final int index;
  final String phase;
  final int position;
  final double hfr;
  final int stars;
  final bool kept;

  const AutofocusProbe({
    required this.index,
    required this.phase,
    required this.position,
    required this.hfr,
    required this.stars,
    required this.kept,
  });

  factory AutofocusProbe.fromJson(Map<String, dynamic> json) => AutofocusProbe(
        index: _int(json, 'index') ?? 0,
        phase: _str(json, 'phase') ?? AutofocusProbePhases.fine,
        position: _int(json, 'position') ?? 0,
        hfr: _num(json, 'hfr') ?? 0,
        stars: _int(json, 'stars') ?? 0,
        kept: json['kept'] as bool? ?? true,
      );
}

/// A sampled point of the fitted curve.
class AutofocusCurvePoint {
  final double position;
  final double hfr;
  const AutofocusCurvePoint(this.position, this.hfr);
}

/// The curve fit of the last sweep attempt.
class AutofocusFit {
  final String algorithm;
  final double rSquared;
  final double bestPosition;
  final double predictedHfr;
  final bool withinSampledRange;
  final List<AutofocusCurvePoint> curve;

  const AutofocusFit({
    required this.algorithm,
    required this.rSquared,
    required this.bestPosition,
    required this.predictedHfr,
    required this.withinSampledRange,
    required this.curve,
  });

  factory AutofocusFit.fromJson(Map<String, dynamic> json) {
    final raw = json['curve'];
    final curve = <AutofocusCurvePoint>[];
    if (raw is List) {
      for (final p in raw) {
        if (p is Map<String, dynamic>) {
          curve.add(AutofocusCurvePoint(
              _num(p, 'position') ?? 0, _num(p, 'hfr') ?? 0));
        }
      }
    }
    return AutofocusFit(
      algorithm: _str(json, 'algorithm') ?? 'parabolic',
      rSquared: _num(json, 'r_squared') ?? 0,
      bestPosition: _num(json, 'best_position') ?? 0,
      predictedHfr: _num(json, 'predicted_hfr') ?? 0,
      withinSampledRange: json['within_sampled_range'] as bool? ?? false,
      curve: curve,
    );
  }
}

/// The run snapshot.
class AutofocusRun {
  final String state;
  final String? mode;
  final String? phase;
  final String? trigger;
  final DateTime? startedUtc;
  final DateTime? completedUtc;
  final double? durationSeconds;
  final int? startPosition;
  final int? finalPosition;
  final double? finalHfr;
  final int? finalStars;
  final String? filter;
  final double? focuserTemperatureC;
  final int totalSteps;
  final int completedSteps;
  final int sweepAttempt;

  /// §59.8 — the fine sweep's step size and where it came from (`manual`,
  /// `measured`, `cfz` or `default`); null until a Classic sweep resolves it.
  final int? stepSize;
  final String? stepSizeSource;
  final List<AutofocusProbe> probes;
  final AutofocusFit? fit;
  final String? reason;
  final int? restoredPosition;
  final bool hasFrame;
  final int frameSeq;
  final int? framePosition;
  final double? frameHfr;

  const AutofocusRun({
    this.state = AutofocusRunStates.idle,
    this.mode,
    this.phase,
    this.trigger,
    this.startedUtc,
    this.completedUtc,
    this.durationSeconds,
    this.startPosition,
    this.finalPosition,
    this.finalHfr,
    this.finalStars,
    this.filter,
    this.focuserTemperatureC,
    this.totalSteps = 0,
    this.completedSteps = 0,
    this.sweepAttempt = 0,
    this.stepSize,
    this.stepSizeSource,
    this.probes = const [],
    this.fit,
    this.reason,
    this.restoredPosition,
    this.hasFrame = false,
    this.frameSeq = 0,
    this.framePosition,
    this.frameHfr,
  });

  static const idle = AutofocusRun();

  bool get isRunning => state == AutofocusRunStates.running;
  bool get isComplete => state == AutofocusRunStates.complete;
  bool get isFailed => state == AutofocusRunStates.failed;
  bool get isCancelled => state == AutofocusRunStates.cancelled;

  /// Probes that feed the V-curve (fine + smart), kept or not.
  List<AutofocusProbe> get sweepProbes => probes
      .where((p) => p.phase != AutofocusProbePhases.coarse)
      .toList(growable: false);

  List<AutofocusProbe> get coarseProbes => probes
      .where((p) => p.phase == AutofocusProbePhases.coarse)
      .toList(growable: false);

  int get keptCount => probes.where((p) => p.kept).length;

  factory AutofocusRun.fromJson(Map<String, dynamic> json) {
    final raw = json['probes'];
    final probes = <AutofocusProbe>[];
    if (raw is List) {
      for (final p in raw) {
        if (p is Map<String, dynamic>) probes.add(AutofocusProbe.fromJson(p));
      }
    }
    final fitJson = json['fit'];
    return AutofocusRun(
      state: _str(json, 'state') ?? AutofocusRunStates.idle,
      mode: _str(json, 'mode'),
      phase: _str(json, 'phase'),
      trigger: _str(json, 'trigger'),
      startedUtc: _time(json, 'started_utc'),
      completedUtc: _time(json, 'completed_utc'),
      durationSeconds: _num(json, 'duration_seconds'),
      startPosition: _int(json, 'start_position'),
      finalPosition: _int(json, 'final_position'),
      finalHfr: _num(json, 'final_hfr'),
      finalStars: _int(json, 'final_stars'),
      filter: _str(json, 'filter'),
      focuserTemperatureC: _num(json, 'focuser_temperature_c'),
      totalSteps: _int(json, 'total_steps') ?? 0,
      completedSteps: _int(json, 'completed_steps') ?? 0,
      sweepAttempt: _int(json, 'sweep_attempt') ?? 0,
      stepSize: _int(json, 'step_size'),
      stepSizeSource: _str(json, 'step_size_source'),
      probes: probes,
      fit: fitJson is Map<String, dynamic> ? AutofocusFit.fromJson(fitJson) : null,
      reason: _str(json, 'reason'),
      restoredPosition: _int(json, 'restored_position'),
      hasFrame: json['has_frame'] as bool? ?? false,
      frameSeq: _int(json, 'frame_seq') ?? 0,
      framePosition: _int(json, 'frame_position'),
      frameHfr: _num(json, 'frame_hfr'),
    );
  }
}
