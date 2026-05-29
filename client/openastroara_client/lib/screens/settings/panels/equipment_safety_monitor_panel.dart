import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Safety Monitor panel. Auto-connect editable; behavior fields
/// remain references to §35 safety policies.
class EquipmentSafetyMonitorPanel extends ConsumerWidget {
  const EquipmentSafetyMonitorPanel({super.key});

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
          value: connection.autoConnect(EquipmentDeviceType.safetyMonitor),
          onChanged: (v) =>
              n.setAutoConnect(EquipmentDeviceType.safetyMonitor, v),
        ),
        const SettingsSectionHeader('Behavior'),
        const SettingsRow(
          label: 'On unsafe',
          value: 'Park + close',
          hint: '§35 safety policies — overrideable per profile',
        ),
        const SettingsRow(label: 'Min safe window (min)', value: '15'),
        const SettingsRow(label: 'Auto-resume when safe', value: 'On'),
        const SettingsRow(
          label: 'Resume delay (min)',
          value: '10',
          hint: 'wait this long after first "safe" reading',
        ),
      ],
    );
  }
}
