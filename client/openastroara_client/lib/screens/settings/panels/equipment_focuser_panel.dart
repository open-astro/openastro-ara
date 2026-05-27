import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Focuser panel. 12h.2 read-only stub.
class EquipmentFocuserPanel extends StatelessWidget {
  const EquipmentFocuserPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsRow(label: 'Auto-connect on boot', value: 'On'),
        SettingsSectionHeader('Movement'),
        SettingsRow(label: 'Step size (steps)', value: '50'),
        SettingsRow(label: 'Backlash IN (steps)', value: '0'),
        SettingsRow(label: 'Backlash OUT (steps)', value: '0'),
        SettingsRow(label: 'Initial settle (ms)', value: '500'),
        SettingsSectionHeader('Autofocus'),
        SettingsRow(label: 'Use temp compensation', value: 'Off'),
        SettingsRow(label: 'Run AF after filter change', value: 'On'),
        SettingsRow(label: 'Trigger temp delta (°C)', value: '2.0'),
      ],
    );
  }
}
