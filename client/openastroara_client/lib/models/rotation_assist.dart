/// The daemon's by-hand rotation readout (`GET /api/v1/rotation-assist/state`):
/// the target position angle, the latest plate-solved angle and the signed,
/// folded delta still to turn, plus the recent history the advice reads.
abstract final class RotationAssistStates {
  static const idle = 'idle';
  static const running = 'running';
  static const stopped = 'stopped';
  static const error = 'error';
}

class RotationAssistSample {
  final int seq;
  final DateTime? solvedUtc;
  final double solvedPositionAngleDeg;

  /// Signed shortest rotation to the target, folded into (−90°, +90°]: a
  /// frame rotated by 180° is the same framing.
  final double deltaDeg;

  const RotationAssistSample({
    required this.seq,
    this.solvedUtc,
    required this.solvedPositionAngleDeg,
    required this.deltaDeg,
  });

  factory RotationAssistSample.fromJson(Map<String, dynamic> json) =>
      RotationAssistSample(
        seq: (json['seq'] as num?)?.toInt() ?? 0,
        solvedUtc: DateTime.tryParse(json['solved_utc'] as String? ?? ''),
        solvedPositionAngleDeg:
            (json['solved_position_angle_deg'] as num?)?.toDouble() ?? 0,
        deltaDeg: (json['delta_deg'] as num?)?.toDouble() ?? 0,
      );
}

class RotationAssistStatus {
  final bool active;
  final String state;
  final double targetPositionAngleDeg;
  final double toleranceDeg;
  final int seq;
  final DateTime? startedUtc;
  final RotationAssistSample? latest;
  final List<RotationAssistSample> recent;
  final bool withinTolerance;
  final String? error;
  final int consecutiveFailures;

  const RotationAssistStatus({
    this.active = false,
    this.state = RotationAssistStates.idle,
    this.targetPositionAngleDeg = 0,
    this.toleranceDeg = 1,
    this.seq = 0,
    this.startedUtc,
    this.latest,
    this.recent = const [],
    this.withinTolerance = false,
    this.error,
    this.consecutiveFailures = 0,
  });

  static const idle = RotationAssistStatus();

  factory RotationAssistStatus.fromJson(Map<String, dynamic> json) {
    final raw = json['recent'];
    final recent = <RotationAssistSample>[];
    if (raw is List) {
      for (final s in raw) {
        if (s is Map<String, dynamic>) {
          recent.add(RotationAssistSample.fromJson(s));
        }
      }
    }
    final latest = json['latest'];
    return RotationAssistStatus(
      active: json['active'] as bool? ?? false,
      state: json['state'] as String? ?? RotationAssistStates.idle,
      targetPositionAngleDeg:
          (json['target_position_angle_deg'] as num?)?.toDouble() ?? 0,
      toleranceDeg: (json['tolerance_deg'] as num?)?.toDouble() ?? 1,
      seq: (json['seq'] as num?)?.toInt() ?? 0,
      startedUtc: DateTime.tryParse(json['started_utc'] as String? ?? ''),
      latest: latest is Map<String, dynamic>
          ? RotationAssistSample.fromJson(latest)
          : null,
      recent: recent,
      withinTolerance: json['within_tolerance'] as bool? ?? false,
      error: json['error'] as String?,
      consecutiveFailures: (json['consecutive_failures'] as num?)?.toInt() ?? 0,
    );
  }
}
