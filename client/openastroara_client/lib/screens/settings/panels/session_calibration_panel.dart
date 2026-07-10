import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §48 Session → Calibration panel — the sequence-start "capture calibration
/// tonight?" default. The value rides the profile's safety-policies document
/// (same daemon round-trip as Safety → Policies): hydrate on mount, persist
/// on Save.
class SessionCalibrationPanel extends ConsumerStatefulWidget {
  const SessionCalibrationPanel({super.key});

  @override
  ConsumerState<SessionCalibrationPanel> createState() =>
      _SessionCalibrationPanelState();
}

class _SessionCalibrationPanelState
    extends ConsumerState<SessionCalibrationPanel> {
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
      await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) {
        setState(() => _lastError = 'Could not load saved values: $e');
      }
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
      await ref.read(safetyPoliciesProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Calibration preference saved to daemon.')),
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
    final s = ref.watch(safetyPoliciesProvider);
    final n = ref.read(safetyPoliciesProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('End-of-night calibration (§48)'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'When a sequence completes, Ara can generate matching flats from '
            'that night\'s own session — every filter replaying the night\'s '
            'focus, gain, and offset. "Ask" prompts once at each sequence '
            'start; panel flats start automatically when the run ends (light '
            'your panel when notified); sky flats are generated ready to run '
            'at twilight.',
            style: Theme.of(context)
                .textTheme
                .bodyMedium
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        SettingsDropdownRow<CalibrationCaptureDefault>(
          label: 'Capture calibration after a sequence',
          helpKey: 'session.calibration.capture_default',
          value: s.calibrationCaptureDefault,
          items: const {
            CalibrationCaptureDefault.ask: 'Ask at each sequence start',
            CalibrationCaptureDefault.panelAtEnd: 'Panel flats at end',
            CalibrationCaptureDefault.skyAtTwilight: 'Sky flats at twilight',
            CalibrationCaptureDefault.never: 'Never',
          },
          onChanged: (v) {
            if (v != null) n.setCalibrationCaptureDefault(v);
          },
        ),
        // §48.7 flat_panel — the auto-exposure flat sets the generated
        // sequences run (each filter probes exposure to the target ADU,
        // then captures this many frames).
        const SettingsSectionHeader('Flat sets'),
        EditableNumberRow(
          label: 'Target brightness (mean ADU)',
          helpKey: 'session.calibration.flat_target_adu',
          currentValue: s.flatTargetAdu.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).flatTargetAdu.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setFlatTargetAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Target tolerance (%)',
          helpKey: 'session.calibration.flat_target_adu_tolerance_pct',
          currentValue: s.flatTargetAduTolerancePct.toString(),
          getCanonical: () => ref
              .read(safetyPoliciesProvider)
              .flatTargetAduTolerancePct
              .toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setFlatTargetAduTolerancePct(v);
          },
        ),
        EditableNumberRow(
          label: 'Frames per filter',
          helpKey: 'session.calibration.flat_frames_per_filter',
          currentValue: s.flatFramesPerFilter.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).flatFramesPerFilter.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setFlatFramesPerFilter(v);
          },
        ),
        SettingsSwitchRow(
          label: 'Park the mount after flats',
          helpKey: 'session.calibration.post_flat_park_mount',
          value: s.postFlatParkMount,
          onChanged: n.setPostFlatParkMount,
        ),
        // §48.4 sky_flat — twilight flats. The generated sequence waits for the
        // sun to reach the altitude below, slews to the sky patch, then re-probes
        // the sky before every frame and stops outside the ADU window.
        const SettingsSectionHeader('Sky flats (twilight)'),
        EditableNumberRow(
          label: 'Target brightness (mean ADU)',
          helpKey: 'session.calibration.sky_flat_target_adu',
          currentValue: s.skyFlatTargetAdu.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatTargetAdu.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setSkyFlatTargetAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Frames per filter',
          helpKey: 'session.calibration.sky_flat_frames_per_filter',
          currentValue: s.skyFlatFramesPerFilter.toString(),
          getCanonical: () => ref
              .read(safetyPoliciesProvider)
              .skyFlatFramesPerFilter
              .toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setSkyFlatFramesPerFilter(v);
          },
        ),
        EditableNumberRow(
          label: 'Sky patch azimuth (deg)',
          helpKey: 'session.calibration.sky_flat_target_azimuth',
          currentValue: s.skyFlatTargetAzimuth.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatTargetAzimuth.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatTargetAzimuth(v);
          },
        ),
        EditableNumberRow(
          label: 'Sky patch altitude (deg)',
          helpKey: 'session.calibration.sky_flat_target_altitude',
          currentValue: s.skyFlatTargetAltitude.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatTargetAltitude.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatTargetAltitude(v);
          },
        ),
        EditableNumberRow(
          label: 'Stop above (ADU)',
          helpKey: 'session.calibration.sky_flat_stop_at_max_adu',
          currentValue: s.skyFlatStopAtMaxAdu.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatStopAtMaxAdu.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatStopAtMaxAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Stop below (ADU)',
          helpKey: 'session.calibration.sky_flat_stop_at_min_adu',
          currentValue: s.skyFlatStopAtMinAdu.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatStopAtMinAdu.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatStopAtMinAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Wait for sun altitude (deg)',
          helpKey: 'session.calibration.sky_flat_sun_altitude',
          currentValue: s.skyFlatSunAltitude.toString(),
          getCanonical: () =>
              ref.read(safetyPoliciesProvider).skyFlatSunAltitude.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatSunAltitude(v);
          },
        ),
        if (_lastError != null)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 6),
            child: Text(
              _lastError!,
              style: TextStyle(
                color: Theme.of(context).colorScheme.error,
                fontSize: 12,
              ),
            ),
          ),
        const SizedBox(height: 16),
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
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
          ],
        ),
      ],
    );
  }
}
