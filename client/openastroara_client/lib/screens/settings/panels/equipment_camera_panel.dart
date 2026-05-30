import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/equipment/alpaca_selection_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/alpaca_chooser_dialog.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 + §52 Camera panel. Phase 12h.5a wires the Alpaca chooser via
/// `showAlpacaChooserDialog` — the daemon-side discovery endpoint is real
/// (Phase 6); selection lives in `alpacaSelectionProvider`. Sensor +
/// cooling fields stay read-only because they're daemon-reported.
class EquipmentCameraPanel extends ConsumerWidget {
  const EquipmentCameraPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final selected = ref
        .watch(alpacaSelectionProvider)[EquipmentDeviceType.camera];

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        _AlpacaDeviceRow(
          deviceTypeLabel: 'camera',
          selectedName: selected?.name,
          onChoose: () => showAlpacaChooserDialog(
            context,
            EquipmentDeviceType.camera,
            deviceTypeLabel: 'camera',
          ),
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

/// Connection-row variant of `SettingsRow` with a "Choose…" trailing button
/// that opens the §52.2 Alpaca chooser dialog. Lives next to the camera
/// panel for now; 12h.5b lifts it into a shared widget once a second panel
/// (mount, focuser, ...) needs the same row.
class _AlpacaDeviceRow extends StatelessWidget {
  final String deviceTypeLabel;
  final String? selectedName;
  final VoidCallback onChoose;

  const _AlpacaDeviceRow({
    required this.deviceTypeLabel,
    required this.selectedName,
    required this.onChoose,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Text('Alpaca device',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ),
        Expanded(
          child: Text(
            selectedName ?? 'Not selected',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: selectedName == null
                      ? AraColors.textDisabled
                      : null,
                ),
          ),
        ),
        TextButton.icon(
          onPressed: onChoose,
          icon: const Icon(Icons.search, size: 16),
          label: const Text('Choose…'),
        ),
      ]),
    );
  }
}
