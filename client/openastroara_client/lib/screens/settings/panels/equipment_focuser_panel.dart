import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Focuser panel. Auto-connect editable in 12h.2; movement +
/// autofocus fields stay read-only refs (autofocus settings live in
/// `autofocusSettingsProvider` per §37.11).
class EquipmentFocuserPanel extends ConsumerWidget {
  const EquipmentFocuserPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final autoConnect = ref.watch(equipmentConnectionProvider
        .select((s) => s.autoConnect(EquipmentDeviceType.focuser)));
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          value: autoConnect,
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.focuser, v),
        ),
        const SettingsSectionHeader('Movement'),
        const SettingsRow(label: 'Step size (steps)', value: '50'),
        const SettingsRow(label: 'Backlash IN (steps)', value: '0'),
        const SettingsRow(label: 'Backlash OUT (steps)', value: '0'),
        const SettingsRow(label: 'Initial settle (ms)', value: '500'),
        const SettingsSectionHeader('Autofocus'),
        const SettingsRow(label: 'Use temp compensation', value: 'Off'),
        const SettingsRow(label: 'Run AF after filter change', value: 'On'),
        const SettingsRow(label: 'Trigger temp delta (°C)', value: '2.0'),
      ],
    );
  }
}
