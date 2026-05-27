import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Rotator panel. 12h.2 read-only stub.
class EquipmentRotatorPanel extends StatelessWidget {
  const EquipmentRotatorPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected (optional)'),
        SettingsRow(label: 'Auto-connect on boot', value: 'On'),
        SettingsSectionHeader('Orientation'),
        SettingsRow(label: 'Reverse direction', value: 'Off'),
        SettingsRow(label: 'Step size (°)', value: '0.5'),
        SettingsRow(label: 'Settle (ms)', value: '500'),
        SettingsRow(
          label: 'Plate-solve to set angle',
          value: 'On',
          hint: '§38 Framing + Set-as-Target hook',
        ),
      ],
    );
  }
}
