import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/equipment/alpaca_device_row.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Flat Panel (CoverCalibrator under Alpaca). Auto-connect editable;
/// other fields read-only stubs.
class EquipmentFlatPanel extends ConsumerWidget {
  const EquipmentFlatPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        const AlpacaDeviceRow(
          deviceType: EquipmentDeviceType.flatPanel,
          deviceTypeLabel: 'flat panel',
          hint: '§10.6 — Alpaca exposes flat panels as CoverCalibrator',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.flatPanel),
          onChanged: (v) =>
              n.setAutoConnect(EquipmentDeviceType.flatPanel, v),
        ),
        const SettingsSectionHeader('Flat capture'),
        const SettingsRow(label: 'Open cover on connect', value: 'On'),
        const SettingsRow(label: 'Close cover on park', value: 'On'),
        const SettingsRow(label: 'Calibrator min brightness', value: '0'),
        const SettingsRow(label: 'Calibrator max brightness', value: '255'),
        const SettingsRow(
          label: 'Auto-brightness target ADU',
          value: '30000',
          hint: '§29.3.2 auto-exposure flats',
        ),
      ],
    );
  }
}
