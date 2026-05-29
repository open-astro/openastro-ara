import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.5 Guider (PHD2) panel. Auto-connect editable in 12h.2; dithering +
/// calibration fields remain stubs until the dedicated `eq.phd2` panel
/// (12h.2c).
class EquipmentGuiderPanel extends ConsumerWidget {
  const EquipmentGuiderPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('PHD2 connection'),
        const SettingsRow(label: 'Host', value: 'localhost'),
        const SettingsRow(label: 'Port', value: '4400'),
        const SettingsRow(
          label: 'Profile',
          value: 'Default',
          hint: 'PHD2 equipment profile, not OpenAstroAra profile',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.guider),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.guider, v),
          hint: 'Off by default — guider connect starts PHD2 client',
        ),
        const SettingsSectionHeader('Dithering'),
        const SettingsRow(label: 'Enable dithering', value: 'On'),
        const SettingsRow(label: 'Dither every N frames', value: '1'),
        const SettingsRow(label: 'Dither pixels', value: '5'),
        const SettingsRow(label: 'Settle pixels', value: '1.5'),
        const SettingsRow(label: 'Settle time (s)', value: '10'),
        const SettingsRow(label: 'Settle timeout (s)', value: '60'),
        const SettingsSectionHeader('Calibration'),
        const SettingsRow(
            label: 'Force calibration each session', value: 'Off'),
        const SettingsRow(
          label: 'Re-calibrate on meridian flip',
          value: 'On',
          hint: '§38.7',
        ),
      ],
    );
  }
}
