import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Rotator panel. Auto-connect editable; other fields read-only.
class EquipmentRotatorPanel extends ConsumerWidget {
  const EquipmentRotatorPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final autoConnect = ref.watch(equipmentConnectionProvider
        .select((s) => s.autoConnect(EquipmentDeviceType.rotator)));
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(
            label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          value: autoConnect,
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.rotator, v),
        ),
        const SettingsSectionHeader('Orientation'),
        const SettingsRow(label: 'Reverse direction', value: 'Off'),
        const SettingsRow(label: 'Step size (°)', value: '0.5'),
        const SettingsRow(label: 'Settle (ms)', value: '500'),
        const SettingsRow(
          label: 'Plate-solve to set angle',
          value: 'On',
          hint: '§38 Framing + Set-as-Target hook',
        ),
      ],
    );
  }
}
