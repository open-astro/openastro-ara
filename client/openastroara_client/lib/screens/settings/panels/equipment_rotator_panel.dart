import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/rotator_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/rotator_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Rotator panel. Shows the connected rotator's live sky/mechanical angle
/// with move, reverse, and sky-angle sync controls, via the shared connection
/// card. The §38 plate-solve framing hook drives sync programmatically.
class EquipmentRotatorPanel extends ConsumerWidget {
  const EquipmentRotatorPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(rotatorProvider);
    final notifier = ref.read(rotatorProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<RotatorStatus>(
          status: status,
          deviceType: EquipmentDeviceType.rotator,
          deviceTypeLabel: 'rotator',
          emptyLabel: 'No rotator connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _RotatorBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.rotator),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.rotator, v),
        ),
        const SettingsSectionHeader('Framing'),
        const SettingsRow(
          label: 'Plate-solve to set angle',
          value: 'On',
          hint: '§38 Framing + Set-as-Target drives Sync',
        ),
      ],
    );
  }
}

/// The connected rotator's live body: sky + mechanical angle, a reverse toggle,
/// and move / sync controls.
class _RotatorBody extends ConsumerStatefulWidget {
  final RotatorStatus status;
  const _RotatorBody({required this.status});

  @override
  ConsumerState<_RotatorBody> createState() => _RotatorBodyState();
}

class _RotatorBodyState extends ConsumerState<_RotatorBody> {
  // Seeded once from the current sky angle; the user's target, not a live value.
  late final TextEditingController _target = TextEditingController(
      text: widget.status.skyAngleDeg?.toStringAsFixed(1) ?? '');
  bool _useSkyAngle = true;

  @override
  void dispose() {
    _target.dispose();
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
        Expanded(child: Text('Rotator read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Sky angle', _fmt(s.skyAngleDeg)),
        _row('Mechanical angle', _fmt(s.mechanicalAngleDeg)),
        if (s.isMoving)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Text('Moving…', style: TextStyle(color: AraColors.accentBusy)),
          ),
        if (caps?.canReverse ?? false)
          Row(children: [
            const Expanded(child: Text('Reverse direction')),
            Switch(
              value: s.reverse,
              onChanged: s.isMoving ? null : (v) => _setReverse(v),
            ),
          ]),
        const SizedBox(height: 8),
        Row(children: [
          SizedBox(
            width: 130,
            child: TextField(
              controller: _target,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              inputFormatters: [
                FilteringTextInputFormatter.allow(RegExp(r'^[0-9]*\.?[0-9]*$')),
              ],
              decoration: const InputDecoration(
                isDense: true,
                labelText: 'Angle (°)',
                helperText: '0–360',
              ),
            ),
          ),
          const SizedBox(width: 12),
          FilledButton(
            onPressed: s.isMoving ? null : _move,
            child: const Text('Move'),
          ),
          const SizedBox(width: 8),
          OutlinedButton(
            onPressed: s.isMoving ? null : _sync,
            child: const Text('Sync'),
          ),
        ]),
        Row(children: [
          Checkbox(
            value: _useSkyAngle,
            onChanged: (v) => setState(() => _useSkyAngle = v ?? true),
          ),
          const Text('Move to sky angle (off = mechanical)'),
        ]),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [Expanded(child: Text(label)), Text(value)]),
      );

  static String _fmt(double? deg) => deg == null ? '—' : '${deg.toStringAsFixed(1)}°';

  // Parse + range-check [0, 360); returns null (with a SnackBar) on bad input.
  double? _parseAngle(ScaffoldMessengerState messenger) {
    final v = double.tryParse(_target.text.trim());
    if (v == null || v < 0 || v >= 360) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter an angle in 0–360.')));
      return null;
    }
    return v;
  }

  Future<void> _move() async {
    final messenger = ScaffoldMessenger.of(context);
    final angle = _parseAngle(messenger);
    if (angle == null) return;
    await _run(messenger, "move",
        () => ref.read(rotatorProvider.notifier).move(angle, useSkyAngle: _useSkyAngle));
  }

  Future<void> _sync() async {
    final messenger = ScaffoldMessenger.of(context);
    final angle = _parseAngle(messenger);
    if (angle == null) return;
    await _run(messenger, "sync",
        () => ref.read(rotatorProvider.notifier).sync(angle));
  }

  Future<void> _setReverse(bool reverse) async {
    final messenger = ScaffoldMessenger.of(context);
    await _run(messenger, "set reverse",
        () => ref.read(rotatorProvider.notifier).setReverse(reverse));
  }

  Future<void> _run(ScaffoldMessengerState messenger, String verb,
      Future<bool> Function() action) async {
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
