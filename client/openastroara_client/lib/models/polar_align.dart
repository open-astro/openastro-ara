/// §45 polar-alignment wire models — mirror the server's `PolarAlignStateDto`
/// (`/api/v1/equipment/polaralign/status`) and `PolarAlignSettingsDto`
/// (`/api/v1/profile/polar-align`). Snake_case on the wire.
library;

/// Server routine states (`PolarAlignStateDto.state`).
abstract final class PolarAlignStates {
  static const idle = 'idle';

  /// The mount is going Home: after Done, before and after the verify solves.
  static const homing = 'homing';
  static const seeding = 'seeding';
  static const adjusting = 'adjusting';
  static const paused = 'paused';

  /// The verify stage's three solves are running.
  static const checking = 'checking';

  /// Terminal: the run is verified (`check_result`) and the mount is back Home.
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

/// #2072 `readout_caution` tokens (`PolarAlignStateDto.readout_caution`, the
/// `polar_align.progress` payload).
abstract final class PolarAlignReadoutCautions {
  /// The camera sits nearly due east or west of the pole: the live readout
  /// reads the altitude bolt poorly, so the verdict after Done is the number
  /// to trust.
  static const altitudeWeak = 'altitude_weak';
}

/// #2082 the camera a measurement uses (`polar_align.camera`, the status's
/// `camera`).
abstract final class PolarAlignCameras {
  /// Through the guider, with the guide optics (the default).
  static const guide = 'guide';

  /// The main camera with its own solver, at the main-camera exposure.
  static const main = 'main';
}

/// The two halves of a run (`PolarAlignStateDto.stage`): align (the
/// three-point measurement from Home, then the live adjust loop) and verify
/// (from Done: Home, three solves around two RA turns, Home again). Both use
/// the profile's `polar_align.camera`.
abstract final class PolarAlignStages {
  static const align = 'align';
  static const verify = 'verify';
}

/// The verify stage's result (`check_result` on the status, on
/// `polar_align.stage` once known, and the `polar_align.checked` payload).
class PolarAlignCheckResult {
  final double altErrorArcmin;
  final double azErrorArcmin;
  final double totalErrorArcmin;

  /// `excellent` | `very_good` | `good` | `rough` | `far_off`.
  final String verdict;

  /// #2082 a point of the measurement stayed scattered over 3″ after its retake.
  final bool noisy;

  /// #2072 the fit's 1σ on the total error (arcmin); null from an older daemon.
  final double? totalSigmaArcmin;

  const PolarAlignCheckResult({
    required this.altErrorArcmin,
    required this.azErrorArcmin,
    required this.totalErrorArcmin,
    this.verdict = '',
    this.noisy = false,
    this.totalSigmaArcmin,
  });

  factory PolarAlignCheckResult.fromJson(Map<String, dynamic> json) =>
      PolarAlignCheckResult(
        altErrorArcmin: (json['altitude_error_arcmin'] as num?)?.toDouble() ?? 0,
        azErrorArcmin: (json['azimuth_error_arcmin'] as num?)?.toDouble() ?? 0,
        totalErrorArcmin: (json['total_error_arcmin'] as num?)?.toDouble() ?? 0,
        verdict: json['verdict'] is String ? json['verdict'] as String : '',
        noisy: json['noisy'] == true,
        totalSigmaArcmin: (json['total_sigma_arcmin'] as num?)?.toDouble(),
      );
}

/// The last routine to end with an error reading
/// (`last_result` on the status): the daemon keeps it for its boot, so a
/// relaunched client can tell whether the mount was aligned tonight.
class PolarAlignLastResult {
  final double errorArcmin;
  final DateTime completedUtc;
  const PolarAlignLastResult({
    required this.errorArcmin,
    required this.completedUtc,
  });

  static PolarAlignLastResult? tryParse(Object? json) {
    if (json is! Map) return null;
    final e = json['error_arcmin'];
    final t = json['completed_utc'];
    final at = t is String ? DateTime.tryParse(t) : null;
    return e is num && at != null
        ? PolarAlignLastResult(errorArcmin: e.toDouble(), completedUtc: at)
        : null;
  }
}

/// #1971 the Polar Aligner's run states (`PolarAlignCorrectionDto.state`).
abstract final class PolarAlignerStates {
  static const manual = 'manual';
  static const probing = 'probing';
  static const moving = 'moving';
  static const settling = 'settling';
  static const measuring = 'measuring';
  static const converged = 'converged';
  static const stopped = 'stopped';
}

/// Why the motors stopped (`PolarAlignCorrectionDto.cause`), as the daemon's
/// `PolarAlignCorrectionCauses` declares them.
abstract final class PolarAlignerCauses {
  static const wrongDirection = 'wrong_direction';
  static const wrongScale = 'wrong_scale';
  static const deviceFault = 'device_fault';
  static const notCalibrated = 'not_calibrated';
  static const deviceRefused = 'device_refused';
  static const diverging = 'diverging';
  static const timeout = 'timeout';
  static const linkLost = 'link_lost';
  static const mountTracking = 'mount_tracking';
  static const mountSlewing = 'mount_slewing';
  static const stoppedByUser = 'stopped_by_user';
  // #2069 (ARA-19): the verified stop and the run's caps.
  static const stopNotConfirmed = 'stop_not_confirmed';
  // A Stop owed from before the run, confirmed later in it: the run waits for Resume.
  static const stopConfirmed = 'stop_confirmed';
  static const travelLimit = 'travel_limit';
  static const moveLimit = 'move_limit';

  static const all = [
    wrongDirection, wrongScale, deviceFault, notCalibrated, deviceRefused, diverging,
    timeout, linkLost, mountTracking, mountSlewing, stoppedByUser,
    stopNotConfirmed, stopConfirmed, travelLimit, moveLimit,
  ];
}

/// The Polar Aligner in a running routine: `correction` on the status and
/// the `polar_align.correction` payload. [arcmin] is signed as sent (+ azimuth
/// = clockwise seen from above, + altitude = raise).
class PolarAlignerRun {
  final String state;
  final int moves;

  /// `azimuth` | `altitude`, for a move or the axis to blame.
  final String? axis;
  final double? arcmin;
  final double? seconds;
  final String? cause;
  final String? deviceText;

  const PolarAlignerRun({
    required this.state,
    this.moves = 0,
    this.axis,
    this.arcmin,
    this.seconds,
    this.cause,
    this.deviceText,
  });

  /// Null for anything that is not an object with a state.
  static PolarAlignerRun? tryParse(Object? json) {
    if (json is! Map || json['state'] is! String) return null;
    String? s(String k) => json[k] is String ? json[k] as String : null;
    return PolarAlignerRun(
      state: json['state'] as String,
      moves: (json['moves'] as num?)?.toInt() ?? 0,
      axis: s('axis'),
      arcmin: (json['arcmin'] as num?)?.toDouble(),
      seconds: (json['seconds'] as num?)?.toDouble(),
      cause: s('cause'),
      deviceText: s('device_text'),
    );
  }

  /// The motors are working: a move, its settle or the measurement after it.
  bool get motorsBusy =>
      state == PolarAlignerStates.probing ||
      state == PolarAlignerStates.moving ||
      state == PolarAlignerStates.settling ||
      state == PolarAlignerStates.measuring;
}

/// The rig's Polar Aligner (`GET /api/v1/equipment/polaraligner`), derived
/// by the daemon: there is no stored choice. A null position is a device that
/// does not report it, never 0. Capabilities and faults are not read: nothing
/// in the app shows them.
class PolarAligner {
  final bool present;

  /// `mount` | `switch`.
  final String? carrier;
  final String? name;
  final bool drivable;

  /// `device_runs_its_own_loop` | `info_unreadable`.
  final String? notDrivableReason;
  final bool moving;
  final double? azimuthPositionArcmin;
  final double? altitudePositionArcmin;

  const PolarAligner({
    this.present = false,
    this.carrier,
    this.name,
    this.drivable = false,
    this.notDrivableReason,
    this.moving = false,
    this.azimuthPositionArcmin,
    this.altitudePositionArcmin,
  });

  factory PolarAligner.fromJson(Map<String, dynamic> json) => PolarAligner(
        present: json['present'] == true,
        carrier: json['carrier'] is String ? json['carrier'] as String : null,
        name: json['name'] is String ? json['name'] as String : null,
        drivable: json['drivable'] == true,
        notDrivableReason: json['not_drivable_reason'] is String ? json['not_drivable_reason'] as String : null,
        moving: json['moving'] == true,
        azimuthPositionArcmin: (json['azimuth_position_arcmin'] as num?)?.toDouble(),
        altitudePositionArcmin: (json['altitude_position_arcmin'] as num?)?.toDouble(),
      );
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

  /// Verify stage: solves done so far (0–3).
  final int checkSolvesDone;

  /// Non-null as soon as the verify verdict exists: already while the mount
  /// returns Home, before `checked`.
  final PolarAlignCheckResult? checkResult;

  /// #1565 — null from an older daemon or before any routine ended.
  final PolarAlignLastResult? lastResult;

  /// #1971 null without a Polar Aligner Ara drives, or before its first measurement.
  final PolarAlignerRun? correction;

  /// #2072 [PolarAlignReadoutCautions]; null when the readout is sound or
  /// from an older daemon.
  final String? readoutCaution;

  /// #2082 the run's camera ([PolarAlignCameras]); null before a run or from
  /// an older daemon.
  final String? camera;

  /// #2082 a point of the measurement stayed scattered over 3″ after its
  /// retake: the readout rests on noisy solves.
  final bool measurementNoisy;

  /// #2072 the 1σ of [currentErrorArcmin] (arcmin), from the fit the readout
  /// rests on; null with no error and from an older daemon.
  final double? currentSigmaArcmin;

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
    this.checkSolvesDone = 0,
    this.checkResult,
    this.lastResult,
    this.correction,
    this.readoutCaution,
    this.camera,
    this.measurementNoisy = false,
    this.currentSigmaArcmin,
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
        checkSolvesDone: (json['check_solves_done'] as num?)?.toInt() ?? 0,
        checkResult: json['check_result'] is Map<String, dynamic>
            ? PolarAlignCheckResult.fromJson(json['check_result'] as Map<String, dynamic>)
            : null,
        lastResult: PolarAlignLastResult.tryParse(json['last_result']),
        correction: PolarAlignerRun.tryParse(json['correction']),
        readoutCaution: json['readout_caution'] is String ? json['readout_caution'] as String : null,
        camera: json['camera'] is String ? json['camera'] as String : null,
        measurementNoisy: json['measurement_noisy'] == true,
        currentSigmaArcmin: (json['current_sigma_arcmin'] as num?)?.toDouble(),
      );

  /// True while the server is running any phase of the routine.
  bool get isActive => PolarAlignStates.isActive(state);
}

/// §45.12 profile section. No field has a client default (ADR 0015 rule 3):
/// the daemon's section table owns them, and the client holds the rig's copy
/// or nothing (`polarAlignSettingsProvider`).
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

  /// The main camera's exposure, when [camera] is the main camera.
  final double coarseExposureSeconds;

  /// #1971 the routine turns the Polar Aligner's motors (off: by hand).
  final bool polarAlignerAuto;

  /// #2082 the camera of the measurement, its live loop and the verify
  /// solves ([PolarAlignCameras]).
  final String camera;

  /// #2082 frames averaged at each of a measurement's three points (1–5).
  final int framesPerPoint;

  /// #2081 the way a run turns the RA axis (NINA's EastDirection): true
  /// east, false west, null by the site's hemisphere (east in the north, west
  /// in the south).
  final bool? eastDirection;

  const PolarAlignSettings({
    required this.exposureSeconds,
    required this.binning,
    required this.targetToleranceArcmin,
    required this.seedRotationDeg,
    required this.loopCadenceMs,
    required this.settleSeconds,
    required this.loopMode,
    required this.coarseExposureSeconds,
    required this.polarAlignerAuto,
    required this.camera,
    required this.framesPerPoint,
    required this.eastDirection,
  });

  Map<String, dynamic> toJson() => <String, dynamic>{
        'exposure_seconds': exposureSeconds,
        'binning': binning,
        'target_tolerance_arcmin': targetToleranceArcmin,
        'seed_rotation_deg': seedRotationDeg,
        'loop_cadence_ms': loopCadenceMs,
        'settle_seconds': settleSeconds,
        'loop_mode': loopMode,
        'coarse_exposure_seconds': coarseExposureSeconds,
        'polar_aligner_auto': polarAlignerAuto,
        'camera': camera,
        'frames_per_point': framesPerPoint,
        'east_direction': eastDirection,
      };

  PolarAlignSettings copyWith({
    double? exposureSeconds,
    int? binning,
    double? targetToleranceArcmin,
    double? seedRotationDeg,
    int? loopCadenceMs,
    double? settleSeconds,
    String? loopMode,
    double? coarseExposureSeconds,
    bool? polarAlignerAuto,
    String? camera,
    int? framesPerPoint,
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
        coarseExposureSeconds:
            coarseExposureSeconds ?? this.coarseExposureSeconds,
        polarAlignerAuto: polarAlignerAuto ?? this.polarAlignerAuto,
        camera: camera ?? this.camera,
        framesPerPoint: framesPerPoint ?? this.framesPerPoint,
        eastDirection: eastDirection,
      );

  /// This copy with [eastDirection] set, null (by hemisphere) included:
  /// [copyWith] cannot tell null from "unchanged".
  PolarAlignSettings withEastDirection(bool? value) => PolarAlignSettings(
        exposureSeconds: exposureSeconds,
        binning: binning,
        targetToleranceArcmin: targetToleranceArcmin,
        seedRotationDeg: seedRotationDeg,
        loopCadenceMs: loopCadenceMs,
        settleSeconds: settleSeconds,
        loopMode: loopMode,
        coarseExposureSeconds: coarseExposureSeconds,
        polarAlignerAuto: polarAlignerAuto,
        camera: camera,
        framesPerPoint: framesPerPoint,
        eastDirection: value,
      );
}
