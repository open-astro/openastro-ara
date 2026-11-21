import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/safety_policies_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';

/// §48 Session → Calibration panel — the sequence-start "capture calibration
/// tonight?" default. The value rides the profile's safety-policies document
/// (same section as Unattended running): the rig's copy, loaded before it is
/// saved (ADR 0015), and saved back on Save.
class SessionCalibrationPanel extends ConsumerStatefulWidget {
  const SessionCalibrationPanel({super.key});

  @override
  ConsumerState<SessionCalibrationPanel> createState() =>
      _SessionCalibrationPanelState();
}

class _SessionCalibrationPanelState
    extends ConsumerState<SessionCalibrationPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;

  @override
  List<PanelSection> get sections => [safetyPoliciesProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    // A field still being edited commits on focus loss; drop focus first so
    // the typed value is what gets sent.
    FocusManager.instance.primaryFocus?.unfocus();
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final s = ref.watch(safetyPoliciesProvider).value!;
    final n = ref.read(safetyPoliciesProvider.notifier);
    SafetyPolicies now() => ref.read(safetyPoliciesProvider).value!;

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        const SettingsSectionHeader('End-of-night calibration frames'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'When a sequence completes, Ara can generate matching flats from '
            'that night\'s own session — every filter replaying the night\'s '
            'focus, gain, and offset. "Ask" prompts once at each sequence '
            'start; panel flats start automatically when the run ends (light '
            'your panel when notified); sky flats are generated ready to run '
            'at twilight.',
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
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
        // §48.7 flat_panel — the flat sets the generated sequences run
        // (each filter solves its exposure and panel brightness for this
        // share of the camera's range, then captures this many frames).
        const SettingsSectionHeader('Flat sets'),
        EditableNumberRow(
          label: 'Target brightness (% of camera range)',
          helpKey: 'session.calibration.flat_target_adu',
          currentValue: _percent(s.flatTargetPercent),
          getCanonical: () => _percent(now().flatTargetPercent),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setFlatTargetPercent(v);
          },
        ),
        EditableNumberRow(
          label: 'Target tolerance (%)',
          helpKey: 'session.calibration.flat_target_adu_tolerance_pct',
          currentValue: s.flatTargetAduTolerancePct.toString(),
          getCanonical: () => now().flatTargetAduTolerancePct.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setFlatTargetAduTolerancePct(v);
          },
        ),
        EditableNumberRow(
          label: 'Frames per filter',
          helpKey: 'session.calibration.flat_frames_per_filter',
          currentValue: s.flatFramesPerFilter.toString(),
          getCanonical: () => now().flatFramesPerFilter.toString(),
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
          getCanonical: () => now().skyFlatTargetAdu.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setSkyFlatTargetAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Frames per filter',
          helpKey: 'session.calibration.sky_flat_frames_per_filter',
          currentValue: s.skyFlatFramesPerFilter.toString(),
          getCanonical: () => now().skyFlatFramesPerFilter.toString(),
          parse: (str) {
            final v = int.tryParse(str);
            if (v != null) n.setSkyFlatFramesPerFilter(v);
          },
        ),
        EditableNumberRow(
          label: 'Sky patch azimuth (deg)',
          helpKey: 'session.calibration.sky_flat_target_azimuth',
          currentValue: s.skyFlatTargetAzimuth.toString(),
          getCanonical: () => now().skyFlatTargetAzimuth.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatTargetAzimuth(v);
          },
        ),
        EditableNumberRow(
          label: 'Sky patch altitude (deg)',
          helpKey: 'session.calibration.sky_flat_target_altitude',
          currentValue: s.skyFlatTargetAltitude.toString(),
          getCanonical: () => now().skyFlatTargetAltitude.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatTargetAltitude(v);
          },
        ),
        EditableNumberRow(
          label: 'Stop above (ADU)',
          helpKey: 'session.calibration.sky_flat_stop_at_max_adu',
          currentValue: s.skyFlatStopAtMaxAdu.toString(),
          getCanonical: () => now().skyFlatStopAtMaxAdu.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatStopAtMaxAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Stop below (ADU)',
          helpKey: 'session.calibration.sky_flat_stop_at_min_adu',
          currentValue: s.skyFlatStopAtMinAdu.toString(),
          getCanonical: () => now().skyFlatStopAtMinAdu.toString(),
          parse: (str) {
            final v = double.tryParse(str);
            if (v != null) n.setSkyFlatStopAtMinAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Wait for sun altitude (deg)',
          helpKey: 'session.calibration.sky_flat_sun_altitude',
          currentValue: s.skyFlatSunAltitude.toString(),
          getCanonical: () => now().skyFlatSunAltitude.toString(),
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
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
      ],
    );
  }
}

String _percent(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toString();
