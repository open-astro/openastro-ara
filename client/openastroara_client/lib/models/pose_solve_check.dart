/// #2173 — clearing an unverified mount position with a plate solve:
/// `GET /api/v1/equipment/telescope/verify-position-by-solve` (whether the
/// check can run now) and the result of `POST …/verify-position-by-solve` and
/// `POST …/verify-position-by-solve/sync`. Strict readers (#2133): a missing
/// key fails the read; a null value is the rig's own null.
library;

Object? _key(Map<String, dynamic> j, String key) =>
    j.containsKey(key) ? j[key] : throw FormatException('missing $key');

String _string(Map<String, dynamic> j, String key) {
  final v = _key(j, key);
  return v is String ? v : throw FormatException('$key is not a string');
}

String? _stringOrNull(Map<String, dynamic> j, String key) {
  final v = _key(j, key);
  return v == null || v is String
      ? v as String?
      : throw FormatException('$key is not a string');
}

double? _doubleOrNull(Map<String, dynamic> j, String key) {
  final v = _key(j, key);
  return v == null || v is num
      ? (v as num?)?.toDouble()
      : throw FormatException('$key is not a number');
}

/// Which camera the check uses: the main camera, or the guide camera only when
/// there is no main camera.
enum PoseSolveCamera { main, guide }

PoseSolveCamera? _camera(String? token) => switch (token) {
  null => null,
  'main' => PoseSolveCamera.main,
  'guide' => PoseSolveCamera.guide,
  _ => throw FormatException('unknown camera $token'),
};

/// Whether the check can run now. [available] is true only while the mount's
/// position is unverified, a camera and ASTAP are set up and the camera is idle.
class PoseSolveAvailability {
  final bool available;
  final PoseSolveCamera? camera;

  /// Null when available; else the rig's token (`not_needed`,
  /// `mount_not_connected`, `no_camera`, `camera_busy`, `solver_not_set`,
  /// `optics_not_set`).
  final String? unavailableReason;

  const PoseSolveAvailability({
    required this.available,
    required this.camera,
    required this.unavailableReason,
  });

  factory PoseSolveAvailability.fromJson(Map<String, dynamic> j) {
    final available = _key(j, 'available');
    if (available is! bool) {
      throw const FormatException('available is not a bool');
    }
    return PoseSolveAvailability(
      available: available,
      camera: _camera(_stringOrNull(j, 'camera')),
      unavailableReason: _stringOrNull(j, 'unavailable_reason'),
    );
  }
}

/// What one check found.
enum PoseSolveOutcome {
  /// The sky and the pier side agree: the position is trusted again.
  trusted,

  /// The sky is elsewhere: one Sync is on offer.
  mismatch,

  /// The pier side disagrees: no trust, no Sync (Home is the way out).
  pierSideDisagrees,

  /// The pier side could not be checked: no trust.
  pierSideUnknown,

  /// The frame did not solve.
  solveFailed,

  /// The mount moved, reconnected or did not answer during the check.
  mountMoved,

  /// The mount (or a Sync guard) refused the Sync.
  syncRefused,

  /// After the Sync the mount or a second solve still disagrees.
  syncNotTaken,
}

PoseSolveOutcome _outcome(String token) => switch (token) {
  'trusted' => PoseSolveOutcome.trusted,
  'mismatch' => PoseSolveOutcome.mismatch,
  'pier_side_disagrees' => PoseSolveOutcome.pierSideDisagrees,
  'pier_side_unknown' => PoseSolveOutcome.pierSideUnknown,
  'solve_failed' => PoseSolveOutcome.solveFailed,
  'mount_moved' => PoseSolveOutcome.mountMoved,
  'sync_refused' => PoseSolveOutcome.syncRefused,
  'sync_not_taken' => PoseSolveOutcome.syncNotTaken,
  _ => throw FormatException('unknown outcome $token'),
};

/// One check's result. Positions are in the mount's own frame ([frame]).
class PoseSolveCheckResult {
  final PoseSolveOutcome outcome;
  final PoseSolveCamera camera;
  final String? frame;
  final double? mountRaHours;
  final double? mountDecDegrees;
  final double? solvedRaHours;
  final double? solvedDecDegrees;
  final double? separationDegrees;

  /// The id the Sync sends back; set only on a [PoseSolveOutcome.mismatch].
  final String? checkId;

  /// The rig's English sentence, for the log.
  final String detail;

  const PoseSolveCheckResult({
    required this.outcome,
    required this.camera,
    required this.frame,
    required this.mountRaHours,
    required this.mountDecDegrees,
    required this.solvedRaHours,
    required this.solvedDecDegrees,
    required this.separationDegrees,
    required this.checkId,
    required this.detail,
  });

  factory PoseSolveCheckResult.fromJson(Map<String, dynamic> j) =>
      PoseSolveCheckResult(
        outcome: _outcome(_string(j, 'outcome')),
        camera: _camera(_string(j, 'camera'))!,
        frame: _stringOrNull(j, 'frame'),
        mountRaHours: _doubleOrNull(j, 'mount_ra_hours'),
        mountDecDegrees: _doubleOrNull(j, 'mount_dec_degrees'),
        solvedRaHours: _doubleOrNull(j, 'solved_ra_hours'),
        solvedDecDegrees: _doubleOrNull(j, 'solved_dec_degrees'),
        separationDegrees: _doubleOrNull(j, 'separation_degrees'),
        checkId: _stringOrNull(j, 'check_id'),
        detail: _string(j, 'detail'),
      );
}
