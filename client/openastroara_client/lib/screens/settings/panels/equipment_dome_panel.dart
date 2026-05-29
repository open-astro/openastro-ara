import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Dome panel. Auto-connect editable; defaults off since dome
/// connection triggers shutter movement.
class EquipmentDomePanel extends ConsumerWidget {
  const EquipmentDomePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(
            label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.dome),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.dome, v),
          hint: 'Off by default — dome connect actuates shutter',
        ),
        const SettingsSectionHeader('Slaving'),
        const SettingsRow(
            label: 'Slave to mount', value: 'On (if connected)'),
        const SettingsRow(label: 'Azimuth tolerance (°)', value: '2.0'),
        const SettingsRow(label: 'Close shutter on park', value: 'On'),
        const SettingsRow(label: 'Close shutter on unsafe', value: 'On'),
        const SettingsRow(label: 'Home on connect', value: 'On'),
      ],
    );
  }
}
