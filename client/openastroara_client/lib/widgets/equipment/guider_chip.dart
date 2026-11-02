import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/ws/ws_providers.dart';
import '../equipment_chip.dart';
import '../status_indicator.dart';

/// Maps the guider's async status to the chip's status dot. Pure so the
/// mapping is unit-testable. `loading` reads as *info* (a connect/poll is in
/// flight), `error` as *error*, and a resolved status delegates to
/// [guiderStatusLevel].
StatusLevel guiderChipLevel(AsyncValue<GuiderStatus?> async) {
  return async.when(
    data: guiderStatusLevel,
    loading: () => StatusLevel.info,
    error: (_, _) => StatusLevel.error,
  );
}

/// The status dot for a resolved [GuiderStatus]: the link state, with the
/// runtime phase overriding it when connected (a lost star is an error; an
/// active calibrate/dither is busy).
StatusLevel guiderStatusLevel(GuiderStatus? status) {
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
        case GuiderRuntimeState.assistant:
        case GuiderRuntimeState.paused:
          // Not actively correcting — amber so a paused guider (e.g. during a
          // plate-solve/flip) is visually distinct from a healthy green guide.
          return StatusLevel.busy;
        case GuiderRuntimeState.guiding:
        case GuiderRuntimeState.stopped:
          return StatusLevel.connected;
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
/// opens the guider Settings panel on tap, like the other device chips.
class GuiderChip extends ConsumerWidget {
  const GuiderChip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Stale-guard: grey the dot when the server link is down (last status stale).
    final level = ref.watch(serverLinkUpProvider)
        ? guiderChipLevel(ref.watch(guiderStatusProvider))
        : StatusLevel.disconnected;
    return EquipmentChip(
      icon: Icons.gps_fixed,
      label: 'Guider',
      status: level,
      onTap: () => openSettingsPanel(ref, 'eq.guider'),
    );
  }
}
