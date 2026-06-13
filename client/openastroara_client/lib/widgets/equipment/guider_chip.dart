import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../theme/ara_colors.dart';
import '../status_indicator.dart';
import 'guider_dialog.dart';

/// Maps the guider's async status to the §25.3 chip's status dot. Pure so the
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
          return StatusLevel.busy;
        default:
          return StatusLevel.connected;
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
    final level = guiderChipLevel(ref.watch(guiderStatusProvider));
    return Semantics(
      label: 'GUIDE (${level.name})',
      button: true,
      child: InkWell(
        onTap: () => showGuiderDialog(context),
        borderRadius: BorderRadius.circular(6),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          width: 64,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Stack(
                clipBehavior: Clip.none,
                children: [
                  const Icon(Icons.gps_fixed, size: 28, color: AraColors.textPrimary),
                  Positioned(
                    top: -2,
                    right: -2,
                    child: Container(
                      width: 10,
                      height: 10,
                      decoration: BoxDecoration(
                        color: level.color,
                        shape: BoxShape.circle,
                        border: Border.all(color: AraColors.bgPanel, width: 2),
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 2),
              Text(
                'GUIDE',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textSecondary,
                      letterSpacing: 0.5,
                    ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
