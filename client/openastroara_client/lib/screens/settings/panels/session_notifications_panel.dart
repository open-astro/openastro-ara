import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §54 Notifications. 12h.2 read-only.
class SessionNotificationsPanel extends StatelessWidget {
  const SessionNotificationsPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Channels'),
        SettingsRow(label: 'In-app banner', value: 'On'),
        SettingsRow(label: 'OS desktop notification', value: 'On'),
        SettingsRow(label: 'Sound alert (§35 alarm)', value: 'On'),
        SettingsRow(label: 'Pushover token', value: 'Not configured'),
        SettingsRow(label: 'Telegram bot', value: 'Not configured'),
        SettingsSectionHeader('Trigger on'),
        SettingsRow(label: 'Sequence complete', value: 'On'),
        SettingsRow(label: 'Sequence paused', value: 'On'),
        SettingsRow(label: 'Critical diagnostic', value: 'On'),
        SettingsRow(label: 'Safety event', value: 'On'),
        SettingsRow(label: 'Autofocus failed', value: 'On'),
        SettingsRow(label: 'Plate solve failed (×N)', value: 'On (after 3)'),
        SettingsRow(label: 'Disk space low', value: 'On (<10 GB)'),
      ],
    );
  }
}
