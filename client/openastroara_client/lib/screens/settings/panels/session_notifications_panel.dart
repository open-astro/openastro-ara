import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/notifications_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/settings_row.dart';

/// §54 Notifications panel — editable form wired to
/// `notificationsSettingsProvider`. Daemon round-trip via
/// `/api/v1/profile/notifications` lands in 12h.2b.
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
        _SwitchRow(
          label: 'In-app banner',
          value: s.inAppBanner,
          onChanged: n.setInAppBanner,
        ),
        _SwitchRow(
          label: 'OS desktop notification',
          value: s.osDesktop,
          onChanged: n.setOsDesktop,
        ),
        _SwitchRow(
          label: 'Sound alert (§35 alarm)',
          value: s.soundAlert,
          onChanged: n.setSoundAlert,
        ),
        _TokenField(
          label: 'Pushover token',
          initialValue: s.pushoverToken,
          parse: n.setPushoverToken,
          hint: 'Empty = disabled',
        ),
        _TokenField(
          label: 'Telegram bot token',
          initialValue: s.telegramBotToken,
          parse: n.setTelegramBotToken,
          hint: 'Empty = disabled',
        ),
        const SettingsSectionHeader('Trigger on'),
        _SwitchRow(
          label: 'Sequence complete',
          value: s.onSequenceComplete,
          onChanged: n.setOnSequenceComplete,
        ),
        _SwitchRow(
          label: 'Sequence paused',
          value: s.onSequencePaused,
          onChanged: n.setOnSequencePaused,
        ),
        _SwitchRow(
          label: 'Critical diagnostic',
          value: s.onCriticalDiagnostic,
          onChanged: n.setOnCriticalDiagnostic,
        ),
        _SwitchRow(
          label: 'Safety event',
          value: s.onSafetyEvent,
          onChanged: n.setOnSafetyEvent,
        ),
        _SwitchRow(
          label: 'Autofocus failed',
          value: s.onAutofocusFailed,
          onChanged: n.setOnAutofocusFailed,
        ),
        _SwitchRow(
          label: 'Plate solve failed (×N)',
          value: s.onPlateSolveFailed,
          onChanged: n.setOnPlateSolveFailed,
        ),
        _SwitchRow(
          label: 'Disk space low (<10 GB)',
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

class _SwitchRow extends StatelessWidget {
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text(label,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}

/// Token field — StatefulWidget owning its own TextEditingController +
/// FocusNode per the PR #63 contract.
class _TokenField extends StatefulWidget {
  final String label;
  final String initialValue;
  final void Function(String) parse;
  final String? hint;
  const _TokenField({
    required this.label,
    required this.initialValue,
    required this.parse,
    this.hint,
  });

  @override
  State<_TokenField> createState() => _TokenFieldState();
}

class _TokenFieldState extends State<_TokenField> {
  late final TextEditingController _controller;
  late final FocusNode _focusNode;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.initialValue);
    _focusNode = FocusNode();
    _focusNode.addListener(() {
      if (!_focusNode.hasFocus) widget.parse(_controller.text);
    });
  }

  @override
  void dispose() {
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text(widget.label,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Expanded(
          child: TextField(
            controller: _controller,
            focusNode: _focusNode,
            decoration: InputDecoration(
              isDense: true,
              hintText: widget.hint,
            ),
            onSubmitted: widget.parse,
          ),
        ),
      ]),
    );
  }
}
