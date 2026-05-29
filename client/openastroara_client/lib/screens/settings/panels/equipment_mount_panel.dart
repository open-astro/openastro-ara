import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.5 Mount panel. Auto-connect editable in 12h.2; remaining fields stay
/// read-only stubs until 12h.2b.
class EquipmentMountPanel extends ConsumerWidget {
  const EquipmentMountPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.mount),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.mount, v),
        ),
        const SettingsSectionHeader('Slew + tracking'),
        const SettingsRow(label: 'Slew rate (°/s)', value: '4'),
        const SettingsRow(label: 'Park policy', value: 'At session end'),
        const SettingsRow(label: 'Sidereal tracking on connect', value: 'On'),
        const SettingsRow(label: 'Meridian flip', value: 'Enabled'),
        const SettingsRow(
          label: 'Pause minutes after meridian',
          value: '5',
          hint: '§38.7 sequencer hooks',
        ),
        const SettingsSectionHeader('Limits'),
        const SettingsRow(label: 'Min altitude (°)', value: '20'),
        const SettingsRow(label: 'Slew settle (s)', value: '5'),
      ],
    );
  }
}
