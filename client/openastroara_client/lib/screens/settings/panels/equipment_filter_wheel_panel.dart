import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Filter Wheel panel. Auto-connect editable in 12h.2; filter slot
/// labels become editable in 12h.2c when the per-slot label state lands.
class EquipmentFilterWheelPanel extends ConsumerWidget {
  const EquipmentFilterWheelPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const SettingsRow(label: 'Alpaca device', value: 'Not selected'),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          value: connection.autoConnect(EquipmentDeviceType.filterWheel),
          onChanged: (v) =>
              n.setAutoConnect(EquipmentDeviceType.filterWheel, v),
        ),
        const SettingsSectionHeader('Filters'),
        const SettingsRow(label: 'Slot 1', value: 'L'),
        const SettingsRow(label: 'Slot 2', value: 'R'),
        const SettingsRow(label: 'Slot 3', value: 'G'),
        const SettingsRow(label: 'Slot 4', value: 'B'),
        const SettingsRow(label: 'Slot 5', value: 'Hα'),
        const SettingsRow(label: 'Slot 6', value: 'OIII'),
        const SettingsRow(label: 'Slot 7', value: 'SII'),
        const SettingsRow(label: 'Slot 8', value: '—'),
        const SettingsSectionHeader('Focus offsets'),
        const SettingsRow(
          label: 'Per-filter offsets',
          value: 'Not measured — run §37.4.2 measurement wizard',
        ),
      ],
    );
  }
}
