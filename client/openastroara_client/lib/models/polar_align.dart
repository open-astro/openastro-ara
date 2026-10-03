/// §45 polar-alignment wire models — mirror the server's `PolarAlignStateDto`
/// (`/api/v1/equipment/polaralign/status`) and `PolarAlignSettingsDto`
/// (`/api/v1/profile/polar-align`). Snake_case on the wire.
library;

/// Server routine states (`PolarAlignStateDto.state`).
abstract final class PolarAlignStates {
  static const idle = 'idle';
  static const seeding = 'seeding';
  static const adjusting = 'adjusting';
  static const paused = 'paused';
  static const stopped = 'stopped';
  static const failed = 'failed';
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

  const PolarAlignStatus({
    required this.state,
    this.currentErrorArcmin,
    this.azimuthAdjustmentArcmin,
    this.altitudeAdjustmentArcmin,
    this.framesCaptured = 0,
    this.lastFrameId,
    this.waitingForCapture = false,
    this.lastFrame,
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
      );

  /// True while the server is running any phase of the routine.
  bool get isActive =>
      state == PolarAlignStates.seeding ||
      state == PolarAlignStates.adjusting ||
      state == PolarAlignStates.paused;
}

/// §45.12 profile section. Field defaults are the playbook defaults, matching
/// the server's ctor defaults for a missing key.
class PolarAlignSettings {
  final double exposureSeconds;
  final int binning;
  final double targetToleranceArcmin;
  final double seedRotationDeg;
  final int loopCadenceMs;
  final double settleSeconds;

  /// [PolarAlignLoopModes.loop] or [PolarAlignLoopModes.single].
  final String loopMode;

  const PolarAlignSettings({
    this.exposureSeconds = 1.0,
    this.binning = 1,
    this.targetToleranceArcmin = 1.0,
    this.seedRotationDeg = 30.0,
    this.loopCadenceMs = 1000,
    this.settleSeconds = 2.0,
    this.loopMode = PolarAlignLoopModes.loop,
  });

  factory PolarAlignSettings.fromJson(Map<String, dynamic> json) =>
      PolarAlignSettings(
        exposureSeconds: (json['exposure_seconds'] as num?)?.toDouble() ?? 1.0,
        binning: (json['binning'] as num?)?.toInt() ?? 1,
        targetToleranceArcmin:
            (json['target_tolerance_arcmin'] as num?)?.toDouble() ?? 1.0,
        seedRotationDeg: (json['seed_rotation_deg'] as num?)?.toDouble() ?? 30.0,
        loopCadenceMs: (json['loop_cadence_ms'] as num?)?.toInt() ?? 1000,
        settleSeconds: (json['settle_seconds'] as num?)?.toDouble() ?? 2.0,
        loopMode: json['loop_mode'] == PolarAlignLoopModes.single
            ? PolarAlignLoopModes.single
            : PolarAlignLoopModes.loop,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
        'exposure_seconds': exposureSeconds,
        'binning': binning,
        'target_tolerance_arcmin': targetToleranceArcmin,
        'seed_rotation_deg': seedRotationDeg,
        'loop_cadence_ms': loopCadenceMs,
        'settle_seconds': settleSeconds,
        'loop_mode': loopMode,
      };

  PolarAlignSettings copyWith({
    double? exposureSeconds,
    int? binning,
    double? targetToleranceArcmin,
    double? seedRotationDeg,
    int? loopCadenceMs,
    double? settleSeconds,
    String? loopMode,
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
      );
}
