import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;

import '../../models/filter_wheel_status.dart';
import 'device_command_state.dart';
import 'filter_wheel_state.dart';

export 'device_command_state.dart'
    show DeviceCommand, DeviceCommandPhase, DeviceCommandRefusal;

/// A filter change, for both presentations (ADR 0013, the device-command
/// pilot), on the shared [DeviceCommandNotifier].
class FilterWheelCommandNotifier
    extends DeviceCommandNotifier<int, FilterWheelStatus> {
  /// No motion seen in this long fails the move; motion seen arms [backstop].
  static const firstStallAfter = Duration(seconds: 10);
  static const backstopAfter = Duration(seconds: 45);

  @override
  ProviderListenable<AsyncValue<FilterWheelStatus?>> get device =>
      filterWheelProvider;

  @override
  Future<bool> send(int target) =>
      ref.read(filterWheelProvider.notifier).changeFilter(target);

  @override
  Future<void> refresh() => ref.read(filterWheelProvider.notifier).refresh();

  // Gone, or a manual wheel now waiting on the user: nothing to watch.
  @override
  bool gone(FilterWheelStatus? s) =>
      s == null || !s.isConnected || s.isAwaitingUser;

  @override
  bool moving(FilterWheelStatus s) => s.isMoving;

  @override
  bool landed(FilterWheelStatus s, int target, {required bool motionSeen}) =>
      s.currentSlot == target;

  @override
  Duration firstStall(int target) => firstStallAfter;

  @override
  Duration backstop(int target) => backstopAfter;
}

/// Lives while a picker shows it; a move in flight ends with the last one.
final filterWheelCommandProvider =
    NotifierProvider.autoDispose<
      FilterWheelCommandNotifier,
      DeviceCommand<int>
    >(FilterWheelCommandNotifier.new);
