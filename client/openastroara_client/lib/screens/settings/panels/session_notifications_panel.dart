import 'package:flutter/material.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/alarm/safety_alarm_state.dart';
import '../../../state/settings/notifications_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/notifications/notification_center.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';

/// §54 Notifications panel — editable form wired to
/// `notificationsSettingsProvider`. Phase 12h.6d added the daemon round-trip
/// — values hydrate from the active server on mount and persist back on Save.
class SessionNotificationsPanel extends ConsumerStatefulWidget {
  const SessionNotificationsPanel({super.key});

  @override
  ConsumerState<SessionNotificationsPanel> createState() =>
      _SessionNotificationsPanelState();
}

class _SessionNotificationsPanelState
    extends ConsumerState<SessionNotificationsPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;

  @override
  List<PanelSection> get sections => [notificationsSettingsProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final s = ref.watch(notificationsSettingsProvider).value!;
    final n = ref.read(notificationsSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        // Choosing what you get told is only half of it — the other half is
        // reading it later, which lives behind the bell.
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: () => showNotificationCenter(context),
            icon: const Icon(Icons.notifications_none, size: 16),
            label: const Text('Read your notifications'),
          ),
        ),
        const SizedBox(height: 8),
        const SettingsSectionHeader('Channels'),
        SettingsSwitchRow(
          label: 'Play a sound for safety alerts',
          value: s.soundAlert,
          onChanged: n.setSoundAlert,
        ),
        if (s.soundAlert)
          Consumer(
            builder: (context, ref, _) {
              final alarm = ref.watch(safetyAlarmProvider);
              final an = ref.read(safetyAlarmProvider.notifier);
              return Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  EditableNumberRow(
                    label: 'Alarm delay (s of silent popup first)',
                    helpKey: 'session.notifications.alarm_delay',
                    currentValue: alarm.delaySec.toString(),
                    getCanonical: () =>
                        ref.read(safetyAlarmProvider).delaySec.toString(),
                    parse: (str) {
                      final v = int.tryParse(str);
                      if (v != null) an.setDelaySec(v);
                    },
                  ),
                  SettingsDropdownRow<String>(
                    label: 'Alarm tone',
                    helpKey: 'session.notifications.alarm_tone',
                    value: alarm.tone,
                    items: const {
                      'siren': 'Two-tone siren',
                      'beeps': 'Urgent beeps',
                      'chime': 'Rising chime',
                    },
                    onChanged: (v) {
                      if (v != null) an.setTone(v);
                    },
                  ),
                ],
              );
            },
          ),
        EditableTextRow(
          label: 'Pushover app token',
          helpKey: 'session.notifications.pushover_token',
          hint: 'Empty = disabled',
          currentValue: s.pushoverToken,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).value!.pushoverToken,
          parse: n.setPushoverToken,
        ),
        EditableTextRow(
          label: 'Pushover user key',
          helpKey: 'session.notifications.pushover_user_key',
          hint: 'Both Pushover fields must be set for pushes to send',
          currentValue: s.pushoverUserKey,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).value!.pushoverUserKey,
          parse: n.setPushoverUserKey,
        ),
        EditableTextRow(
          label: 'Telegram bot token',
          helpKey: 'session.notifications.telegram_bot_token',
          hint: 'Empty = disabled',
          currentValue: s.telegramBotToken,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).value!.telegramBotToken,
          parse: n.setTelegramBotToken,
        ),
        EditableTextRow(
          label: 'Telegram chat ID',
          helpKey: 'session.notifications.telegram_chat_id',
          hint: 'Both Telegram fields must be set for pushes to send',
          currentValue: s.telegramChatId,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).value!.telegramChatId,
          parse: n.setTelegramChatId,
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
          // The threshold itself lives in Storage → "Warn below (GB free)";
          // quoting it live keeps the two panels from contradicting each other.
          label: () {
            final gb = ref
                .watch(storageSettingsProvider)
                .value
                ?.minFreeDiskWarnGb;
            return gb == null
                ? 'Disk space low (threshold in Storage)'
                : 'Disk space low (below $gb GB free)';
          }(),
          helpKey: 'session.notifications.on_disk_space_low',
          value: s.onDiskSpaceLow,
          onChanged: n.setOnDiskSpaceLow,
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
      ],
    );
  }
}
