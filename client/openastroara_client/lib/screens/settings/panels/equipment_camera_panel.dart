import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/equipment/alpaca_device_row.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 + §52 Camera panel. Alpaca device + auto-connect are editable;
/// sensor + cooling fields stay read-only because they're daemon-reported.
class EquipmentCameraPanel extends ConsumerWidget {
  const EquipmentCameraPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const AlpacaDeviceRow(
          deviceType: EquipmentDeviceType.camera,
          deviceTypeLabel: 'camera',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.camera),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.camera, v),
          hint: '§52.1 connection lifecycle',
        ),
        const SettingsSectionHeader('Sensor'),
        const SettingsRow(label: 'Pixel size (μm)', value: '—'),
        const SettingsRow(label: 'Sensor type', value: '— (Mono / OSC)'),
        const SettingsRow(label: 'Bit depth', value: '16'),
        const SettingsRow(label: 'Bayer pattern', value: 'N/A'),
        const SettingsSectionHeader('Cooling'),
        const SettingsRow(label: 'Has cooler', value: 'Auto-detect'),
        const SettingsRow(label: 'Default target temp (°C)', value: '−10'),
        const SettingsRow(label: 'Ramp rate (°C/min)', value: '1.0'),
        const SettingsRow(label: 'Warm-up at session end', value: 'Off'),
      ],
    );
  }
}
