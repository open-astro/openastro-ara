import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.5 Guider (PHD2) panel. 12h.2 read-only stub for the PHD2 client
/// settings; the dedicated `eq.phd2` panel (12h.2c) covers
/// openastro-phd2 daemon settings + connection diagnostics.
class EquipmentGuiderPanel extends StatelessWidget {
  const EquipmentGuiderPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('PHD2 connection'),
        SettingsRow(label: 'Host', value: 'localhost'),
        SettingsRow(label: 'Port', value: '4400'),
        SettingsRow(
          label: 'Profile',
          value: 'Default',
          hint: 'PHD2 equipment profile, not OpenAstroAra profile',
        ),
        SettingsSectionHeader('Dithering'),
        SettingsRow(label: 'Enable dithering', value: 'On'),
        SettingsRow(label: 'Dither every N frames', value: '1'),
        SettingsRow(label: 'Dither pixels', value: '5'),
        SettingsRow(label: 'Settle pixels', value: '1.5'),
        SettingsRow(label: 'Settle time (s)', value: '10'),
        SettingsRow(label: 'Settle timeout (s)', value: '60'),
        SettingsSectionHeader('Calibration'),
        SettingsRow(label: 'Force calibration each session', value: 'Off'),
        SettingsRow(
          label: 'Re-calibrate on meridian flip',
          value: 'On',
          hint: '§38.7',
        ),
      ],
    );
  }
}
