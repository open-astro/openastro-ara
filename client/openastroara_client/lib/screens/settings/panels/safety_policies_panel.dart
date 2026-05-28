import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
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
        SettingsSwitchRow(
          label: 'Auto-resume when safe',
          value: s.autoResumeWhenSafe,
          onChanged: n.setAutoResumeWhenSafe,
        ),
        _NumberRow(
          label: 'Resume delay (min)',
          currentValue: s.resumeDelayMin.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).resumeDelayMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setResumeDelayMin(v);
          },
        ),
        const SettingsSectionHeader('On meridian flip'),
        SettingsSwitchRow(
          label: 'Auto flip',
          value: s.meridianFlipAuto,
          onChanged: n.setMeridianFlipAuto,
        ),
        _NumberRow(
          label: 'Pause after flip (min)',
          currentValue: s.meridianPauseMin.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).meridianPauseMin.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setMeridianPauseMin(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Re-center after flip',
          value: s.meridianRecenter,
          onChanged: n.setMeridianRecenter,
        ),
        SettingsSwitchRow(
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
        SettingsSwitchRow(
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
          currentValue: s.guiderRetryTimeoutSec.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).guiderRetryTimeoutSec.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setGuiderRetryTimeoutSec(v);
          },
        ),
        SettingsSwitchRow(
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
  // Current state value (passed in on each rebuild so the row updates on
  // external state changes).
  final String currentValue;
  // After parse runs, re-read the notifier's value and resync the
  // controller. This handles the rejected-value case (setter no-ops on
  // invalid input → state didn't change → without resync the field would
  // keep displaying the user's rejected input).
  final String Function() getCanonical;
  final void Function(String) parse;
  const _NumberRow({
    required this.label,
    required this.currentValue,
    required this.getCanonical,
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
    _controller = TextEditingController(text: widget.currentValue);
    _focusNode = FocusNode();
    _focusNode.addListener(() {
      if (!_focusNode.hasFocus) _commit();
    });
  }

  void _commit() {
    widget.parse(_controller.text);
    final canonical = widget.getCanonical();
    if (canonical != _controller.text) {
      _controller.text = canonical;
    }
  }

  @override
  void didUpdateWidget(covariant _NumberRow old) {
    super.didUpdateWidget(old);
    // External state changes (e.g. another panel resetting the value)
    // should refresh the displayed text. Only update when focus is
    // elsewhere — don't yank text out from under the user mid-edit.
    if (!_focusNode.hasFocus && widget.currentValue != _controller.text) {
      _controller.text = widget.currentValue;
    }
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
            onSubmitted: (_) => _commit(),
          ),
        ),
      ]),
    );
  }
}
