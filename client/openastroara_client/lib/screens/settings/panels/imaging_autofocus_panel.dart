import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/autofocus_settings_state.dart';
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
        SettingsDropdownRow<AutofocusMethod>(
          label: 'Method',
          helpKey: 'img.autofocus.method',
          value: s.method,
          items: const {
            AutofocusMethod.hfrVCurve: 'HFR (V-curve)',
            AutofocusMethod.brightestStarHfr: 'Brightest-star HFR',
            AutofocusMethod.fwhm: 'FWHM (Gaussian fit)',
          },
          onChanged: (v) {
            if (v != null) n.setMethod(v);
          },
        ),
        EditableNumberRow(
          label: 'Number of steps (3..31)',
          helpKey: 'img.autofocus.steps',
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
          helpKey: 'img.autofocus.step_size',
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
        SettingsSwitchRow(
          label: 'After filter change',
          value: s.runAfterFilterChange,
          onChanged: n.setRunAfterFilterChange,
        ),
        EditableNumberRow(
          label: 'After temp delta (°C)',
          helpKey: 'img.autofocus.trigger_temp_delta_c',
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
          helpKey: 'img.autofocus.trigger_hfr_drift_pct',
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
          helpKey: 'img.autofocus.every_n_hours',
          currentValue: s.everyNHours.toString(),
          getCanonical: () =>
              ref.read(autofocusSettingsProvider).everyNHours.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setEveryNHours(v);
          },
        ),
        const SettingsSectionHeader('Safety'),
        SettingsSwitchRow(
          label: 'Abort sequence if AF fails',
          value: s.abortSequenceOnAfFailure,
          onChanged: n.setAbortSequenceOnAfFailure,
          hint: '§35 — overrideable by diagnostics-mode policy',
        ),
        SettingsSwitchRow(
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
