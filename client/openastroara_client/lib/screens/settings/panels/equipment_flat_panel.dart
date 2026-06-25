import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/flat_panel_status.dart';
import '../../../state/equipment/flat_panel_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Flat Panel (CoverCalibrator under Alpaca). Connect/disconnect/reconnect
/// run through the shared [EquipmentConnectionCard] on the live [flatPanelProvider]
/// — the same provider the top-bar FLAT chip watches, so connecting here turns the
/// chip green. The cover/light readout is the connected body; the §29.3 capture
/// rows below stay as profile-policy references.
class EquipmentFlatPanel extends ConsumerWidget {
  const EquipmentFlatPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(flatPanelProvider);
    final notifier = ref.read(flatPanelProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FlatPanelStatus>(
          status: status,
          deviceType: EquipmentDeviceType.flatPanel,
          deviceTypeLabel: 'flat panel',
          emptyLabel: 'No flat panel connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FlatBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.flatPanel),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.flatPanel, v),
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

/// The connected flat panel's live body: cover position + calibrator light state.
/// While connecting it's the daemon default (transient → "Reading…"); `error` is a
/// failed read, not a flag.
class _FlatBody extends StatelessWidget {
  final FlatPanelStatus status;
  const _FlatBody({required this.status});

  @override
  Widget build(BuildContext context) {
    return switch (status.connectionState) {
      EquipmentConnectionState.connected => _FlatReadout(status: status),
      EquipmentConnectionState.error => const Row(children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Flat panel read failed — check the device.')),
        ]),
      _ => const Text('Reading…'),
    };
  }
}

/// Cover state (open / closed / moving) + calibrator light (on at brightness, or off).
class _FlatReadout extends StatelessWidget {
  final FlatPanelStatus status;
  const _FlatReadout({required this.status});

  @override
  Widget build(BuildContext context) {
    final (coverIcon, coverText) = status.isMoving
        ? (Icons.sync, 'Cover moving…')
        : status.coverOpen
            ? (Icons.unfold_more, 'Cover open')
            : (Icons.unfold_less, 'Cover closed');
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(children: [
          Icon(coverIcon, size: 20, color: AraColors.textSecondary),
          const SizedBox(width: 8),
          Text(coverText),
        ]),
        const SizedBox(height: 6),
        Row(children: [
          Icon(
            status.lightOn ? Icons.lightbulb : Icons.lightbulb_outline,
            size: 20,
            color: status.lightOn
                ? AraColors.accentConnected
                : AraColors.textSecondary,
          ),
          const SizedBox(width: 8),
          Text(status.lightOn
              ? 'Light on · brightness ${status.brightness}'
              : 'Light off'),
        ]),
      ],
    );
  }
}
