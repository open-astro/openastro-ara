import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/autofocus_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.11 Autofocus panel — editable. Phase 12h.6h added the daemon
/// round-trip — values hydrate from the active server on mount and
/// persist back on Save.
class ImagingAutofocusPanel extends ConsumerStatefulWidget {
  const ImagingAutofocusPanel({super.key});

  @override
  ConsumerState<ImagingAutofocusPanel> createState() =>
      _ImagingAutofocusPanelState();
}

class _ImagingAutofocusPanelState extends ConsumerState<ImagingAutofocusPanel> {
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
          .read(autofocusSettingsProvider.notifier)
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
      await ref.read(autofocusSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Autofocus settings saved to daemon.')),
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
        SettingsDropdownRow<TelescopeType>(
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
