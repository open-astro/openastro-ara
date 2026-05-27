import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Dome panel. 12h.2 read-only.
class EquipmentDomePanel extends StatelessWidget {
  const EquipmentDomePanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsRow(label: 'Auto-connect on boot', value: 'Off'),
        SettingsSectionHeader('Slaving'),
        SettingsRow(label: 'Slave to mount', value: 'On (if connected)'),
        SettingsRow(label: 'Azimuth tolerance (°)', value: '2.0'),
        SettingsRow(label: 'Close shutter on park', value: 'On'),
        SettingsRow(label: 'Close shutter on unsafe', value: 'On'),
        SettingsRow(label: 'Home on connect', value: 'On'),
      ],
    );
  }
}
