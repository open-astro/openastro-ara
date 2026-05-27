import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.11 Autofocus panel. 12h.2 read-only.
class ImagingAutofocusPanel extends StatelessWidget {
  const ImagingAutofocusPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Algorithm'),
        SettingsRow(label: 'Method', value: 'HFR (V-curve)'),
        SettingsRow(label: 'Number of steps', value: '7'),
        SettingsRow(label: 'Step size (focuser steps)', value: '50'),
        SettingsRow(label: 'Exposure time (s)', value: '5'),
        SettingsRow(label: 'Binning', value: '1×1'),
        SettingsRow(label: 'Filter for AF', value: 'L (luminance)'),
        SettingsSectionHeader('Triggers'),
        SettingsRow(label: 'After filter change', value: 'On'),
        SettingsRow(label: 'After temp delta (°C)', value: '2.0'),
        SettingsRow(label: 'On HFR drift threshold (%)', value: '15'),
        SettingsRow(label: 'Every N hours', value: '2'),
        SettingsSectionHeader('Safety'),
        SettingsRow(
          label: 'Abort sequence if AF fails',
          value: 'On',
          hint: '§35 — overrideable by diagnostics-mode policy',
        ),
        SettingsRow(label: 'Restore position on failure', value: 'On'),
      ],
    );
  }
}
