import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/dome_status.dart';
import '../../../models/equipment_device_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/dome_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Dome panel. Shows the connected dome's live shutter / azimuth / park
/// state with shutter, slew, and park controls (each gated on the device's
/// capabilities) via the shared connection card.
class EquipmentDomePanel extends ConsumerWidget {
  const EquipmentDomePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(domeProvider);
    final notifier = ref.read(domeProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<DomeStatus>(
          status: status,
          deviceType: EquipmentDeviceType.dome,
          deviceTypeLabel: 'dome',
          emptyLabel: 'No dome connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _DomeBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.dome),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.dome, v),
          hint: 'Off by default — dome connect actuates shutter',
        ),
        const SettingsSectionHeader('Slaving'),
        const SettingsRow(
          label: 'Slave to mount',
          value: 'On (if connected)',
          hint: '§35 — dome follows the mount azimuth',
        ),
        const SettingsRow(label: 'Close shutter on park', value: 'On'),
        const SettingsRow(label: 'Close shutter on unsafe', value: 'On'),
      ],
    );
  }
}

/// The connected dome's live body: shutter/azimuth/home/park state + shutter,
/// slew, and park controls gated on capabilities.
class _DomeBody extends ConsumerStatefulWidget {
  final DomeStatus status;
  const _DomeBody({required this.status});

  @override
  ConsumerState<_DomeBody> createState() => _DomeBodyState();
}

class _DomeBodyState extends ConsumerState<_DomeBody> {
  late final TextEditingController _az = TextEditingController(
      text: widget.status.azimuthDeg?.toStringAsFixed(0) ?? '');

  @override
  void dispose() {
    _az.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Dome read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Shutter', s.shutterOpen ? 'Open' : 'Closed'),
        _row('Azimuth', s.azimuthDeg == null ? '—' : '${s.azimuthDeg!.toStringAsFixed(0)}°'),
        _row('At home', s.atHome ? 'Yes' : 'No'),
        _row('Parked', s.parked ? 'Yes' : 'No'),
        if (s.isBusy)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Text(s.runtimeState == 'slewing' ? 'Slewing…' : 'Shutter moving…',
                style: const TextStyle(color: AraColors.accentBusy)),
          ),
        const SizedBox(height: 8),
        if (caps?.canSetShutter ?? false)
          Row(children: [
            OutlinedButton(
              onPressed: s.isBusy ? null : () => _run('open shutter', ref.read(domeProvider.notifier).openShutter),
              child: const Text('Open shutter'),
            ),
            const SizedBox(width: 8),
            OutlinedButton(
              onPressed: s.isBusy ? null : () => _run('close shutter', ref.read(domeProvider.notifier).closeShutter),
              child: const Text('Close shutter'),
            ),
          ]),
        Padding(
          padding: const EdgeInsets.only(top: 4),
          child: Wrap(spacing: 8, runSpacing: 4, children: [
            if (caps?.canPark ?? false)
              OutlinedButton(
                onPressed: s.isBusy ? null : () => _run('park', ref.read(domeProvider.notifier).park),
                child: const Text('Park'),
              ),
            if (caps?.canSetPark ?? false)
              OutlinedButton(
                onPressed: s.isBusy ? null : () => _run('set park position', ref.read(domeProvider.notifier).setPark),
                child: const Text('Set park here'),
              ),
            if (caps?.canFindHome ?? false)
              OutlinedButton(
                onPressed: s.isBusy ? null : () => _run('find home', ref.read(domeProvider.notifier).findHome),
                child: const Text('Find home'),
              ),
            // Deliberately NOT disabled while busy — stopping a moving dome is
            // exactly when you need it (§57 panic-stop shape).
            OutlinedButton(
              onPressed: () => _run('stop', ref.read(domeProvider.notifier).abortSlew),
              child: const Text('Stop'),
            ),
          ]),
        ),
        if (caps?.canSetAzimuth ?? false)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(children: [
              SizedBox(
                width: 130,
                child: TextField(
                  controller: _az,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  inputFormatters: [
                    FilteringTextInputFormatter.allow(RegExp(r'^[0-9]*\.?[0-9]*$')),
                  ],
                  decoration: const InputDecoration(
                      isDense: true,
                      labelText: 'Azimuth (°)',
                      helperText: '0 to <360'),
                ),
              ),
              const SizedBox(width: 12),
              FilledButton(
                onPressed: s.isBusy ? null : _slew,
                child: const Text('Slew'),
              ),
              if (caps?.canSyncAzimuth ?? false) ...[
                const SizedBox(width: 8),
                OutlinedButton(
                  onPressed: s.isBusy ? null : _sync,
                  child: const Text('Sync'),
                ),
              ],
            ]),
          ),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [Expanded(child: Text(label)), Text(value)]),
      );

  Future<void> _slew() async {
    final messenger = ScaffoldMessenger.of(context);
    // ASCOM azimuth is [0, 360) — 360° ≡ 0° (North), so 360 is rejected (matches
    // the daemon's IsAzimuthOutOfRange).
    final v = double.tryParse(_az.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter an azimuth from 0 up to (not including) 360.')));
      return;
    }
    await _run('slew', () => ref.read(domeProvider.notifier).slew(v));
  }

  Future<void> _sync() async {
    final messenger = ScaffoldMessenger.of(context);
    // Same [0, 360) rule as _slew — sync re-labels the current position, no motion.
    final v = double.tryParse(_az.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter an azimuth from 0 up to (not including) 360.')));
      return;
    }
    await _run('sync', () => ref.read(domeProvider.notifier).syncToAzimuth(v));
  }

  Future<void> _run(String verb, Future<bool> Function() action) async {
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
