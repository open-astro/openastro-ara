import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Filter Wheel panel. 12h.2 read-only stub.
class EquipmentFilterWheelPanel extends StatelessWidget {
  const EquipmentFilterWheelPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsRow(label: 'Auto-connect on boot', value: 'On'),
        SettingsSectionHeader('Filters'),
        SettingsRow(label: 'Slot 1', value: 'L'),
        SettingsRow(label: 'Slot 2', value: 'R'),
        SettingsRow(label: 'Slot 3', value: 'G'),
        SettingsRow(label: 'Slot 4', value: 'B'),
        SettingsRow(label: 'Slot 5', value: 'Hα'),
        SettingsRow(label: 'Slot 6', value: 'OIII'),
        SettingsRow(label: 'Slot 7', value: 'SII'),
        SettingsRow(label: 'Slot 8', value: '—'),
        SettingsSectionHeader('Focus offsets'),
        SettingsRow(
          label: 'Per-filter offsets',
          value: 'Not measured — run §37.4.2 measurement wizard',
        ),
      ],
    );
  }
}
