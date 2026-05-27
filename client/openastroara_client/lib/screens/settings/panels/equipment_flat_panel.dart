import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 Flat Panel (CoverCalibrator under Alpaca) settings. 12h.2 read-only.
class EquipmentFlatPanel extends StatelessWidget {
  const EquipmentFlatPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(
          label: 'Alpaca device',
          value: 'Not selected (CoverCalibrator)',
          hint: '§10.6 — Alpaca exposes flat panels as CoverCalibrator',
        ),
        SettingsRow(label: 'Auto-connect on boot', value: 'On'),
        SettingsSectionHeader('Flat capture'),
        SettingsRow(label: 'Open cover on connect', value: 'On'),
        SettingsRow(label: 'Close cover on park', value: 'On'),
        SettingsRow(label: 'Calibrator min brightness', value: '0'),
        SettingsRow(label: 'Calibrator max brightness', value: '255'),
        SettingsRow(
          label: 'Auto-brightness target ADU',
          value: '30000',
          hint: '§29.3.2 auto-exposure flats',
        ),
      ],
    );
  }
}
