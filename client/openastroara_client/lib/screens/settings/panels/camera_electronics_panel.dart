import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/camera_electronics_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';

/// NEXTGEN §3/§4 Camera electronics panel — the exposure-planning inputs
/// behind the Optimal-Sub calculator (read noise, full well, e⁻/ADU, gain,
/// QE peak). The ASCOM-sourced values auto-capture when a camera connects
/// (for its CURRENT readout mode); read noise and QE peak are never in ASCOM,
/// so the user enters them from the manufacturer's gain chart or a SharpCap
/// sensor analysis. Hydrates from `camera-electronics` on mount; persists on
/// Save — same daemon round-trip as the Optics panel.
class CameraElectronicsPanel extends ConsumerStatefulWidget {
  const CameraElectronicsPanel({super.key});

  @override
  ConsumerState<CameraElectronicsPanel> createState() =>
      _CameraElectronicsPanelState();
}

class _CameraElectronicsPanelState extends ConsumerState<CameraElectronicsPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;

  @override
  List<PanelSection> get sections => [cameraElectronicsProvider];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final e = ref.watch(cameraElectronicsProvider).value!;
    final n = ref.read(cameraElectronicsProvider.notifier);
    final dim = Theme.of(
      context,
    ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        Text(
          'Inputs for the Optimal Sub exposure calculator (read-noise criterion '
          'popularised by Dr. Robin Glover, SharpCap). Full well, e⁻/ADU, gain '
          'and the sensor name auto-fill from a connected camera for its current '
          'readout mode; read noise and peak QE come from the manufacturer\'s '
          'gain chart or a SharpCap sensor analysis. 0 (or −1 for gain) means '
          'unset — planning then uses generic modern-CMOS defaults and says so.',
          style: dim,
        ),
        const SizedBox(height: 12),
        if (e.autoCaptured)
          Padding(
            padding: const EdgeInsets.only(bottom: 4),
            child: Row(
              children: [
                const Icon(
                  Icons.camera_alt_outlined,
                  size: 14,
                  color: AraColors.accentInfo,
                ),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    'Auto-captured from the connected camera'
                    '${e.sensorName.isNotEmpty ? " (${e.sensorName})" : ""} — '
                    'reconnecting in another readout mode (e.g. High Full Well) '
                    're-captures automatically.',
                    style: dim?.copyWith(color: AraColors.accentInfo),
                  ),
                ),
              ],
            ),
          ),
        EditableTextRow(
          label: 'Sensor name',
          helpKey: 'img.electronics.sensor_name',
          currentValue: e.sensorName,
          getCanonical: () =>
              ref.read(cameraElectronicsProvider).value!.sensorName,
          parse: n.setSensorName,
          hint: 'e.g. IMX571 (auto-filled on camera connect)',
        ),
        EditableNumberRow(
          label: 'Read noise (e⁻ RMS)',
          helpKey: 'img.electronics.read_noise',
          currentValue: _fmt(e.readNoiseE),
          getCanonical: () =>
              _fmt(ref.read(cameraElectronicsProvider).value!.readNoiseE),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setReadNoiseE(v);
          },
        ),
        EditableNumberRow(
          label: 'Full-well capacity (e⁻)',
          helpKey: 'img.electronics.full_well',
          currentValue: _fmt(e.fullWellE),
          getCanonical: () =>
              _fmt(ref.read(cameraElectronicsProvider).value!.fullWellE),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setFullWellE(v);
          },
        ),
        EditableNumberRow(
          label: 'Conversion gain (e⁻/ADU)',
          helpKey: 'img.electronics.conversion_gain',
          currentValue: _fmt(e.electronsPerAdu),
          getCanonical: () =>
              _fmt(ref.read(cameraElectronicsProvider).value!.electronsPerAdu),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setElectronsPerAdu(v);
          },
        ),
        EditableNumberRow(
          label: 'Gain these values apply at',
          helpKey: 'img.electronics.gain_reference',
          currentValue: e.gain.toString(),
          getCanonical: () =>
              ref.read(cameraElectronicsProvider).value!.gain.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) n.setGain(v);
          },
        ),
        EditableNumberRow(
          label: 'Peak quantum efficiency (0–1)',
          helpKey: 'img.electronics.peak_qe',
          currentValue: _fmt(e.quantumEfficiencyPeak),
          getCanonical: () => _fmt(
            ref.read(cameraElectronicsProvider).value!.quantumEfficiencyPeak,
          ),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setQuantumEfficiencyPeak(v);
          },
        ),
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

  // Trim a trailing '.0' so a whole number shows as "50000", not "50000.0".
  static String _fmt(double v) =>
      v == v.roundToDouble() ? v.toInt().toString() : v.toString();
}
