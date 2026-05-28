import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/autofocus_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.11 Autofocus panel — editable.
class ImagingAutofocusPanel extends ConsumerWidget {
  const ImagingAutofocusPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(autofocusSettingsProvider);
    final n = ref.read(autofocusSettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Algorithm'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(children: [
            SizedBox(
              width: 280,
              child: Text('Method',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
            ),
            Expanded(
              child: DropdownButtonFormField<AutofocusMethod>(
                initialValue: s.method,
                isDense: true,
                items: const [
                  DropdownMenuItem(
                    value: AutofocusMethod.hfrVCurve,
                    child: Text('HFR (V-curve)'),
                  ),
                  DropdownMenuItem(
                    value: AutofocusMethod.brightestStarHfr,
                    child: Text('Brightest-star HFR'),
                  ),
                  DropdownMenuItem(
                    value: AutofocusMethod.fwhm,
                    child: Text('FWHM (Gaussian fit)'),
                  ),
                ],
                onChanged: (v) {
                  if (v != null) n.setMethod(v);
                },
              ),
            ),
          ]),
        ),
        EditableNumberRow(
          label: 'Number of steps (3..31)',
          currentValue: s.steps.toString(),
          getCanonical: () =>
              ref.read(autofocusSettingsProvider).steps.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setSteps(v);
          },
        ),
        EditableNumberRow(
          label: 'Step size (focuser steps)',
          currentValue: s.stepSize.toString(),
          getCanonical: () =>
              ref.read(autofocusSettingsProvider).stepSize.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setStepSize(v);
          },
        ),
        EditableNumberRow(
          label: 'Exposure time (s)',
          currentValue: s.exposureSeconds.toString(),
          getCanonical: () => ref
              .read(autofocusSettingsProvider)
              .exposureSeconds
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setExposureSeconds(v);
          },
        ),
        EditableNumberRow(
          label: 'Binning',
          currentValue: s.binning.toString(),
          getCanonical: () =>
              ref.read(autofocusSettingsProvider).binning.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setBinning(v);
          },
        ),
        EditableTextRow(
          label: 'Filter for AF',
          currentValue: s.afFilter,
          getCanonical: () => ref.read(autofocusSettingsProvider).afFilter,
          parse: n.setAfFilter,
        ),
        const SettingsSectionHeader('Triggers'),
        _SwitchRow(
          label: 'After filter change',
          value: s.runAfterFilterChange,
          onChanged: n.setRunAfterFilterChange,
        ),
        EditableNumberRow(
          label: 'After temp delta (°C)',
          currentValue: s.triggerTempDeltaC.toString(),
          getCanonical: () => ref
              .read(autofocusSettingsProvider)
              .triggerTempDeltaC
              .toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setTriggerTempDeltaC(v);
          },
        ),
        EditableNumberRow(
          label: 'On HFR drift threshold (%)',
          currentValue: s.triggerHfrDriftPct.toString(),
          getCanonical: () => ref
              .read(autofocusSettingsProvider)
              .triggerHfrDriftPct
              .toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setTriggerHfrDriftPct(v);
          },
        ),
        EditableNumberRow(
          label: 'Every N hours (0 = off)',
          currentValue: s.everyNHours.toString(),
          getCanonical: () =>
              ref.read(autofocusSettingsProvider).everyNHours.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setEveryNHours(v);
          },
        ),
        const SettingsSectionHeader('Safety'),
        _SwitchRow(
          label: 'Abort sequence if AF fails',
          value: s.abortSequenceOnAfFailure,
          onChanged: n.setAbortSequenceOnAfFailure,
          hint: '§35 — overrideable by diagnostics-mode policy',
        ),
        _SwitchRow(
          label: 'Restore position on failure',
          value: s.restorePositionOnFailure,
          onChanged: n.setRestorePositionOnFailure,
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'Autofocus settings saved (in memory). Daemon round-trip lands in 12h.2b.',
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
  final String? hint;
  const _SwitchRow({
    required this.label,
    required this.value,
    required this.onChanged,
    this.hint,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(label,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
              if (hint != null)
                Text(hint!,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        )),
            ],
          ),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}
