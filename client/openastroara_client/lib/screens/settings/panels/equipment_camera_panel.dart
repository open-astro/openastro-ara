import 'package:flutter/material.dart';

import '../../../widgets/settings/settings_row.dart';

/// §37.4 + §52 Camera panel. Phase 12h.2 ships read-only stubs reflecting
/// the camera-equipment fields from `ProfileDraft.camera`. Phase 12h.2b
/// wires editable forms + persistence via `/api/v1/profile/camera`.
class EquipmentCameraPanel extends StatelessWidget {
  const EquipmentCameraPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(24),
      children: const [
        SettingsSectionHeader('Connection'),
        SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsRow(
          label: 'Auto-connect on boot',
          value: 'On',
          hint: 'See §52.1 connection lifecycle',
        ),
        SettingsSectionHeader('Sensor'),
        SettingsRow(label: 'Pixel size (μm)', value: '—'),
        SettingsRow(label: 'Sensor type', value: '— (Mono / OSC)'),
        SettingsRow(label: 'Bit depth', value: '16'),
        SettingsRow(label: 'Bayer pattern', value: 'N/A'),
        SettingsSectionHeader('Cooling'),
        SettingsRow(label: 'Has cooler', value: 'Auto-detect'),
        SettingsRow(label: 'Default target temp (°C)', value: '−10'),
        SettingsRow(label: 'Ramp rate (°C/min)', value: '1.0'),
        SettingsRow(label: 'Warm-up at session end', value: 'Off'),
      ],
    );
  }
}
