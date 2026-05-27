import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37 + §42 Active profile panel. 12h.2 read-only.
class ProfileActivePanel extends StatelessWidget {
  const ProfileActivePanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Profile'),
        const SettingsRow(label: 'Name', value: 'Default'),
        const SettingsRow(label: 'Created', value: '2026-05-26 22:10 PDT'),
        const SettingsRow(label: 'Last modified', value: '2026-05-26 22:10 PDT'),
        const SettingsRow(label: 'Profile ID', value: 'profile-default'),
        const SettingsSectionHeader('Profiles on this server'),
        const SettingsRow(
          label: 'Available',
          value: 'Default (active)',
          hint: '§42 multi-profile UI lands in Phase 12h.2c',
        ),
        const SizedBox(height: 16),
        Wrap(spacing: 8, children: [
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.add, size: 16),
            label: const Text('New profile'),
          ),
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.content_copy, size: 16),
            label: const Text('Duplicate'),
          ),
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.download, size: 16),
            label: const Text('Export JSON'),
          ),
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.upload, size: 16),
            label: const Text('Import JSON'),
          ),
          OutlinedButton.icon(
            onPressed: null,
            icon: const Icon(Icons.delete_outline,
                size: 16, color: AraColors.accentBusy),
            label: const Text('Delete'),
          ),
        ]),
      ],
    );
  }
}
