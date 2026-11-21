import 'package:flutter/material.dart';

import '../../../util/friendly_error.dart';
import '../../../l10n/l10n.dart';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../settings/registry.dart';
import '../../../state/equipment/focuser_state.dart';
import '../../../state/focus/autofocus_step_size_state.dart';
import '../../../state/focus/focus_presets.dart';
import '../../../state/settings/advanced_settings_state.dart';
import '../../../state/settings/autofocus_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/settings_nav.dart';
import '../../../state/simple_mode_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_load_state.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';

// #1544 — each row's tier, from its settings-registry entry.
final _tierById = {for (final s in settingsRegistry) s.id: s.tier};

/// §37.11 Autofocus panel — editable. Phase 12h.6h added the daemon
/// round-trip — values hydrate from the active server on mount and
/// persist back on Save.
class ImagingAutofocusPanel extends ConsumerStatefulWidget {
  const ImagingAutofocusPanel({super.key});

  @override
  ConsumerState<ImagingAutofocusPanel> createState() =>
      _ImagingAutofocusPanelState();
}

class _ImagingAutofocusPanelState extends ConsumerState<ImagingAutofocusPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;
  // #1544: Advanced rows a search hit opened while the toggle is off. They
  // stay until this panel closes; the saved toggle is left alone.
  final Set<String> _revealed = {};

  @override
  void initState() {
    super.initState();
    // A hit picked before this panel opened (the palette selects it first).
    final hit = ref.read(highlightedSettingProvider);
    if (hit != null) _revealed.add(hit);
  }

  @override
  List<PanelSection> get sections => [autofocusSettingsProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    final error = await saveSections();
    // The daemon derives the step size from what is now saved.
    if (error == null && mounted) ref.invalidate(autofocusStepSizeProvider);
    if (mounted) setState(() => _lastError = error);
  }

  Future<void> _resetCalibration() async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialog) => AlertDialog(
        title: Text(l10n.focusCalibrationResetAsk),
        content: Text(l10n.focusCalibrationResetHint),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialog).pop(false),
            child: Text(l10n.focusCalibrationResetCancel),
          ),
          FilledButton(
            key: const Key('af-reset-calibration-confirm'),
            onPressed: () => Navigator.of(dialog).pop(true),
            child: Text(l10n.focusCalibrationResetConfirm),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      await ref.read(autofocusApiProvider)?.recalibrate();
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.focusCalibrationResetDone)),
      );
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _lastError = friendlyError(
          context.l10n,
          e,
          action: l10n.focusCalibrationResetAction,
        ),
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  static String _sharpName(AppLocalizations l10n, KeepFocusSharp p) =>
      switch (p) {
        KeepFocusSharp.relaxed => l10n.deviceKeepSharpRelaxed,
        KeepFocusSharp.normal => l10n.deviceKeepSharpNormal,
        KeepFocusSharp.tight => l10n.deviceKeepSharpTight,
        KeepFocusSharp.custom => l10n.deviceKeepSharpCustom,
      };

  @override
  Widget build(BuildContext context) {
    // The reset below is not the profile section: it shows whether or not the
    // section loaded.
    final gate = sectionGate();
    final s = ref.watch(autofocusSettingsProvider).value;
    final n = ref.read(autofocusSettingsProvider.notifier);
    final l10n = context.l10n;
    final derivedStep = ref.watch(autofocusStepSizeProvider).value;
    final sharp = s == null ? null : keepFocusSharpOf(s);
    final setBy = sharp == null
        ? null
        : l10n.autofocusTriggerSetBy(_sharpName(l10n, sharp));
    // #1544: a row shows by its registry tier: Simple always, Advanced with
    // the global toggle on or once a search hit lands on it.
    // #2036: Advanced Mode has no toggle and shows every tier.
    final advanced =
        !ref.watch(simpleModeActiveProvider) ||
        (ref.watch(advancedSettingsProvider).value ?? true);
    ref.listen(highlightedSettingProvider, (_, hit) {
      if (hit != null) setState(() => _revealed.add(hit));
    });
    bool shows(String id) =>
        _tierById[id]! == SettingTier.simple ||
        advanced ||
        _revealed.contains(id);
    // The rows of one section that show, under its header when any do.
    List<Widget> section(String? title, Map<String, Widget> rows) {
      final shown = [
        for (final e in rows.entries)
          if (shows(e.key)) e.value,
      ];
      return [
        if (title != null && shown.isNotEmpty) SettingsSectionHeader(title),
        ...shown,
      ];
    }

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        if (gate != null || s == null)
          SectionNotLoaded(
            section: ref.watch(autofocusSettingsProvider),
            padding: const EdgeInsets.symmetric(vertical: 12),
            onRetry: loadSections,
          )
        else ...[
          ?sectionConflict(),
          // Simple tier: what Simple Mode's Focuser window offers, kept in step
          // with it (same four triggers, same profile).
          ...section(null, {
            'img.autofocus.during_plans': SettingsSwitchRow(
              label: l10n.deviceAutofocusDuringPlans,
              value: autofocusDuringPlans(s),
              onChanged: n.setAutofocusDuringPlans,
              hint: autofocusDuringPlans(s)
                  ? l10n.deviceAutofocusOnHint
                  : l10n.deviceAutofocusOffHint,
            ),
            if (sharp != null)
              'img.autofocus.keep_focus_sharp':
                  SettingsDropdownRow<KeepFocusSharp>(
                    label: l10n.deviceKeepFocusSharp,
                    value: sharp,
                    items: {
                      KeepFocusSharp.relaxed: l10n.deviceKeepSharpRelaxed,
                      KeepFocusSharp.normal: l10n.deviceKeepSharpNormal,
                      KeepFocusSharp.tight: l10n.deviceKeepSharpTight,
                      // Triggers that match no preset; picking one overwrites them.
                      if (sharp == KeepFocusSharp.custom)
                        KeepFocusSharp.custom: l10n.deviceKeepSharpCustom,
                    },
                    onChanged: (v) {
                      if (v != null) n.applyKeepFocusSharp(v);
                    },
                  ),
            'img.autofocus.af_filter': EditableTextRow(
              label: 'Filter for AF',
              helpKey: 'img.autofocus.filter',
              currentValue: s.afFilter,
              getCanonical: () =>
                  ref.read(autofocusSettingsProvider).value?.afFilter ?? '',
              parse: n.setAfFilter,
            ),
          }),
          ...section('Algorithm', {
            'img.autofocus.method': SettingsDropdownRow<AutofocusMethod>(
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
            'img.autofocus.telescope_type': SettingsDropdownRow<TelescopeType>(
              label: 'Telescope type',
              helpKey: 'img.autofocus.telescope_type',
              value: s.telescopeType,
              items: const {
                TelescopeType.refractor: 'Refractor',
                TelescopeType.sct: 'Schmidt-Cassegrain (SCT)',
                TelescopeType.mak: 'Maksutov-Cassegrain',
                TelescopeType.rc: 'Ritchey-Chrétien (RC)',
                TelescopeType.newtonian: 'Newtonian',
                TelescopeType.other: 'Other / unknown',
              },
              onChanged: (v) {
                if (v != null) n.setTelescopeType(v);
              },
            ),
            // §59.8 — the sweep sizes itself from the V-curve it measured; the
            // raw knobs (how many probes, how far apart) are Advanced tier.
            'img.autofocus.step_size_auto': SettingsSwitchRow(
              label: 'Automatic step size',
              helpKey: 'img.autofocus.step_size_auto',
              value: s.stepSizeAuto,
              onChanged: n.setStepSizeAuto,
              hint: s.stepSizeAuto
                  ? 'First sweep wide, then sized from the measured V-curve'
                  : 'Every sweep uses the step size below',
            ),
            'img.autofocus.exposure_seconds': EditableNumberRow(
              label: 'Exposure time (s)',
              helpKey: 'img.autofocus.exposure',
              currentValue: s.exposureSeconds.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.exposureSeconds
                      .toString() ??
                  '',
              parse: (str) {
                final v = int.tryParse(str);
                if (v != null) n.setExposureSeconds(v);
              },
            ),
            'img.autofocus.binning': EditableNumberRow(
              label: 'Binning',
              helpKey: 'img.autofocus.binning',
              currentValue: s.binning.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.binning
                      .toString() ??
                  '',
              parse: (str) {
                final v = int.tryParse(str);
                if (v != null) n.setBinning(v);
              },
            ),
            'img.autofocus.steps': EditableNumberRow(
              label: 'Number of steps (3..31)',
              helpKey: 'img.autofocus.steps',
              currentValue: s.steps.toString(),
              getCanonical: () =>
                  ref.read(autofocusSettingsProvider).value?.steps.toString() ??
                  '',
              parse: (str) {
                final v = int.tryParse(str);
                if (v != null) n.setSteps(v);
              },
            ),
            'img.autofocus.step_size': EditableNumberRow(
              label: s.stepSizeAuto
                  ? 'First-sweep step size (focuser steps)'
                  : 'Step size (focuser steps)',
              helpKey: 'img.autofocus.step_size',
              hint: derivedStep == null
                  ? null
                  : derivedStepSizeText(l10n, derivedStep),
              currentValue: s.stepSize.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.stepSize
                      .toString() ??
                  '',
              parse: (str) {
                final v = int.tryParse(str);
                if (v != null) n.setStepSize(v);
              },
            ),
          }),
          ...section('Triggers', {
            'img.autofocus.run_after_filter_change': SettingsSwitchRow(
              label: 'After filter change',
              hint: setBy,
              value: s.runAfterFilterChange,
              onChanged: n.setRunAfterFilterChange,
            ),
            'img.autofocus.trigger_temp_delta_c': EditableNumberRow(
              label: 'After temp delta (°C)',
              hint: setBy,
              helpKey: 'img.autofocus.trigger_temp_delta_c',
              currentValue: s.triggerTempDeltaC.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.triggerTempDeltaC
                      .toString() ??
                  '',
              parse: (str) {
                final v = double.tryParse(str);
                if (v != null) n.setTriggerTempDeltaC(v);
              },
            ),
            'img.autofocus.trigger_hfr_drift_pct': EditableNumberRow(
              label: 'On HFR drift threshold (%)',
              hint: setBy,
              helpKey: 'img.autofocus.trigger_hfr_drift_pct',
              currentValue: s.triggerHfrDriftPct.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.triggerHfrDriftPct
                      .toString() ??
                  '',
              parse: (str) {
                final v = double.tryParse(str);
                if (v != null) n.setTriggerHfrDriftPct(v);
              },
            ),
            'img.autofocus.every_n_hours': EditableNumberRow(
              label: 'Every N hours (0 = off)',
              hint: setBy,
              helpKey: 'img.autofocus.every_n_hours',
              currentValue: s.everyNHours.toString(),
              getCanonical: () =>
                  ref
                      .read(autofocusSettingsProvider)
                      .value
                      ?.everyNHours
                      .toString() ??
                  '',
              parse: (str) {
                final v = int.tryParse(str);
                if (v != null) n.setEveryNHours(v);
              },
            ),
          }),
          ...section('Safety', {
            'img.autofocus.abort_sequence_on_af_failure': SettingsSwitchRow(
              label: 'Abort sequence if AF fails',
              value: s.abortSequenceOnAfFailure,
              onChanged: n.setAbortSequenceOnAfFailure,
              hint: 'Safety rules can override this',
            ),
            'img.autofocus.restore_position_on_failure': SettingsSwitchRow(
              label: 'Restore position on failure',
              value: s.restorePositionOnFailure,
              onChanged: n.setRestorePositionOnFailure,
            ),
          }),
        ],
        if (shows('img.autofocus.reset_calibration')) ...[
          SettingsSectionHeader(context.l10n.focusCalibrationSection),
          Align(
            alignment: Alignment.centerLeft,
            child: OutlinedButton(
              key: const Key('af-reset-calibration'),
              onPressed: ref.watch(autofocusApiProvider) == null
                  ? null
                  : _resetCalibration,
              child: Text(context.l10n.focusCalibrationReset),
            ),
          ),
          const SizedBox(height: 4),
          Text(
            context.l10n.focusCalibrationResetHint,
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ],
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
