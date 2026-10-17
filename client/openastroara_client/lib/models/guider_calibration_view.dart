/// #1364 — the guider's calibration as Ara shows it
/// (`GET /api/v1/equipment/guider/live/calibration`): this session's run
/// (state, the steps PHD2's plot draws, the failure reason), the stored result
/// and Ara's verdict on it.
class GuiderCalibrationView {
  final bool connected;

  /// none | running | complete | failed.
  final String runState;
  final List<GuiderCalibrationStep> steps;
  final String? error;
  final GuiderCalibrationData? data;

  /// good | check | redo.
  final String verdict;
  final String? verdictReason;

  /// The Dec backlash this session's calibration measured: PHD2's
  /// backlash-clearing pulses before the North leg, each one calibration step
  /// long. Null until a run with clearing legs completed and the guider told
  /// Ara its step length.
  final int? backlashClearingSteps;
  final int? calibrationStepMs;
  final int? estimatedDecBacklashMs;

  const GuiderCalibrationView({
    this.connected = false,
    this.runState = 'none',
    this.steps = const [],
    this.error,
    this.data,
    this.verdict = 'redo',
    this.verdictReason,
    this.backlashClearingSteps,
    this.calibrationStepMs,
    this.estimatedDecBacklashMs,
  });

  static const disconnected = GuiderCalibrationView();

  bool get isRunning => runState == 'running';
  bool get hasSteps => steps.isNotEmpty;

  factory GuiderCalibrationView.fromJson(Map<String, dynamic> json) {
    final raw = json['steps'];
    final data = json['data'];
    return GuiderCalibrationView(
      connected: json['connected'] as bool? ?? false,
      runState: json['run_state'] is String
          ? json['run_state'] as String
          : 'none',
      steps: [
        if (raw is List)
          for (final s in raw)
            if (s is Map<String, dynamic>) GuiderCalibrationStep.fromJson(s),
      ],
      error: json['error'] is String ? json['error'] as String : null,
      data: data is Map<String, dynamic>
          ? GuiderCalibrationData.fromJson(data)
          : null,
      verdict: json['verdict'] is String ? json['verdict'] as String : 'redo',
      backlashClearingSteps: (json['backlash_clearing_steps'] as num?)?.toInt(),
      calibrationStepMs: (json['calibration_step_ms'] as num?)?.toInt(),
      estimatedDecBacklashMs: (json['estimated_dec_backlash_ms'] as num?)?.toInt(),
      verdictReason: json['verdict_reason'] is String
          ? json['verdict_reason'] as String
          : null,
    );
  }
}

/// One calibration step: the leg, its number, and the star's displacement from
/// the leg's start in guide pixels.
/// "Dec backlash ≈ 1.5 s (6 clearing steps × 250 ms)", "no measurable Dec
/// backlash" (zero clearing steps), or null when the run has not told us yet.
String? describeDecBacklash(GuiderCalibrationView view) {
  final steps = view.backlashClearingSteps;
  if (steps == null) return null;
  if (steps == 0) return 'No measurable Dec backlash in this calibration';
  final ms = view.estimatedDecBacklashMs;
  final stepMs = view.calibrationStepMs;
  if (ms == null || stepMs == null) {
    return 'Dec backlash: $steps clearing step${steps == 1 ? '' : 's'} (step length unknown)';
  }
  final seconds = (ms / 1000).toStringAsFixed(ms >= 1000 ? 1 : 2);
  return 'Dec backlash ≈ $seconds s ($steps clearing step${steps == 1 ? '' : 's'} × $stepMs ms)';
}

class GuiderCalibrationStep {
  final String direction;
  final int step;
  final double dx;
  final double dy;
  final double dist;
  const GuiderCalibrationStep({
    required this.direction,
    required this.step,
    required this.dx,
    required this.dy,
    required this.dist,
  });

  /// West/East legs (and their backlash-clearing legs) are the RA axis.
  bool get isRa =>
      direction.toLowerCase().contains('west') ||
      direction.toLowerCase().contains('east');

  factory GuiderCalibrationStep.fromJson(Map<String, dynamic> json) =>
      GuiderCalibrationStep(
        direction: json['direction'] is String
            ? json['direction'] as String
            : '',
        step: (json['step'] as num?)?.toInt() ?? 0,
        dx: (json['dx'] as num?)?.toDouble() ?? 0,
        dy: (json['dy'] as num?)?.toDouble() ?? 0,
        dist: (json['dist'] as num?)?.toDouble() ?? 0,
      );
}

/// The stored calibration: axis angles (°), rates (px/s), parities, the
/// declination it was taken at and the orthogonality error (° from square).
class GuiderCalibrationData {
  final bool calibrated;
  final double? xAngleDeg;
  final double? yAngleDeg;
  final double? xRatePxPerSec;
  final double? yRatePxPerSec;
  final String? xParity;
  final String? yParity;
  final double? declinationDeg;
  final double? orthogonalityErrorDeg;
  const GuiderCalibrationData({
    this.calibrated = false,
    this.xAngleDeg,
    this.yAngleDeg,
    this.xRatePxPerSec,
    this.yRatePxPerSec,
    this.xParity,
    this.yParity,
    this.declinationDeg,
    this.orthogonalityErrorDeg,
  });

  factory GuiderCalibrationData.fromJson(Map<String, dynamic> json) =>
      GuiderCalibrationData(
        calibrated: json['calibrated'] as bool? ?? false,
        xAngleDeg: (json['x_angle_deg'] as num?)?.toDouble(),
        yAngleDeg: (json['y_angle_deg'] as num?)?.toDouble(),
        xRatePxPerSec: (json['x_rate_px_per_sec'] as num?)?.toDouble(),
        yRatePxPerSec: (json['y_rate_px_per_sec'] as num?)?.toDouble(),
        xParity: json['x_parity'] is String ? json['x_parity'] as String : null,
        yParity: json['y_parity'] is String ? json['y_parity'] as String : null,
        declinationDeg: (json['declination_deg'] as num?)?.toDouble(),
        orthogonalityErrorDeg: (json['orthogonality_error_deg'] as num?)
            ?.toDouble(),
      );
}
