import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/camera_status.dart';
import '../../models/equipment_device_status.dart';
import '../../models/mount_status.dart';
import '../../models/autofocus_run.dart';
import '../../models/polar_align.dart';
import '../../util/observing_night.dart';
import '../equipment/camera_state.dart';
import '../equipment/mount_state.dart';
import '../focus/autofocus_live_state.dart';
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
/// problem = the routine failed; inProgress = any running stage;
/// else pending. This is a THIS-SESSION signal by design: alignment is
/// physical and per-setup, so a fresh app session starts unaligned. Simple
/// Mode's Night Setup (#1565) passes [last] and [now] to also count the rig's
/// last routine past the rough stage: done when it ended in tonight's observing
/// night inside the green zone, unless a newer routine stopped here outside it.
/// The Full UI passes neither.
SetupStepState polarAlignStepState(
  PolarAlignLive live, {
  PolarAlignLastResult? last,
  DateTime? now,
}) {
  // The rough stage's green is a first pass on the main camera, not an
  // alignment: fine and check are still ahead of it. (A new stage starts with
  // no readout — the daemon and the fold both clear it — so a later stage's
  // green is its own.)
  if (live.zone == 'green' && live.stage != PolarAlignStages.coarse) {
    return SetupStepState.done;
  }
  if (live.phase == PolarAlignStates.failed) return SetupStepState.problem;
  // A finished check outside the green zone is a measurement that says
  // "not aligned": the step needs work, it is not untouched.
  if (live.phase == PolarAlignStates.checked) return SetupStepState.problem;
  if (live.isActive) return SetupStepState.inProgress;
  // A routine stopped outside the green here is newer than [last].
  if (live.phase == PolarAlignStates.stopped &&
      live.zone != null &&
      live.zone != 'green') {
    return SetupStepState.pending;
  }
  if (last != null &&
      now != null &&
      polarAlignZone(last.errorArcmin) == 'green' &&
      isTonight(last.completedUtc, now)) {
    return SetupStepState.done;
  }
  return SetupStepState.pending;
}

/// Smart Focus readiness from the live autofocus view. Pure — unit-tested.
///
/// done = a run completed this app session (or the user marked the scope as
/// already in focus); inProgress = a run is underway; problem = the last run
/// failed; else pending. A cancelled run is pending, not a problem — the user
/// asked for it. THIS-SESSION by design, like polar alignment: focus drifts.
/// Simple Mode's Night Setup (#1565) passes [now] to also count a completed run
/// the rig kept from earlier in tonight's observing night; the Full UI does not.
SetupStepState focusStepState(AutofocusLive live, {DateTime? now}) {
  if (live.run.isRunning) return SetupStepState.inProgress;
  if (live.focusedThisSession) return SetupStepState.done;
  if (now != null &&
      live.run.isComplete &&
      isTonight(live.run.completedUtc, now)) {
    return SetupStepState.done;
  }
  if (live.run.state == AutofocusRunStates.failed) return SetupStepState.problem;
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

/// Live focusing step state for the checklist glyph.
final setupFocusStateProvider = Provider<SetupStepState>(
    (ref) => focusStepState(ref.watch(autofocusLiveProvider)));
