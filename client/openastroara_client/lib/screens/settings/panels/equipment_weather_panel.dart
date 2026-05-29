import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Weather panel. Auto-connect editable; defaults off.
class EquipmentWeatherPanel extends ConsumerWidget {
  const EquipmentWeatherPanel({super.key});

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
          value: connection.autoConnect(EquipmentDeviceType.weather),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.weather, v),
        ),
        const SettingsSectionHeader('Thresholds'),
        const SettingsRow(label: 'Cloud cover max (%)', value: '40'),
        const SettingsRow(label: 'Wind speed max (km/h)', value: '30'),
        const SettingsRow(label: 'Wind gust max (km/h)', value: '50'),
        const SettingsRow(label: 'Humidity max (%)', value: '85'),
        const SettingsRow(label: 'Dew-point margin (°C)', value: '2'),
        const SettingsRow(label: 'Rain → trigger safety', value: 'On'),
        const SettingsSectionHeader('Polling'),
        const SettingsRow(label: 'Poll interval (s)', value: '30'),
        const SettingsRow(
          label: 'Stale reading timeout (min)',
          value: '5',
          hint: '§51 diagnostics raises critical if reading is stale',
        ),
      ],
    );
  }
}
