import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Weather panel. 12h.2 read-only.
class EquipmentWeatherPanel extends StatelessWidget {
  const EquipmentWeatherPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsRow(label: 'Auto-connect on boot', value: 'Off'),
        SettingsSectionHeader('Thresholds'),
        SettingsRow(label: 'Cloud cover max (%)', value: '40'),
        SettingsRow(label: 'Wind speed max (km/h)', value: '30'),
        SettingsRow(label: 'Wind gust max (km/h)', value: '50'),
        SettingsRow(label: 'Humidity max (%)', value: '85'),
        SettingsRow(label: 'Dew-point margin (°C)', value: '2'),
        SettingsRow(label: 'Rain → trigger safety', value: 'On'),
        SettingsSectionHeader('Polling'),
        SettingsRow(label: 'Poll interval (s)', value: '30'),
        SettingsRow(
          label: 'Stale reading timeout (min)',
          value: '5',
          hint: '§51 diagnostics raises critical if reading is stale',
        ),
      ],
    );
  }
}
