import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/phd2_settings_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §63 PHD2 / Guider panel — editable in 12h.4. PHD2-internal settings
/// (host/port/profile + dithering + per-session calibration) live in
/// `phd2SettingsProvider`. The §35 meridian-flip re-cal-guider toggle is
/// surfaced read-only here as a reference — edit it in Safety → Policies.
class EquipmentGuiderPanel extends ConsumerWidget {
  const EquipmentGuiderPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final connN = ref.read(equipmentConnectionProvider.notifier);
    final phd2 = ref.watch(phd2SettingsProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('PHD2 connection'),
        EditableTextRow(
          label: 'Host',
          currentValue: phd2.host,
          getCanonical: () => ref.read(phd2SettingsProvider).host,
          parse: phd2N.setHost,
        ),
        EditableNumberRow(
          label: 'Port',
          currentValue: phd2.port.toString(),
          getCanonical: () => ref.read(phd2SettingsProvider).port.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setPort(v);
          },
        ),
        EditableTextRow(
          label: 'Profile',
          currentValue: phd2.phd2Profile,
          getCanonical: () => ref.read(phd2SettingsProvider).phd2Profile,
          parse: phd2N.setPhd2Profile,
          hint: 'PHD2 equipment profile, not OpenAstroAra profile',
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.guider),
          onChanged: (v) => connN.setAutoConnect(EquipmentDeviceType.guider, v),
          hint: 'Off by default — guider connect starts PHD2 client',
        ),
        const SettingsSectionHeader('Dithering'),
        SettingsSwitchRow(
          label: 'Enable dithering',
          value: phd2.ditherEnabled,
          onChanged: phd2N.setDitherEnabled,
        ),
        EditableNumberRow(
          label: 'Dither every N frames',
          currentValue: phd2.ditherEveryNFrames.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).ditherEveryNFrames.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setDitherEveryNFrames(v);
          },
        ),
        EditableNumberRow(
          label: 'Dither pixels',
          helpKey: 'eq.guider.dither_pixels',
          currentValue: phd2.ditherPixels.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).ditherPixels.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setDitherPixels(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle threshold (pixels)',
          helpKey: 'eq.guider.settle_pixels',
          currentValue: phd2.settlePixels.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settlePixels.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) phd2N.setSettlePixels(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle time (s)',
          currentValue: phd2.settleTimeSec.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settleTimeSec.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setSettleTimeSec(v);
          },
        ),
        EditableNumberRow(
          label: 'Settle timeout (s)',
          helpKey: 'eq.guider.settle_timeout_sec',
          currentValue: phd2.settleTimeoutSec.toString(),
          getCanonical: () =>
              ref.read(phd2SettingsProvider).settleTimeoutSec.toString(),
          parse: (s) {
            final v = int.tryParse(s);
            if (v != null) phd2N.setSettleTimeoutSec(v);
          },
        ),
        const SettingsSectionHeader('Calibration'),
        SettingsSwitchRow(
          label: 'Force calibration each session',
          helpKey: 'eq.guider.force_calibration_each_session',
          value: phd2.forceCalibrationEachSession,
          onChanged: phd2N.setForceCalibrationEachSession,
        ),
        const SettingsRow(
          label: 'Re-calibrate on meridian flip',
          value: 'Edit in Settings → Safety → Policies',
          hint: '§35 meridian-flip behaviour',
        ),
        const SizedBox(height: 24),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text(
                    'PHD2 settings saved (in memory). Daemon round-trip lands in 12h.2b.',
                  ),
                ),
              );
            },
            icon: const Icon(Icons.save, size: 16),
            label: const Text('Save'),
          ),
        ]),
      ],
    );
  }
}
