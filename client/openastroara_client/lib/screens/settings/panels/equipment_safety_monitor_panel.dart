import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Safety Monitor panel. 12h.2 read-only.
class EquipmentSafetyMonitorPanel extends StatelessWidget {
  const EquipmentSafetyMonitorPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsRow(label: 'Auto-connect on boot', value: 'Off'),
        SettingsSectionHeader('Behavior'),
        SettingsRow(
          label: 'On unsafe',
          value: 'Park + close',
          hint: '§35 safety policies — overrideable per profile',
        ),
        SettingsRow(label: 'Min safe window (min)', value: '15'),
        SettingsRow(label: 'Auto-resume when safe', value: 'On'),
        SettingsRow(
          label: 'Resume delay (min)',
          value: '10',
          hint: 'wait this long after first "safe" reading',
        ),
      ],
    );
  }
}
