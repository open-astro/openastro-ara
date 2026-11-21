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

/// #2072 `readout_caution` tokens (`PolarAlignStateDto.readout_caution`, the
/// `polar_align.progress` payload).
abstract final class PolarAlignReadoutCautions {
  /// The camera sits nearly due east or west of the pole: the live readout
  /// reads the altitude bolt poorly, so the Check is the number to trust.
  static const altitudeWeak = 'altitude_weak';
}

/// #2082 the camera a measurement uses (`polar_align.camera`, the start
/// plan's and the status's `camera`).
abstract final class PolarAlignCameras {
  /// Through the guider, with the guide optics (the default).
  static const guide = 'guide';

  /// The main camera with its own solver, at the main-camera exposure.
  static const main = 'main';
}

/// The routine's stages (`PolarAlignStateDto.stage`): coarse (Home, then the
/// three-point seed — two RA-motor turns — + live loop on the main camera), fine (the same on the guide
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

  /// #2082 a point of the check stayed scattered over 3″ after its retake.
  final bool noisy;

  /// #2072 the fit's 1σ on the total error (arcmin); null from an older daemon.
  final double? totalSigmaArcmin;

  /// #2081 the mount's Dec spread across the check's three points (arcsec)
  /// when over 2″ (NINA's warning: the result may be off); null otherwise.
  final double? decSpreadArcsec;

  const PolarAlignCheckResult({
    required this.altErrorArcmin,
    required this.azErrorArcmin,
    required this.totalErrorArcmin,
    this.coneHalfAngleDeg = 0,
    this.turnMismatchDeg = 0,
    this.fineErrorArcmin,
    this.fineDeltaArcmin,
    this.verdict = '',
    this.noisy = false,
    this.totalSigmaArcmin,
    this.decSpreadArcsec,
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
        noisy: json['noisy'] == true,
        totalSigmaArcmin: (json['total_sigma_arcmin'] as num?)?.toDouble(),
        decSpreadArcsec: (json['dec_spread_arcsec'] as num?)?.toDouble(),
      );
}

/// The last routine to end with an error reading outside the rough stage
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

/// #2083 a hand-turn run waiting for the user to turn the RA axis
/// (`hand_turn_wait` on the status, and the `polar_align.hand_turn` payload):
/// the [point] it waits for (2 or 3 of three), the turn asked for
/// ([direction], [targetDeg]) and how far the mount's reading shows the axis
/// turned that way so far ([turnedDeg]; negative is the other way, null before
/// a solve and with the clutch open, when no angle can be claimed).
/// [waiting] is false once the turn is in.
class PolarAlignHandTurn {
  final int point;

  /// [PolarAlignDirections.east] or [PolarAlignDirections.west].
  final String direction;
  final double targetDeg;
  final double? turnedDeg;
  final bool waiting;

  const PolarAlignHandTurn({
    required this.point,
    required this.direction,
    required this.targetDeg,
    this.turnedDeg,
    this.waiting = true,
  });

  /// Null for anything that is not an object with a point.
  static PolarAlignHandTurn? tryParse(Object? json) {
    if (json is! Map || json['point'] is! num) return null;
    return PolarAlignHandTurn(
      point: (json['point'] as num).toInt(),
      direction: json['direction'] == PolarAlignDirections.west
          ? PolarAlignDirections.west
          : PolarAlignDirections.east,
      targetDeg: (json['target_deg'] as num?)?.toDouble() ?? 10.0,
      turnedDeg: (json['turned_deg'] as num?)?.toDouble(),
      waiting: json['waiting'] != false,
    );
  }
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
  /// #2083 the current or last run turns RA by hand (Ara moves nothing).
  final bool handTurn;

  /// #2083 a hand-turn run waiting for the user's turn; null otherwise.
  final PolarAlignHandTurn? handTurnWait;

  /// #2072 the 1σ of [currentErrorArcmin] (arcmin), from the fit the readout
  /// rests on; null with no error and from an older daemon.
  final double? currentSigmaArcmin;

  /// #2081 the mount's Dec spread across the seed's three points (arcsec)
  /// when over 2″, while the live readout runs; null otherwise.
  final double? decSpreadArcsec;

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
    this.correction,
    this.readoutCaution,
    this.camera,
    this.measurementNoisy = false,
    this.handTurn = false,
    this.handTurnWait,
    this.currentSigmaArcmin,
    this.decSpreadArcsec,
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
        correction: PolarAlignerRun.tryParse(json['correction']),
        readoutCaution: json['readout_caution'] is String ? json['readout_caution'] as String : null,
        camera: json['camera'] is String ? json['camera'] as String : null,
        measurementNoisy: json['measurement_noisy'] == true,
        handTurn: json['hand_turn'] == true,
        handTurnWait: PolarAlignHandTurn.tryParse(json['hand_turn_wait']),
        currentSigmaArcmin: (json['current_sigma_arcmin'] as num?)?.toDouble(),
        decSpreadArcsec: (json['dec_spread_arcsec'] as num?)?.toDouble(),
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

  /// Check stage: each of the two RA turns between the three solves.
  final double checkStepDeg;

  /// Coarse stage: the main-camera exposure.
  final double coarseExposureSeconds;

  /// #1971 the routine turns the Polar Aligner's motors (off: by hand).
  final bool polarAlignerAuto;

  /// #2048 every end of a run turns tracking off (NINA's StopTrackingWhenDone,
  /// on by default). Off: a run that finishes gives back the tracking it
  /// found; a Stop or a failure still leaves tracking off.
  final bool stopTrackingWhenDone;

  /// #2082 the camera of the measurement, its live loop and the check
  /// ([PolarAlignCameras]). Rough first is always the main camera.
  final String camera;

  /// #2082 frames averaged at each of a measurement's three points (1–5).
  final int framesPerPoint;
  /// #2068 the counterweight limit and the clearance confirmation per mount,
  /// keyed by the mount's identity (the rig's [PolarAlignPreflight.mountKey]).
  /// A mount with no entry has the rig's defaults: the pre-flight list shows
  /// them ([PolarAlignPreflightPlanned.maxCwAngleDeg]).
  final Map<String, PolarAlignMountClearance> mountClearance;

  const PolarAlignSettings({
    required this.exposureSeconds,
    required this.binning,
    required this.targetToleranceArcmin,
    required this.seedRotationDeg,
    required this.loopCadenceMs,
    required this.settleSeconds,
    required this.loopMode,
    required this.checkStepDeg,
    required this.coarseExposureSeconds,
    required this.polarAlignerAuto,
    required this.stopTrackingWhenDone,
    required this.camera,
    required this.framesPerPoint,
    required this.mountClearance,
  });

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
        'polar_aligner_auto': polarAlignerAuto,
        'stop_tracking_when_done': stopTrackingWhenDone,
        'camera': camera,
        'frames_per_point': framesPerPoint,
        'mount_clearance': {
          for (final e in mountClearance.entries) e.key: e.value.toJson(),
        },
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
    bool? polarAlignerAuto,
    bool? stopTrackingWhenDone,
    String? camera,
    int? framesPerPoint,
    Map<String, PolarAlignMountClearance>? mountClearance,
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
        polarAlignerAuto: polarAlignerAuto ?? this.polarAlignerAuto,
        stopTrackingWhenDone: stopTrackingWhenDone ?? this.stopTrackingWhenDone,
        camera: camera ?? this.camera,
        framesPerPoint: framesPerPoint ?? this.framesPerPoint,
        mountClearance: mountClearance ?? this.mountClearance,
      );

  /// This section with [mountKey]'s entry set to [clearance], the other
  /// mounts' entries kept.
  PolarAlignSettings withMountClearance(
    String mountKey,
    PolarAlignMountClearance clearance,
  ) => copyWith(mountClearance: {...mountClearance, mountKey: clearance});
}

/// #2068 one mount's entry in [PolarAlignSettings.mountClearance]: how far the
/// RA axis may turn the counterweight up from counterweight-down
/// ([maxCwAngleDeg]: 0° Home, 90° horizontal; the rig holds it at 10–75°), and
/// whether the user checked with the hand controller that the counterweight,
/// tube and cables clear the pier and tripod up to it. A confirmation is for
/// one limit ([confirmedMaxCwAngleDeg], null while not confirmed): the rig
/// keeps the entry confirmed only while it equals [maxCwAngleDeg], so a limit
/// changed without a new confirmation is not confirmed (Joey, 2026-11-21).
class PolarAlignMountClearance {
  final double maxCwAngleDeg;
  final bool clearanceConfirmed;
  final double? confirmedMaxCwAngleDeg;

  const PolarAlignMountClearance({
    required this.maxCwAngleDeg,
    required this.clearanceConfirmed,
    required this.confirmedMaxCwAngleDeg,
  });

  /// The user confirmed the clearance up to [limitDeg].
  const PolarAlignMountClearance.confirmedAt(double limitDeg)
    : maxCwAngleDeg = limitDeg,
      clearanceConfirmed = true,
      confirmedMaxCwAngleDeg = limitDeg;

  /// Not confirmed (taken back), the limit kept.
  const PolarAlignMountClearance.notConfirmed(double limitDeg)
    : maxCwAngleDeg = limitDeg,
      clearanceConfirmed = false,
      confirmedMaxCwAngleDeg = null;

  /// The limit the rig accepts ([maxCwAngleDeg] is clamped to it).
  static const double minMaxCwAngleDeg = 10;
  static const double maxMaxCwAngleDeg = 75;

  Map<String, dynamic> toJson() => <String, dynamic>{
    'max_cw_angle_deg': maxCwAngleDeg,
    'clearance_confirmed': clearanceConfirmed,
    'confirmed_max_cw_angle_deg': confirmedMaxCwAngleDeg,
  };

  @override
  bool operator ==(Object other) =>
      other is PolarAlignMountClearance &&
      other.maxCwAngleDeg == maxCwAngleDeg &&
      other.clearanceConfirmed == clearanceConfirmed &&
      other.confirmedMaxCwAngleDeg == confirmedMaxCwAngleDeg;

  @override
  int get hashCode =>
      Object.hash(maxCwAngleDeg, clearanceConfirmed, confirmedMaxCwAngleDeg);
}

/// #2068 the statuses of a [PolarAlignPreflightCheck].
abstract final class PolarAlignPreflightStatuses {
  static const ok = 'ok';
  static const warn = 'warn';

  /// The rig refuses a start on it.
  static const fail = 'fail';

  /// Not checked by the rig yet; never refuses a start.
  static const unknown = 'unknown';
}

/// #2068 the ids of the pre-flight checks the app acts on.
abstract final class PolarAlignPreflightChecks {
  static const clearanceConfirmed = 'clearance_confirmed';
  static const startPointing = 'start_pointing';

  /// The checks that depend on the measurement's turn step. The list is read
  /// for the fine stage; a Check plans with its own step, so the rig judges
  /// these for it when it starts.
  static const stageDependent = {startPointing};
}

/// #2068 one check of the pre-flight list: its id, status and the rig's
/// sentence for it.
class PolarAlignPreflightCheck {
  final String id;
  final String status;
  final String detail;

  const PolarAlignPreflightCheck({
    required this.id,
    required this.status,
    required this.detail,
  });

  bool get fails => status == PolarAlignPreflightStatuses.fail;

  bool get passes => status == PolarAlignPreflightStatuses.ok;

  factory PolarAlignPreflightCheck.fromJson(Map<String, dynamic> json) =>
      PolarAlignPreflightCheck(
        id: json['id'] as String? ?? '',
        status: json['status'] as String? ?? PolarAlignPreflightStatuses.unknown,
        detail: json['detail'] as String? ?? '',
      );
}

/// #2068 what the next Start would do, from the pre-flight list: the RA turn
/// direction (null when the rig could not plan it), each turn's step, the most
/// it turns, and the mount's counterweight limit.
class PolarAlignPreflightPlanned {
  final String? direction;
  final double turnStepDeg;
  final double checkStepDeg;
  final int turns;
  final double maxSweepDeg;
  final double maxCwAngleDeg;
  final double? startCwAngleDeg;
  final double? minAltitudeDeg;

  const PolarAlignPreflightPlanned({
    this.direction,
    required this.turnStepDeg,
    required this.checkStepDeg,
    required this.turns,
    required this.maxSweepDeg,
    required this.maxCwAngleDeg,
    this.startCwAngleDeg,
    this.minAltitudeDeg,
  });

  factory PolarAlignPreflightPlanned.fromJson(Map<String, dynamic> json) =>
      PolarAlignPreflightPlanned(
        direction: json['direction'] as String?,
        turnStepDeg: (json['turn_step_deg'] as num).toDouble(),
        checkStepDeg: (json['check_step_deg'] as num).toDouble(),
        turns: (json['turns'] as num).toInt(),
        maxSweepDeg: (json['max_sweep_deg'] as num).toDouble(),
        maxCwAngleDeg: (json['max_cw_angle_deg'] as num).toDouble(),
        startCwAngleDeg: (json['start_cw_angle_deg'] as num?)?.toDouble(),
        minAltitudeDeg: (json['min_altitude_deg'] as num?)?.toDouble(),
      );
}

/// #2068 (ARA-18) Polar Align's pre-flight list
/// (`GET /api/v1/equipment/polaralign/preflight`): every check a start makes,
/// in its order. The rig refuses a start on the first that [fails]; a warn or
/// unknown check never refuses. [mountKey] is the connected mount's key in
/// [PolarAlignSettings.mountClearance] (null with no mount).
class PolarAlignPreflight {
  final bool ready;
  final List<PolarAlignPreflightCheck> checks;
  final PolarAlignPreflightPlanned planned;
  final String? mountKey;

  const PolarAlignPreflight({
    required this.ready,
    required this.checks,
    required this.planned,
    this.mountKey,
  });

  /// A check fails: the rig would refuse a start.
  bool get blocksStart => !ready || checks.any((c) => c.fails);

  /// A check that does not depend on the turn step fails: the rig would
  /// refuse a Check too ([PolarAlignPreflightChecks.stageDependent] are the
  /// fine stage's, and a Check turns by its own step).
  bool get blocksCheck => checks.any(
    (c) => c.fails && !PolarAlignPreflightChecks.stageDependent.contains(c.id),
  );

  PolarAlignPreflightCheck? check(String id) {
    for (final c in checks) {
      if (c.id == id) return c;
    }
    return null;
  }

  factory PolarAlignPreflight.fromJson(Map<String, dynamic> json) =>
      PolarAlignPreflight(
        ready: json['ready'] == true,
        checks: [
          for (final c in json['checks'] as List<dynamic>? ?? const [])
            if (c is Map<String, dynamic>) PolarAlignPreflightCheck.fromJson(c),
        ],
        planned: PolarAlignPreflightPlanned.fromJson(
          json['planned'] as Map<String, dynamic>,
        ),
        mountKey: json['mount_key'] as String?,
      );
}

/// The RA turn directions of a [PolarAlignStartPlan].
abstract final class PolarAlignDirections {
  static const east = 'east';
  static const west = 'west';
}

/// #2048 — what a start will do, read from the rig before the mount moves
/// (`GET /api/v1/equipment/polaralign/start-plan`): the routine starts from
/// where the mount points, and its RA axis turns [stepDeg] [turns] times toward
/// [direction] first, never more than [maxSweepDeg] from where it starts. The
/// user confirms it; the start sends [direction] back, and the rig refuses a
/// start whose direction changed since.
///
/// #2089 — [newOrigin] is true when this pose becomes Polar Align's start for
/// the session (the first start since the mount connected, or the mount was
/// moved some other way since Polar Align last turned it); otherwise
/// [originOffsetDeg] is how far RA has turned from that start (+ east,
/// − west) and [direction] turns back toward it. No turn goes more than
/// [sweepLimitDeg] from that start. The start sends [newOrigin] back too.
///
/// #2083 — [handTurnAvailable]: the rig takes a hand-turn start (an older rig
/// leaves it out, and would turn the mount with its motor). [handTurn] is the
/// user's choice in the confirm step, never the rig's ([byHand]).
class PolarAlignStartPlan {
  /// The stage the start asks for ([PolarAlignStages]; null = the default).
  final String? stage;

  /// [PolarAlignDirections.east] or [PolarAlignDirections.west].
  final String direction;
  final double stepDeg;
  final int turns;
  final double maxSweepDeg;
  final bool newOrigin;
  final double originOffsetDeg;
  final double sweepLimitDeg;
  final bool handTurnAvailable;
  final bool handTurn;

  /// #2082 the camera the run measures with ([PolarAlignCameras]): the main
  /// camera for Rough first, else the profile's.
  final String camera;

  const PolarAlignStartPlan({
    this.stage,
    required this.direction,
    this.stepDeg = 10.0,
    this.turns = 2,
    this.maxSweepDeg = 20.0,
    this.newOrigin = true,
    this.originOffsetDeg = 0.0,
    this.sweepLimitDeg = 30.0,
    this.camera = PolarAlignCameras.guide,
    this.handTurnAvailable = false,
    this.handTurn = false,
  });

  /// This plan, turned by hand: the start asks the rig to move nothing.
  PolarAlignStartPlan byHand() => PolarAlignStartPlan(
        stage: stage,
        direction: direction,
        stepDeg: stepDeg,
        turns: turns,
        maxSweepDeg: maxSweepDeg,
        newOrigin: newOrigin,
        originOffsetDeg: originOffsetDeg,
        sweepLimitDeg: sweepLimitDeg,
        camera: camera,
        handTurnAvailable: handTurnAvailable,
        handTurn: true,
      );

  factory PolarAlignStartPlan.fromJson(Map<String, dynamic> json, {String? stage}) =>
      PolarAlignStartPlan(
        stage: json['stage'] as String? ?? stage,
        direction: json['direction'] == PolarAlignDirections.west
            ? PolarAlignDirections.west
            : PolarAlignDirections.east,
        stepDeg: (json['step_deg'] as num?)?.toDouble() ?? 10.0,
        turns: (json['turns'] as num?)?.toInt() ?? 2,
        maxSweepDeg: (json['max_sweep_deg'] as num?)?.toDouble() ?? 20.0,
        newOrigin: json['new_origin'] != false,
        originOffsetDeg: (json['origin_offset_deg'] as num?)?.toDouble() ?? 0.0,
        sweepLimitDeg: (json['sweep_limit_deg'] as num?)?.toDouble() ?? 30.0,
        camera: json['camera'] == PolarAlignCameras.main
            ? PolarAlignCameras.main
            : PolarAlignCameras.guide,
        handTurnAvailable: json['hand_turn_available'] == true,
      );
}
