import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../util/friendly_error.dart';
import 'editable_field.dart';

/// The "Auto-connect on boot" switch of an equipment panel. It saves on
/// change (ADR 0015): disabled until the rig's copy is loaded, and a refusal
/// (not loaded, changed on the rig, unreachable) shows in a snackbar and
/// leaves the switch where the rig has it.
class AutoConnectRow extends ConsumerWidget {
  const AutoConnectRow(this.type, {super.key, this.hint});

  final EquipmentDeviceType type;
  final String? hint;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final settings = ref.watch(equipmentConnectionProvider).value;
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    return SettingsSwitchRow(
      label: 'Auto-connect on boot',
      helpKey: 'eq.auto_connect_on_boot',
      value: settings?.autoConnect(type) ?? false,
      hint: hint,
      onChanged: settings == null
          ? null
          : (v) async {
              try {
                await ref
                    .read(equipmentConnectionProvider.notifier)
                    .setAutoConnect(type, v);
              } catch (e) {
                messenger.showSnackBar(
                  SnackBar(
                    content: Text(
                      e is SectionNotLoadedException ||
                              e is SectionConflictException
                          ? e.toString()
                          : friendlyError(l10n, e, action: l10n.actionSave),
                    ),
                  ),
                );
              }
            },
    );
  }
}
