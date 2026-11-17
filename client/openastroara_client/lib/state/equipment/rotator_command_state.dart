import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../../models/rotator_status.dart';
import 'device_command_state.dart';
import 'rotator_state.dart';

/// What a rotator command asks for: a move to an angle (sky or mechanical) or
/// a sync of the sky angle.
@immutable
class RotatorTarget {
  const RotatorTarget.move(this.angleDeg, {required this.useSkyAngle})
    : isSync = false;
  const RotatorTarget.sync(this.angleDeg) : useSkyAngle = true, isSync = true;

  final double angleDeg;
  final bool useSkyAngle;
  final bool isSync;

  @override
  bool operator ==(Object other) =>
      other is RotatorTarget &&
      other.angleDeg == angleDeg &&
      other.useSkyAngle == useSkyAngle &&
      other.isSync == isSync;

  @override
  int get hashCode => Object.hash(angleDeg, useSkyAngle, isSync);
}

/// A rotator move or sync, for both presentations (ADR 0013).
class RotatorCommandNotifier
    extends DeviceCommandNotifier<RotatorTarget, RotatorStatus> {
  /// The angle moved less than this reads as not turning; within
  /// [_landedTolerance] of the target is on it (RotatorDriftWatch).
  static const _motionTolerance = 0.1;
  static const _landedTolerance = 0.5;

  // The move's angle as it read when the command was sent.
  double? _angleAtSend;

  @override
  ProviderListenable<AsyncValue<RotatorStatus?>> get device => rotatorProvider;

  double? _angle(RotatorStatus s, RotatorTarget t) =>
      t.useSkyAngle ? s.skyAngleDeg : s.mechanicalAngleDeg;

  /// Degrees between two angles, the short way round through 0.
  static double _apart(double a, double b) {
    final d = (a - b).abs() % 360;
    return d > 180 ? 360 - d : d;
  }

  @override
  Future<bool> send(RotatorTarget target) {
    final s = ref.read(rotatorProvider).value;
    _angleAtSend = s == null ? null : _angle(s, target);
    final notifier = ref.read(rotatorProvider.notifier);
    return target.isSync
        ? notifier.sync(target.angleDeg)
        : notifier.move(target.angleDeg, useSkyAngle: target.useSkyAngle);
  }

  @override
  Future<void> refresh() => ref.read(rotatorProvider.notifier).refresh();

  // A null status is no news (a read with no server), not a lost rotator.
  @override
  bool gone(RotatorStatus? s) => s != null && !s.isConnected;

  @override
  bool moving(RotatorStatus s) => s.isMoving;

  @override
  bool motionSeen(RotatorStatus s, RotatorTarget target) {
    if (s.isMoving) return true;
    final now = _angle(s, target);
    final before = _angleAtSend;
    return now != null &&
        before != null &&
        _apart(now, before) > _motionTolerance;
  }

  @override
  bool landed(
    RotatorStatus s,
    RotatorTarget target, {
    required bool motionSeen,
  }) {
    if (!motionSeen || s.isMoving) return false;
    final now = _angle(s, target);
    return now != null && _apart(now, target.angleDeg) <= _landedTolerance;
  }

  @override
  bool expectsMotion(RotatorTarget target) => !target.isSync;

  @override
  Duration firstStall(RotatorTarget target) => const Duration(seconds: 3);

  // RotatorService.Mediator.cs MovePolicy HardTimeout, 5 min.
  @override
  Duration backstop(RotatorTarget target) => const Duration(seconds: 300);
}

/// Lives while a window or panel shows it; a move in flight ends with the last.
final rotatorCommandProvider =
    NotifierProvider.autoDispose<
      RotatorCommandNotifier,
      DeviceCommand<RotatorTarget>
    >(RotatorCommandNotifier.new);
