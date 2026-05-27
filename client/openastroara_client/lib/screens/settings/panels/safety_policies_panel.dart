import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §35 Safety policies. 12h.2 read-only.
class SafetyPoliciesPanel extends StatelessWidget {
  const SafetyPoliciesPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('On unsafe weather'),
        SettingsRow(label: 'Action', value: 'Pause + park + close dome'),
        SettingsRow(label: 'Auto-resume when safe', value: 'On'),
        SettingsRow(label: 'Resume delay (min)', value: '10'),
        SettingsSectionHeader('On meridian flip'),
        SettingsRow(label: 'Action', value: 'Auto flip'),
        SettingsRow(label: 'Pause minutes after flip', value: '5'),
        SettingsRow(label: 'Re-center after flip', value: 'On'),
        SettingsRow(label: 'Re-calibrate guider after flip', value: 'On'),
        SettingsSectionHeader('On altitude limit'),
        SettingsRow(label: 'Action', value: 'Skip + advance to next target'),
        SettingsRow(label: 'Park if no more targets', value: 'On'),
        SettingsSectionHeader('On guider lost'),
        SettingsRow(label: 'Action', value: 'Pause + retry once'),
        SettingsRow(label: 'Retry timeout (s)', value: '60'),
        SettingsRow(label: 'Skip target if recovery fails', value: 'On'),
        SettingsSectionHeader('Session end'),
        SettingsRow(
          label: 'On session complete',
          value: 'Park + warm cooler + close dome',
          hint: '§29.3.2 + §52 graceful shutdown',
        ),
      ],
    );
  }
}
