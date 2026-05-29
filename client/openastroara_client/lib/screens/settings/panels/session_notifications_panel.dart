import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/notifications_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §54 Notifications panel — editable form wired to
/// `notificationsSettingsProvider`. Phase 12h.3d registered all 12 fields in
/// `settings/registry.dart` and wired `helpKey`s on the non-obvious controls
/// (pushover/telegram tokens, critical-diagnostic semantics, plate-solve
/// retry budget, disk-space threshold). Local `_TokenField` widget retired
/// in favor of the shared `EditableTextRow` from 12h.3a.
class SessionNotificationsPanel extends ConsumerWidget {
  const SessionNotificationsPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(notificationsSettingsProvider);
    final n = ref.read(notificationsSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Channels'),
        SettingsSwitchRow(
          label: 'In-app banner',
          value: s.inAppBanner,
          onChanged: n.setInAppBanner,
        ),
        SettingsSwitchRow(
          label: 'OS desktop notification',
          value: s.osDesktop,
          onChanged: n.setOsDesktop,
        ),
        SettingsSwitchRow(
          label: 'Sound alert (§35 alarm)',
          value: s.soundAlert,
          onChanged: n.setSoundAlert,
        ),
        EditableTextRow(
          label: 'Pushover token',
          helpKey: 'session.notifications.pushover_token',
          hint: 'Empty = disabled',
          currentValue: s.pushoverToken,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).pushoverToken,
          parse: n.setPushoverToken,
        ),
        EditableTextRow(
          label: 'Telegram bot token',
          helpKey: 'session.notifications.telegram_bot_token',
          hint: 'Empty = disabled',
          currentValue: s.telegramBotToken,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).telegramBotToken,
          parse: n.setTelegramBotToken,
        ),
        const SettingsSectionHeader('Trigger on'),
        SettingsSwitchRow(
          label: 'Sequence complete',
          value: s.onSequenceComplete,
          onChanged: n.setOnSequenceComplete,
        ),
        SettingsSwitchRow(
          label: 'Sequence paused',
          value: s.onSequencePaused,
          onChanged: n.setOnSequencePaused,
        ),
        SettingsSwitchRow(
          label: 'Critical diagnostic',
          helpKey: 'session.notifications.on_critical_diagnostic',
          value: s.onCriticalDiagnostic,
          onChanged: n.setOnCriticalDiagnostic,
        ),
        SettingsSwitchRow(
          label: 'Safety event',
          helpKey: 'session.notifications.on_safety_event',
          value: s.onSafetyEvent,
          onChanged: n.setOnSafetyEvent,
        ),
        SettingsSwitchRow(
          label: 'Autofocus failed',
          value: s.onAutofocusFailed,
          onChanged: n.setOnAutofocusFailed,
        ),
        SettingsSwitchRow(
          label: 'Plate solve failed (×N)',
          helpKey: 'session.notifications.on_plate_solve_failed',
          value: s.onPlateSolveFailed,
          onChanged: n.setOnPlateSolveFailed,
        ),
        SettingsSwitchRow(
          label: 'Disk space low (<10 GB)',
          helpKey: 'session.notifications.on_disk_space_low',
          value: s.onDiskSpaceLow,
          onChanged: n.setOnDiskSpaceLow,
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'Notifications saved (in memory). Daemon round-trip lands in 12h.2b.',
                  ),
                ),
              );
            },
            icon: const Icon(Icons.save, size: 16),
            label: const Text('Save'),
          ),
        ]),
      ],
    );
  }
}
