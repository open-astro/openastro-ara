import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/ws/ws_providers.dart';
import '../equipment_chip.dart';
import '../status_indicator.dart';
import 'guider_dialog.dart';

/// Maps the guider's async status to the chip's status dot. Pure so the
/// mapping is unit-testable. `loading` reads as *info* (a connect/poll is in
/// flight), `error` as *error*, and a resolved status delegates to
/// [guiderStatusLevel].
StatusLevel guiderChipLevel(AsyncValue<GuiderStatus?> async,
    {bool runActive = false}) {
  return async.when(
    data: (s) => guiderStatusLevel(s, runActive: runActive),
    loading: () => StatusLevel.info,
    error: (_, _) => StatusLevel.error,
  );
}

/// The tooltip for the dot, or null when green needs no explanation.
String? guiderChipTooltip(GuiderStatus? status, {bool runActive = false}) {
  if (status == null ||
      status.connectionState != GuiderConnectionState.connected) {
    return null;
  }
  return switch (status.runtimeState) {
    GuiderRuntimeState.stopped when runActive =>
      'Not guiding while a sequence runs',
    GuiderRuntimeState.starLost => 'Guide star lost',
    GuiderRuntimeState.calibrating => 'Calibrating',
    GuiderRuntimeState.settling => 'Settling after a dither',
    GuiderRuntimeState.dithering => 'Dithering',
    GuiderRuntimeState.paused => 'Guiding paused',
    GuiderRuntimeState.looping => 'Looping exposures, not guiding',
    GuiderRuntimeState.starSelected => 'Star selected, not guiding',
    _ => null,
  };
}

/// The status dot for a resolved [GuiderStatus]: the link state, with the
/// runtime phase overriding it when connected (a lost star is an error; an
/// active calibrate/dither is busy).
StatusLevel guiderStatusLevel(GuiderStatus? status, {bool runActive = false}) {
  if (status == null) return StatusLevel.disconnected;
  switch (status.connectionState) {
    case GuiderConnectionState.connected:
      switch (status.runtimeState) {
        case GuiderRuntimeState.starLost:
          return StatusLevel.error;
        case GuiderRuntimeState.calibrating:
        case GuiderRuntimeState.dithering:
        case GuiderRuntimeState.settling:
        case GuiderRuntimeState.looping:
        case GuiderRuntimeState.starSelected:
        case GuiderRuntimeState.paused:
          // Not actively correcting — amber so a paused guider (e.g. during a
          // plate-solve/flip) is visually distinct from a healthy green guide.
          return StatusLevel.busy;
        case GuiderRuntimeState.guiding:
          return StatusLevel.connected;
        case GuiderRuntimeState.stopped:
          // Idle between sessions is healthy; idle while a sequence runs is a
          // standing problem (2026-10-16: 20 minutes of unguided frames behind
          // a green dot after Stop in the guide-camera dialog).
          return runActive ? StatusLevel.warning : StatusLevel.connected;
        case GuiderRuntimeState.unknown:
          // Connected but runtime not yet polled — info, not a misleading green.
          return StatusLevel.info;
      }
    case GuiderConnectionState.connecting:
      return StatusLevel.info;
    case GuiderConnectionState.error:
      return StatusLevel.error;
    case GuiderConnectionState.disconnected:
    case GuiderConnectionState.unknown:
      return StatusLevel.disconnected;
  }
}

/// §25.3 GUIDE chip, live. Watches [guiderStatusProvider] for the status dot and
/// opens the connect/disconnect dialog on tap. A drop-in for the static
/// `EquipmentChip` the top bar used before slice 2.
class GuiderChip extends ConsumerWidget {
  const GuiderChip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Stale-guard: grey the dot when the server link is down (last status stale).
    final linkUp = ref.watch(serverLinkUpProvider);
    final status = ref.watch(guiderStatusProvider);
    final runActive =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final level = linkUp
        ? guiderChipLevel(status, runActive: runActive)
        : StatusLevel.disconnected;
    final tooltip = !linkUp
        ? 'Server link lost — status is stale'
        : guiderChipTooltip(status.value, runActive: runActive);
    return EquipmentChip(
      icon: Icons.gps_fixed,
      label: 'GUIDE',
      status: level,
      tooltip: tooltip,
      onTap: () => showGuiderDialog(context),
    );
  }
}
