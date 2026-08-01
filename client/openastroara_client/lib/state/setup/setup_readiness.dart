import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/camera_status.dart';
import '../../models/equipment_device_status.dart';
import '../../models/mount_status.dart';
import '../../models/polar_align.dart';
import '../equipment/camera_state.dart';
import '../equipment/mount_state.dart';
import '../polar_align/polar_align_state.dart';

/// Setup-stage readiness (§25 flow redesign). Each checklist row on the Setup
/// tab maps to one [SetupStepState]; the Run tab's pre-flight confirm reads
/// the same providers. Gates are ADVISORY — readiness is shown, never
/// enforced (soft dependencies get status affordances, not locks).

enum SetupStepState {
  /// Not started / no signal yet — neutral dot.
  pending,

  /// Actively underway (device connecting, alignment routine running).
  inProgress,

  /// The gate is satisfied — green check.
  done,

  /// Something went wrong and needs attention — red.
  problem,
}

/// Connect-equipment readiness from the two devices a session cannot start
/// without. Pure — unit-tested.
///
/// done = mount AND camera connected; problem = either reports an error;
/// inProgress = either is connecting (and none errored); else pending.
SetupStepState connectStepState(MountStatus? mount, CameraStatus? camera) {
  final states = [mount?.connectionState, camera?.connectionState];
  if (states.any((s) => s == EquipmentConnectionState.error)) {
    return SetupStepState.problem;
  }
  if (states.every((s) => s == EquipmentConnectionState.connected)) {
    return SetupStepState.done;
  }
  if (states.any((s) => s == EquipmentConnectionState.connecting)) {
    return SetupStepState.inProgress;
  }
  return SetupStepState.pending;
}

/// Polar-align readiness from the live routine view. Pure — unit-tested.
///
/// done = the last solved error sat in the green zone (the zone survives a
/// clean Stop, so "aligned then stopped the loop" stays satisfied);
/// problem = the routine failed; inProgress = seeding/adjusting/paused;
/// else pending. This is a THIS-SESSION signal by design: alignment is
/// physical and per-setup, so a fresh app session starts unaligned.
SetupStepState polarAlignStepState(PolarAlignLive live) {
  if (live.zone == 'green') return SetupStepState.done;
  if (live.phase == PolarAlignStates.failed) return SetupStepState.problem;
  if (live.phase == PolarAlignStates.seeding ||
      live.phase == PolarAlignStates.adjusting ||
      live.phase == PolarAlignStates.paused) {
    return SetupStepState.inProgress;
  }
  return SetupStepState.pending;
}

/// Live connect-equipment step state for the checklist glyph.
final setupConnectStateProvider = Provider<SetupStepState>((ref) {
  final mount = ref.watch(mountProvider).asData?.value;
  final camera = ref.watch(cameraStatusProvider).asData?.value;
  return connectStepState(mount, camera);
});

/// Live polar-align step state — shared by the Setup checklist glyph and the
/// Run pre-flight confirm.
final setupPolarAlignStateProvider = Provider<SetupStepState>(
    (ref) => polarAlignStepState(ref.watch(polarAlignLiveProvider)));
