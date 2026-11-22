import 'dart:convert';

import 'package:dio/dio.dart';

/// The causes the daemon declares for a refusal (ADR 0022): the `reason` of a
/// 409 or 422 problem. The list must equal the `RefusalReason` schema in
/// `openapi.yaml` (`refusal_openapi_drift_test.dart`).
enum RefusalCause {
  noCamera('no-camera'),
  cameraBusy('camera-busy'),
  gateStuck('gate-stuck'),
  centerInProgress('center-in-progress'),
  opticsNotSet('optics-not-set'),
  solverNotSet('solver-not-set'),
  captureSettingsUnsupported('capture-settings-unsupported'),
  storageUnavailable('storage-unavailable'),
  belowHorizonLimit('below-horizon-limit'),
  endNightInProgress('end-night-in-progress'),
  mountPositionUnverified('mount-position-unverified'),
  counterweightLimit('counterweight-limit'),
  polarAlignMountParked('polar-align-mount-parked'),
  polarAlignUnconfirmed('polar-align-unconfirmed'),
  polarAlignSweepLimit('polar-align-sweep-limit'),
  polarAlignMovingMount('polar-align-moving-mount'),
  polarAlignOnAxis('polar-align-on-axis'),
  polarAlignPointingUnknown('polar-align-pointing-unknown'),
  polarAlignPoseAmbiguous('polar-align-pose-ambiguous'),
  polarAlignMountBusy('polar-align-mount-busy'),
  firstFlipNotCompleted('first-flip-not-completed'),
  mountClockNotVerified('mount-clock-not-verified'),
  polarAlignAlreadyRunning('polar-align-already-running'),
  polarAlignClearanceNotConfirmed('polar-align-clearance-not-confirmed'),
  aboveClearanceLimit('above-clearance-limit'),
  polarAlignSiteNotSet('polar-align-site-not-set'),
  polarAlignAltAzMount('polar-align-alt-az-mount'),
  polarAlignCannotMoveAxis('polar-align-cannot-move-axis'),
  polarAlignRunActive('polar-align-run-active'),
  polarAlignCannotMoveDec('polar-align-cannot-move-dec'),
  polarAlignMoveToStartNotNeeded('polar-align-move-to-start-not-needed'),
  polarAlignMoveNotConfirmed('polar-align-move-not-confirmed');

  const RefusalCause(this.token);

  /// The wire spelling of the cause.
  final String token;

  /// The cause for [token]; null for one this app does not know.
  static RefusalCause? fromToken(String? token) {
    for (final c in values) {
      if (c.token == token) return c;
    }
    return null;
  }
}

/// The rig declining a request for a reason the user can fix (ADR 0022),
/// decoded once, in the API layer. [cause] is null for a `reason` this app
/// does not know and for an answer with none (an older daemon); [detail] is the
/// daemon's English sentence and shows only then.
class Refusal implements Exception {
  const Refusal({
    required this.cause,
    required this.reason,
    required this.detail,
    this.parameters = const {},
    this.status,
  });

  final RefusalCause? cause;
  final String? reason;
  final String? detail;

  /// The problem's extensions other than `reason` (`missing`, `job_id`, `altitude_deg`, `limit_deg`,
  /// `position_unverified_reason`, `home_is_sensor_search`, `hour_angle_hours`, `counterweight_angle_deg`, `direction`, `offset_deg`).
  final Map<String, Object?> parameters;
  final int? status;

  static const _problemFields = {
    'type',
    'title',
    'status',
    'detail',
    'instance',
    'reason',
    'traceId',
  };

  /// The Refusal a 409 or 422 problem answer carries; null for any other
  /// failure (a transport error, a 5xx, a 404), which stays the caller's.
  static Refusal? fromDio(DioException e) {
    final status = e.response?.statusCode;
    if (status != 409 && status != 422) return null;
    var body = e.response?.data;
    if (body is String) {
      try {
        body = jsonDecode(body);
      } on FormatException {
        return null;
      }
    }
    if (body is! Map) return null;
    final reason = body['reason'];
    final detail = body['detail'];
    return Refusal(
      cause: reason is String ? RefusalCause.fromToken(reason) : null,
      reason: reason is String ? reason : null,
      detail: detail is String && detail.isNotEmpty ? detail : null,
      parameters: {
        for (final f in body.entries)
          if (f.key is String && !_problemFields.contains(f.key))
            f.key as String: f.value,
      },
      status: status,
    );
  }

  @override
  String toString() => detail ?? reason ?? 'Refusal';
}
