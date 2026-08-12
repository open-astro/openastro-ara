import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/profile_management_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/profile/profile_import_flow.dart';
import '../../../widgets/settings/settings_row.dart';
import '../profile_management_screen.dart';

/// §37 + §42 Active profile panel: the live active-profile identity plus the
/// door into the full multi-profile management screen (select / rename /
/// delete / add via the wizard / import / export).
class ProfileActivePanel extends ConsumerWidget {
  const ProfileActivePanel({super.key});

  static String _stamp(DateTime? utc) {
    if (utc == null) {
      return '—';
    }
    final local = utc.toLocal();
    String two(int v) => v.toString().padLeft(2, '0');
    return '${local.year}-${two(local.month)}-${two(local.day)} '
        '${two(local.hour)}:${two(local.minute)}';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(profileManagementProvider);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Profile'),
        ...async.when(
          loading: () => const [
            SettingsRow(
              label: 'Name',
              helpKey: 'profile.active.name',
              value: 'Loading…',
            ),
          ],
          error: (e, _) => [
            SettingsRow(
              label: 'Profiles',
              helpKey: 'profile.active.available',
              value: 'Unavailable',
              hint: friendlyDaemonError(e, fallback: "Couldn't load the active profile"),
            ),
          ],
          data: (list) {
            final active = list.active;
            return [
              SettingsRow(
                label: 'Name',
                helpKey: 'profile.active.name',
                value: active?.name ?? '—',
              ),
              SettingsRow(
                label: 'Created',
                helpKey: 'profile.active.metadata',
                value: _stamp(active?.createdUtc),
              ),
              SettingsRow(
                label: 'Last modified',
                helpKey: 'profile.active.metadata',
                value: _stamp(active?.updatedUtc),
              ),
              SettingsRow(
                label: 'Profile ID',
                helpKey: 'profile.active.metadata',
                value: active?.id ?? '—',
              ),
              const SettingsSectionHeader('Profiles on this server'),
              SettingsRow(
                label: 'Available',
                helpKey: 'profile.active.available',
                value: list.profiles.isEmpty
                    ? 'None'
                    : list.profiles
                          .map(
                            (p) => p.id == list.activeId
                                ? '${p.name} (active)'
                                : p.name,
                          )
                          .join(', '),
              ),
            ];
          },
        ),
        const SizedBox(height: 16),
        Row(
          children: [
            FilledButton.icon(
              onPressed: () => Navigator.of(context).push<void>(
                MaterialPageRoute(
                  builder: (_) => const ProfileManagementScreen(),
                ),
              ),
              icon: const Icon(Icons.manage_accounts, size: 18),
              label: const Text('Manage profiles…'),
            ),
            const SizedBox(width: 12),
            Flexible(
              child: Text(
                'Add, select, rename, delete, import and export profiles.',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
              ),
            ),
          ],
        ),
      ],
    );
  }
}
