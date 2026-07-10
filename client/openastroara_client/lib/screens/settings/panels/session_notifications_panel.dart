import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/alarm/safety_alarm_state.dart';
import '../../../state/settings/notifications_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
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
    extends ConsumerState<SessionNotificationsPanel> {
  bool _saving = false;
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref
          .read(notificationsSettingsProvider.notifier)
          .hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref
          .read(notificationsSettingsProvider.notifier)
          .persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Notifications saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  @override
  Widget build(BuildContext context) {
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
        if (s.soundAlert)
          Consumer(builder: (context, ref, _) {
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
          }),
        EditableTextRow(
          label: 'Pushover app token',
          helpKey: 'session.notifications.pushover_token',
          hint: 'Empty = disabled',
          currentValue: s.pushoverToken,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).pushoverToken,
          parse: n.setPushoverToken,
        ),
        EditableTextRow(
          label: 'Pushover user key',
          helpKey: 'session.notifications.pushover_user_key',
          hint: 'Both Pushover fields must be set for pushes to send',
          currentValue: s.pushoverUserKey,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).pushoverUserKey,
          parse: n.setPushoverUserKey,
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
        EditableTextRow(
          label: 'Telegram chat ID',
          helpKey: 'session.notifications.telegram_chat_id',
          hint: 'Both Telegram fields must be set for pushes to send',
          currentValue: s.telegramChatId,
          getCanonical: () =>
              ref.read(notificationsSettingsProvider).telegramChatId,
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
          label: 'Disk space low (<10 GB)',
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
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: _saving ? null : _save,
            icon: _saving
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.save, size: 16),
            label: Text(_saving ? 'Saving…' : 'Save'),
          ),
        ]),
      ],
    );
  }
}
