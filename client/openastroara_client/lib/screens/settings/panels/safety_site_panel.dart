import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.12 Site preferences. 12h.2 read-only.
class SafetySitePanel extends StatelessWidget {
  const SafetySitePanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Location'),
        SettingsRow(label: 'Site name', value: 'Backyard'),
        SettingsRow(label: 'Latitude', value: '+34.0522°'),
        SettingsRow(label: 'Longitude', value: '−118.2437°'),
        SettingsRow(label: 'Elevation (m)', value: '90'),
        SettingsRow(label: 'Time zone', value: 'America/Los_Angeles'),
        SettingsSectionHeader('Horizon'),
        SettingsRow(label: 'Horizon profile', value: 'Default (20° flat)'),
        SettingsRow(
          label: 'Custom horizon file',
          value: '— (not loaded)',
          hint: '§36.8 — JSON polygon path; loaded via Sky Atlas',
        ),
        SettingsSectionHeader('Conditions defaults'),
        SettingsRow(label: 'Bortle class', value: '6 (Bright suburban)'),
        SettingsRow(label: 'Typical seeing (″)', value: '2.5'),
        SettingsRow(
          label: 'Twilight definition',
          value: 'Astronomical (−18°)',
        ),
      ],
    );
  }
}
