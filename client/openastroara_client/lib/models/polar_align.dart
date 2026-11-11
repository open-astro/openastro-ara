/// §45 polar-alignment wire models — mirror the server's `PolarAlignStateDto`
/// (`/api/v1/equipment/polaralign/status`) and `PolarAlignSettingsDto`
/// (`/api/v1/profile/polar-align`). Snake_case on the wire.
library;

/// Server routine states (`PolarAlignStateDto.state`).
abstract final class PolarAlignStates {
  static const idle = 'idle';

  /// Find Home running, or (with `waiting_for_home`) waiting for the user to
  /// put the mount at Home and tap Continue.
  static const homing = 'homing';
  static const seeding = 'seeding';
  static const adjusting = 'adjusting';
  static const paused = 'paused';

  /// The check stage's three solves are running.
  static const checking = 'checking';

  /// Terminal: the three-point check has its result (`check_result`).
  static const checked = 'checked';
  static const stopped = 'stopped';
  static const failed = 'failed';

  /// True while the server runs any part of the routine.
  static bool isActive(String state) =>
      state == homing ||
      state == seeding ||
      state == adjusting ||
      state == paused ||
      state == checking;
}

/// The routine's stages (`PolarAlignStateDto.stage`): coarse (Home, then the
/// seed turn + live loop on the main camera), fine (the same on the guide
/// camera), check (Home, three guide-camera solves, an exact fit).
abstract final class PolarAlignStages {
  static const coarse = 'coarse';
  static const fine = 'fine';
  static const check = 'check';

  static const all = [coarse, fine, check];

  /// 0-based position of [stage] in the routine, or null.
  static int? indexOf(String? stage) {
    final i = all.indexOf(stage ?? '');
    return i < 0 ? null : i;
  }
}

/// The check stage's result (`check_result` on the status and the
/// `polar_align.checked` payload).
class PolarAlignCheckResult {
  final double altErrorArcmin;
  final double azErrorArcmin;
  final double totalErrorArcmin;

  /// How far off the axis the three pointings sat (conditioning).
  final double coneHalfAngleDeg;

  /// How far the solved turns disagreed with what the mount said it turned.
  final double turnMismatchDeg;

  /// The fine stage's last readout (its total), and the distance between the
  /// check's axis and the fine stage's — alt and az both, so a readout of the
  /// same size on the other side of the pole still shows as a disagreement;
  /// null when the run began at the check.
  final double? fineErrorArcmin;
  final double? fineDeltaArcmin;

  /// `excellent` | `very_good` | `good` | `rough` | `far_off`.
  final String verdict;

  const PolarAlignCheckResult({
    required this.altErrorArcmin,
    required this.azErrorArcmin,
    required this.totalErrorArcmin,
    this.coneHalfAngleDeg = 0,
    this.turnMismatchDeg = 0,
    this.fineErrorArcmin,
    this.fineDeltaArcmin,
    this.verdict = '',
  });

  factory PolarAlignCheckResult.fromJson(Map<String, dynamic> json) =>
      PolarAlignCheckResult(
        altErrorArcmin: (json['altitude_error_arcmin'] as num?)?.toDouble() ?? 0,
        azErrorArcmin: (json['azimuth_error_arcmin'] as num?)?.toDouble() ?? 0,
        totalErrorArcmin: (json['total_error_arcmin'] as num?)?.toDouble() ?? 0,
        coneHalfAngleDeg: (json['cone_half_angle_deg'] as num?)?.toDouble() ?? 0,
        turnMismatchDeg: (json['turn_mismatch_deg'] as num?)?.toDouble() ?? 0,
        fineErrorArcmin: (json['fine_error_arcmin'] as num?)?.toDouble(),
        fineDeltaArcmin: (json['fine_delta_arcmin'] as num?)?.toDouble(),
        verdict: json['verdict'] is String ? json['verdict'] as String : '',
      );
}

/// The last routine to end with an error reading (`last_result` on the status):
/// the daemon keeps it for its boot, so a relaunched client can tell whether
/// the mount was aligned tonight.
class PolarAlignLastResult {
  final double errorArcmin;
  final DateTime completedUtc;
  const PolarAlignLastResult({required this.errorArcmin, required this.completedUtc});

  static PolarAlignLastResult? tryParse(Object? json) {
    if (json is! Map) return null;
    final e = json['error_arcmin'];
    final t = json['completed_utc'];
    final at = t is String ? DateTime.tryParse(t) : null;
    return e is num && at != null ? PolarAlignLastResult(errorArcmin: e.toDouble(), completedUtc: at) : null;
  }
}

/// Live-adjust capture modes (`PolarAlignSettingsDto.loop_mode`).
abstract final class PolarAlignLoopModes {
  /// Re-shoot continuously.
  static const loop = 'loop';

  /// Wait for `POST /api/v1/equipment/polaralign/capture` before each frame.
  static const single = 'single';
}

/// One polar-align frame: its exposure, how long the capture (exposure +
/// download) and the plate solve took, and the solved pointing (apparent
/// of date, degrees) when it solved. Parsed from the status snapshot's
/// `last_frame` and from the `polar_align.frame_complete` payload.
class PolarAlignFrameInfo {
  final String frameId;
  final bool solved;
  final double exposureSeconds;
  final int captureMs;
  final int? solveMs;
  final double? raDeg;
  final double? decDeg;

  const PolarAlignFrameInfo({
    required this.frameId,
    required this.solved,
    required this.exposureSeconds,
    required this.captureMs,
    this.solveMs,
    this.raDeg,
    this.decDeg,
  });

  factory PolarAlignFrameInfo.fromJson(Map<String, dynamic> json) =>
      PolarAlignFrameInfo(
        frameId: json['frame_id'] is String ? json['frame_id'] as String : '',
        solved: json['solved'] == true,
        exposureSeconds: (json['exposure_seconds'] as num?)?.toDouble() ?? 0,
        captureMs: (json['capture_ms'] as num?)?.toInt() ?? 0,
        solveMs: (json['solve_ms'] as num?)?.toInt(),
        raDeg: (json['ra_deg'] as num?)?.toDouble(),
        decDeg: (json['dec_deg'] as num?)?.toDouble(),
      );
}

/// Snapshot of the routine (`GET /api/v1/equipment/polaralign/status`).
/// Error fields are null until the live-adjust loop has produced its first
/// solve (and always null for `idle`/`stopped`).
class PolarAlignStatus {
  final String state;
  final double? currentErrorArcmin;
  final double? azimuthAdjustmentArcmin;
  final double? altitudeAdjustmentArcmin;
  final int framesCaptured;
  final String? lastFrameId;

  /// Single-frame mode: the adjust loop is parked until a capture request.
  final bool waitingForCapture;
  final PolarAlignFrameInfo? lastFrame;

  /// [PolarAlignStages]; null when idle before any run.
  final String? stage;

  /// The mount cannot find Home itself: the routine waits for Continue.
  final bool waitingForHome;

  /// Check stage: solves done so far (0–3).
  final int checkSolvesDone;
  final PolarAlignCheckResult? checkResult;

  /// #1565 — null from an older daemon or before any routine ended.
  final PolarAlignLastResult? lastResult;

  const PolarAlignStatus({
    required this.state,
    this.currentErrorArcmin,
    this.azimuthAdjustmentArcmin,
    this.altitudeAdjustmentArcmin,
    this.framesCaptured = 0,
    this.lastFrameId,
    this.waitingForCapture = false,
    this.lastFrame,
    this.stage,
    this.waitingForHome = false,
    this.checkSolvesDone = 0,
    this.checkResult,
    this.lastResult,
  });

  factory PolarAlignStatus.fromJson(Map<String, dynamic> json) =>
      PolarAlignStatus(
        state: json['state'] is String ? json['state'] as String : PolarAlignStates.idle,
        currentErrorArcmin: (json['current_error_arcmin'] as num?)?.toDouble(),
        azimuthAdjustmentArcmin: (json['azimuth_adjustment_arcmin'] as num?)?.toDouble(),
        altitudeAdjustmentArcmin: (json['altitude_adjustment_arcmin'] as num?)?.toDouble(),
        framesCaptured: (json['frames_captured'] as num?)?.toInt() ?? 0,
        lastFrameId: json['last_frame_id'] as String?,
        waitingForCapture: json['waiting_for_capture'] == true,
        lastFrame: json['last_frame'] is Map<String, dynamic>
            ? PolarAlignFrameInfo.fromJson(json['last_frame'] as Map<String, dynamic>)
            : null,
        stage: json['stage'] is String ? json['stage'] as String : null,
        waitingForHome: json['waiting_for_home'] == true,
        checkSolvesDone: (json['check_solves_done'] as num?)?.toInt() ?? 0,
        checkResult: json['check_result'] is Map<String, dynamic>
            ? PolarAlignCheckResult.fromJson(json['check_result'] as Map<String, dynamic>)
            : null,
        lastResult: PolarAlignLastResult.tryParse(json['last_result']),
      );

  /// True while the server is running any phase of the routine.
  bool get isActive => PolarAlignStates.isActive(state);
}

/// §45.12 profile section. Field defaults are the playbook defaults, matching
/// the server's ctor defaults for a missing key.
class PolarAlignSettings {
  final double exposureSeconds;

  /// 0 = auto (§45.4: the server picks the tier for the guide camera's
  /// sensor), 1–4 = a fixed override.
  final int binning;
  final double targetToleranceArcmin;
  final double seedRotationDeg;
  final int loopCadenceMs;
  final double settleSeconds;

  /// [PolarAlignLoopModes.loop] or [PolarAlignLoopModes.single].
  final String loopMode;

  /// Check stage: each of the two RA turns between the three solves.
  final double checkStepDeg;

  /// Coarse stage: the main-camera exposure.
  final double coarseExposureSeconds;

  /// Coarse and check stages send the mount Home first.
  final bool useHome;

  const PolarAlignSettings({
    this.exposureSeconds = 1.0,
    this.binning = 0,
    this.targetToleranceArcmin = 1.0,
    this.seedRotationDeg = 60.0,
    this.loopCadenceMs = 1000,
    this.settleSeconds = 2.0,
    this.loopMode = PolarAlignLoopModes.loop,
    this.checkStepDeg = 30.0,
    this.coarseExposureSeconds = 3.0,
    this.useHome = true,
  });

  factory PolarAlignSettings.fromJson(Map<String, dynamic> json) =>
      PolarAlignSettings(
        exposureSeconds: (json['exposure_seconds'] as num?)?.toDouble() ?? 1.0,
        binning: (json['binning'] as num?)?.toInt() ?? 0,
        targetToleranceArcmin:
            (json['target_tolerance_arcmin'] as num?)?.toDouble() ?? 1.0,
        seedRotationDeg: (json['seed_rotation_deg'] as num?)?.toDouble() ?? 60.0,
        loopCadenceMs: (json['loop_cadence_ms'] as num?)?.toInt() ?? 1000,
        settleSeconds: (json['settle_seconds'] as num?)?.toDouble() ?? 2.0,
        loopMode: json['loop_mode'] == PolarAlignLoopModes.single
            ? PolarAlignLoopModes.single
            : PolarAlignLoopModes.loop,
        checkStepDeg: (json['check_step_deg'] as num?)?.toDouble() ?? 30.0,
        coarseExposureSeconds:
            (json['coarse_exposure_seconds'] as num?)?.toDouble() ?? 3.0,
        useHome: json['use_home'] != false,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
        'exposure_seconds': exposureSeconds,
        'binning': binning,
        'target_tolerance_arcmin': targetToleranceArcmin,
        'seed_rotation_deg': seedRotationDeg,
        'loop_cadence_ms': loopCadenceMs,
        'settle_seconds': settleSeconds,
        'loop_mode': loopMode,
        'check_step_deg': checkStepDeg,
        'coarse_exposure_seconds': coarseExposureSeconds,
        'use_home': useHome,
      };

  PolarAlignSettings copyWith({
    double? exposureSeconds,
    int? binning,
    double? targetToleranceArcmin,
    double? seedRotationDeg,
    int? loopCadenceMs,
    double? settleSeconds,
    String? loopMode,
    double? checkStepDeg,
    double? coarseExposureSeconds,
    bool? useHome,
  }) =>
      PolarAlignSettings(
        exposureSeconds: exposureSeconds ?? this.exposureSeconds,
        binning: binning ?? this.binning,
        targetToleranceArcmin:
            targetToleranceArcmin ?? this.targetToleranceArcmin,
        seedRotationDeg: seedRotationDeg ?? this.seedRotationDeg,
        loopCadenceMs: loopCadenceMs ?? this.loopCadenceMs,
        settleSeconds: settleSeconds ?? this.settleSeconds,
        loopMode: loopMode ?? this.loopMode,
        checkStepDeg: checkStepDeg ?? this.checkStepDeg,
        coarseExposureSeconds:
            coarseExposureSeconds ?? this.coarseExposureSeconds,
        useHome: useHome ?? this.useHome,
      );
}
