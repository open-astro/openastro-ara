import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/mount_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/mount_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.5 Mount panel. Shows the connected mount's live RA/Dec + tracking/park
/// state with tracking, park/unpark, and abort-slew controls (each gated on the
/// device's capabilities) via the shared connection card.
class EquipmentMountPanel extends ConsumerWidget {
  const EquipmentMountPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(mountProvider);
    final notifier = ref.read(mountProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<MountStatus>(
          status: status,
          deviceType: EquipmentDeviceType.mount,
          deviceTypeLabel: 'mount',
          emptyLabel: 'No mount connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _MountBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.mount),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.mount, v),
          hint: '§52.1 connection lifecycle',
        ),
      ],
    );
  }
}

/// The connected mount's live body: RA/Dec + tracking/park/home state, with
/// tracking, park/unpark, and abort-slew controls gated on capabilities.
class _MountBody extends ConsumerWidget {
  final MountStatus status;
  const _MountBody({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Mount read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Right ascension', formatRaHours(s.rightAscensionHours)),
        _row('Declination', formatDecDegrees(s.declinationDegrees)),
        _row('Parked', s.parked ? 'Yes' : 'No'),
        _row('At home', s.atHome ? 'Yes' : 'No'),
        if (s.isBusy)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Text(s.runtimeState == 'unparking' ? 'Unparking…' : 'Slewing…',
                style: const TextStyle(color: AraColors.accentBusy)),
          ),
        if (caps?.canSetTracking ?? false)
          Row(children: [
            const Expanded(child: Text('Tracking')),
            Switch(
              key: const Key('mount_tracking_switch'),
              value: s.tracking,
              onChanged: s.parked
                  ? null
                  : (v) => _run(context, ref, 'set tracking',
                      () => ref.read(mountProvider.notifier).setTracking(v)),
            ),
          ]),
        const SizedBox(height: 8),
        Wrap(spacing: 8, runSpacing: 8, children: [
          if ((caps?.canPark ?? false) && !s.parked)
            OutlinedButton(
              onPressed: s.isBusy
                  ? null
                  : () => _run(context, ref, 'park',
                      () => ref.read(mountProvider.notifier).park()),
              child: const Text('Park'),
            ),
          if ((caps?.canUnpark ?? false) && s.parked)
            OutlinedButton(
              onPressed: s.isBusy
                  ? null
                  : () => _run(context, ref, 'unpark',
                      () => ref.read(mountProvider.notifier).unpark()),
              child: const Text('Unpark'),
            ),
          OutlinedButton(
            onPressed: () => _run(context, ref, 'stop',
                () => ref.read(mountProvider.notifier).abortSlew()),
            child: const Text('Stop'),
          ),
        ]),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [Expanded(child: Text(label)), Text(value)]),
      );

  Future<void> _run(BuildContext context, WidgetRef ref, String verb,
      Future<bool> Function() action) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed = await action();
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another action is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't $verb: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}
