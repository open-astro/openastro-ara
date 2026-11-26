import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/mount_status.dart';
import '../../models/polar_align.dart';
import '../equipment/mount_home_state.dart';
import '../equipment/mount_state.dart';
import 'polar_align_state.dart';

/// The Polar Align screen, one step at a time. Both presentations (the Setup
/// panel and Simple Mode) show the same step from the same live view (ADR
/// 0013 rule 3).
enum PolarFlowStep {
  /// No run: Home, then Start.
  ready,

  /// The three-point measurement from Home.
  measuring,

  /// The live readout while the user turns the bolts.
  adjusting,

  /// After Done: the mount goes Home before the verify solves.
  goingHome,

  /// The verify solves.
  checking,

  /// The verdict is known and the mount goes back Home.
  returningHome,

  /// The run is over: the verdict, Done and Adjust again.
  result,
}

/// The step [live] is at. Pure — unit-tested.
PolarFlowStep polarFlowStep(PolarAlignLive live) => switch (live.phase) {
  PolarAlignStates.seeding => PolarFlowStep.measuring,
  PolarAlignStates.adjusting ||
  PolarAlignStates.paused => PolarFlowStep.adjusting,
  PolarAlignStates.homing =>
    live.checkResult == null
        ? PolarFlowStep.goingHome
        : PolarFlowStep.returningHome,
  PolarAlignStates.checking => PolarFlowStep.checking,
  PolarAlignStates.checked
      when live.checkResult != null && !live.resultDismissed =>
    PolarFlowStep.result,
  _ => PolarFlowStep.ready,
};

/// What the Ready step offers, from the mount: every run starts from Home.
enum PolarReadyGate {
  /// No mount is connected.
  noMount,

  /// The mount is parked: Unpark comes first.
  parked,

  /// The mount moves for something else.
  moving,

  /// The mount can find Home and is not there: Home.
  home,

  /// Home was asked here (or in the Mount window) and the mount is on its way.
  goingHome,

  /// The mount is at Home, or cannot find Home by itself: Start.
  start,
}

/// The Ready step's gate for [mount]; [homing] is [mountHomeProvider]. Pure —
/// unit-tested.
PolarReadyGate polarReadyGate(MountStatus? mount, {required bool homing}) {
  if (mount == null || !mount.isConnected) return PolarReadyGate.noMount;
  if (homing) return PolarReadyGate.goingHome;
  if (mount.parked) return PolarReadyGate.parked;
  if (mount.isBusy) return PolarReadyGate.moving;
  final canFindHome = mount.capabilities?.canFindHome ?? false;
  return canFindHome && !mount.atHome
      ? PolarReadyGate.home
      : PolarReadyGate.start;
}

/// The Ready step's gate from the live mount; loading and error are the
/// mount read's own.
final polarReadyGateProvider = Provider<AsyncValue<PolarReadyGate>>((ref) {
  final homing = ref.watch(mountHomeProvider);
  return ref
      .watch(mountProvider)
      .whenData((m) => polarReadyGate(m, homing: homing));
});
