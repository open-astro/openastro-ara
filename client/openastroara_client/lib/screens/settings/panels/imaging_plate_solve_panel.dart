import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.10 Plate Solving panel. 12h.2 read-only.
class ImagingPlateSolvePanel extends StatelessWidget {
  const ImagingPlateSolvePanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Solver'),
        SettingsRow(
          label: 'Engine',
          value: 'astap',
          hint: 'astap / astrometry.net / platesolve2',
        ),
        SettingsRow(label: 'Path / endpoint', value: '/usr/bin/astap'),
        SettingsRow(label: 'Index download path', value: '/var/lib/astap'),
        SettingsSectionHeader('Solving parameters'),
        SettingsRow(label: 'Search radius (°)', value: '30'),
        SettingsRow(label: 'Downsample factor', value: '2'),
        SettingsRow(label: 'Timeout (s)', value: '60'),
        SettingsRow(
          label: 'Use blind solve as fallback',
          value: 'On',
          hint: 'if hint-based solve times out',
        ),
        SettingsSectionHeader('Slew + sync'),
        SettingsRow(label: 'Center after slew', value: 'On'),
        SettingsRow(label: 'Sync to coordinates', value: 'On'),
        SettingsRow(label: 'Max iterations', value: '5'),
        SettingsRow(label: 'Convergence tolerance (″)', value: '60'),
      ],
    );
  }
}
