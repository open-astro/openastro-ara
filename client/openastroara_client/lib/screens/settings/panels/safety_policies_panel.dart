import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/settings_row.dart';

/// §35 Safety Policies panel — editable. Daemon round-trip via
/// `/api/v1/profile/safety` lands in 12h.2b.
class SafetyPoliciesPanel extends ConsumerWidget {
  const SafetyPoliciesPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(safetyPoliciesProvider);
    final n = ref.read(safetyPoliciesProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('On unsafe weather'),
        _DropdownRow<UnsafeAction>(
          label: 'Action',
          value: s.onUnsafe,
          items: const {
            UnsafeAction.pauseAndPark: 'Pause + park + close dome',
            UnsafeAction.parkOnly: 'Park only',
            UnsafeAction.abortAndPark: 'Abort sequence + park',
            UnsafeAction.ignore: 'Ignore (not recommended)',
          },
          onChanged: (v) {
            if (v != null) n.setOnUnsafe(v);
          },
        ),
        _SwitchRow(
          label: 'Auto-resume when safe',
          value: s.autoResumeWhenSafe,
          onChanged: n.setAutoResumeWhenSafe,
        ),
        _NumberRow(
          label: 'Resume delay (min)',
          initialValue: s.resumeDelayMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setResumeDelayMin(v);
          },
        ),
        const SettingsSectionHeader('On meridian flip'),
        _SwitchRow(
          label: 'Auto flip',
          value: s.meridianFlipAuto,
          onChanged: n.setMeridianFlipAuto,
        ),
        _NumberRow(
          label: 'Pause after flip (min)',
          initialValue: s.meridianPauseMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMeridianPauseMin(v);
          },
        ),
        _SwitchRow(
          label: 'Re-center after flip',
          value: s.meridianRecenter,
          onChanged: n.setMeridianRecenter,
        ),
        _SwitchRow(
          label: 'Re-calibrate guider after flip',
          value: s.meridianRecalGuider,
          onChanged: n.setMeridianRecalGuider,
        ),
        const SettingsSectionHeader('On altitude limit'),
        _DropdownRow<AltitudeLimitAction>(
          label: 'Action',
          value: s.onAltitudeLimit,
          items: const {
            AltitudeLimitAction.skipTarget: 'Skip + advance to next target',
            AltitudeLimitAction.pauseSequence: 'Pause sequence',
            AltitudeLimitAction.abortSequence: 'Abort sequence',
          },
          onChanged: (v) {
            if (v != null) n.setOnAltitudeLimit(v);
          },
        ),
        _SwitchRow(
          label: 'Park if no more targets',
          value: s.parkIfNoMoreTargets,
          onChanged: n.setParkIfNoMoreTargets,
        ),
        const SettingsSectionHeader('On guider lost'),
        _DropdownRow<GuiderLostAction>(
          label: 'Action',
          value: s.onGuiderLost,
          items: const {
            GuiderLostAction.pauseAndRetry: 'Pause + retry once',
            GuiderLostAction.skipTarget: 'Skip target',
            GuiderLostAction.abortSequence: 'Abort sequence',
          },
          onChanged: (v) {
            if (v != null) n.setOnGuiderLost(v);
          },
        ),
        _NumberRow(
          label: 'Retry timeout (s)',
          initialValue: s.guiderRetryTimeoutSec.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setGuiderRetryTimeoutSec(v);
          },
        ),
        _SwitchRow(
          label: 'Skip target if recovery fails',
          value: s.skipTargetIfRecoveryFails,
          onChanged: n.setSkipTargetIfRecoveryFails,
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'Safety policies saved (in memory). Daemon round-trip lands in 12h.2b.',
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

class _DropdownRow<T> extends StatelessWidget {
  final String label;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T?> onChanged;
  const _DropdownRow({
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text(label,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Expanded(
          child: DropdownButtonFormField<T>(
            initialValue: value,
            isDense: true,
            items: [
              for (final e in items.entries)
                DropdownMenuItem<T>(value: e.key, child: Text(e.value)),
            ],
            onChanged: onChanged,
          ),
        ),
      ]),
    );
  }
}

class _NumberRow extends StatefulWidget {
  final String label;
  final String initialValue;
  final void Function(String) parse;
  const _NumberRow({
    required this.label,
    required this.initialValue,
    required this.parse,
  });

  @override
  State<_NumberRow> createState() => _NumberRowState();
}

class _NumberRowState extends State<_NumberRow> {
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
            decoration: const InputDecoration(isDense: true),
            onSubmitted: widget.parse,
          ),
        ),
      ]),
    );
  }
}
