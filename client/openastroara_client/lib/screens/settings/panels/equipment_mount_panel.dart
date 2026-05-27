import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.5 Mount panel. 12h.2 read-only stub; 12h.2b wires editable forms +
/// `/api/v1/profile/mount`.
class EquipmentMountPanel extends StatelessWidget {
  const EquipmentMountPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsRow(label: 'Auto-connect on boot', value: 'On'),
        SettingsSectionHeader('Slew + tracking'),
        SettingsRow(label: 'Slew rate (°/s)', value: '4'),
        SettingsRow(label: 'Park policy', value: 'At session end'),
        SettingsRow(label: 'Sidereal tracking on connect', value: 'On'),
        SettingsRow(label: 'Meridian flip', value: 'Enabled'),
        SettingsRow(
          label: 'Pause minutes after meridian',
          value: '5',
          hint: '§38.7 sequencer hooks',
        ),
        SettingsSectionHeader('Limits'),
        SettingsRow(label: 'Min altitude (°)', value: '20'),
        SettingsRow(label: 'Slew settle (s)', value: '5'),
      ],
    );
  }
}
