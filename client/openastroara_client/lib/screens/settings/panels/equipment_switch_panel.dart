import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/switch_device.dart';
import '../../../services/equipment_device_api.dart' show isNotFoundEquipmentError;
import '../../../state/equipment/switch_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/alpaca_chooser_dialog.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §6 Switch panel. Unlike the single-instance device panels, several ASCOM
/// Switch devices can be connected at once (each addressed by its Alpaca device
/// number), so this lists every connected switch with its ports + an "Add
/// switch" action, instead of the one-device `AlpacaDeviceRow`.
class EquipmentSwitchPanel extends ConsumerStatefulWidget {
  const EquipmentSwitchPanel({super.key});

  @override
  ConsumerState<EquipmentSwitchPanel> createState() => _EquipmentSwitchPanelState();
}

class _EquipmentSwitchPanelState extends ConsumerState<EquipmentSwitchPanel> {
  // The daemon's connect is 202-accepted + background, so a freshly-added switch
  // first reads back as `connecting`. The provider is pull-on-demand (no periodic
  // refresh), so without this the card would stay stuck on "Connecting" until the
  // user re-opened the panel. While ANY switch is mid-connect, re-read so the card
  // settles to its real state; the tick is a no-op (no network) when nothing is
  // connecting, and stops when the panel is disposed.
  Timer? _settlePoll;

  @override
  void initState() {
    super.initState();
    _settlePoll = Timer.periodic(const Duration(milliseconds: 1500), (_) {
      final list = ref.read(switchListProvider).value;
      final anyConnecting = list != null &&
          list.any((d) => d.connectionState == SwitchConnectionState.connecting);
      if (anyConnecting) {
        ref.read(switchListProvider.notifier).refresh();
      }
    });
  }

  @override
  void dispose() {
    _settlePoll?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final switches = ref.watch(switchListProvider);
    final connection = ref.watch(equipmentConnectionProvider);
    final connNotifier = ref.read(equipmentConnectionProvider.notifier);
    // Only offer Reconnect while no switch is currently connected/connecting —
    // reconnectAll re-dispatches every remembered switch, and re-connecting one
    // that's live can tear it down if the daemon's remembered UniqueId differs
    // from the live connection (e.g. its Alpaca host IP changed). An empty list
    // (every() is true) still offers it — that's the post-power-cycle case.
    final canReconnect = switches.maybeWhen(
      data: (list) => list.every((d) =>
          d.connectionState != SwitchConnectionState.connected &&
          d.connectionState != SwitchConnectionState.connecting),
      orElse: () => false,
    );
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        Row(
          children: [
            const Expanded(child: SettingsSectionHeader('Connected switches')),
            // Reconnect every switch the daemon remembers (e.g. after a gear
            // power-cycle) without re-discovering each one.
            if (canReconnect)
              TextButton.icon(
                onPressed: () => _reconnectAll(context),
                icon: const Icon(Icons.refresh, size: 18),
                label: const Text('Reconnect'),
              ),
            TextButton.icon(
              onPressed: () => _addSwitch(context),
              icon: const Icon(Icons.add, size: 18),
              label: const Text('Add switch'),
            ),
          ],
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.switchDevice),
          onChanged: (v) =>
              connNotifier.setAutoConnect(EquipmentDeviceType.switchDevice, v),
        ),
        const SizedBox(height: 4),
        ...switch (switches) {
          AsyncData(:final value) => value.isEmpty
              ? const [_EmptyState()]
              : [for (final d in value) _SwitchCard(device: d)],
          AsyncError(:final error) => [
              _MessageRow(
                icon: Icons.error_outline,
                color: AraColors.accentError,
                text: "Couldn't read the switch list: ${_msg(error)}",
                onRetry: () => ref.read(switchListProvider.notifier).refresh(),
              ),
            ],
          _ => const [
              Padding(
                padding: EdgeInsets.all(32),
                child: Center(child: CircularProgressIndicator()),
              ),
            ],
        },
      ],
    );
  }

  Future<void> _reconnectAll(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed = await ref.read(switchListProvider.notifier).reconnectAll();
      if (!performed) {
        // Re-entrancy guard fired (a port write/connect is still in flight) —
        // tell the user instead of swallowing the tap.
        messenger.showSnackBar(const SnackBar(
          content: Text('Another connect/disconnect is still in progress.'),
        ));
      }
    } catch (e) {
      final text = isNotFoundEquipmentError(e)
          ? 'No previous switches to reconnect — use “Add switch” first.'
          : "Couldn't reconnect switches: ${_msg(e)}";
      messenger.showSnackBar(SnackBar(
        content: Text(text),
        backgroundColor: AraColors.accentError,
      ));
    }
  }

  Future<void> _addSwitch(BuildContext context) {
    final messenger = ScaffoldMessenger.of(context);
    return showAlpacaChooserDialog(
      context,
      EquipmentDeviceType.switchDevice,
      deviceTypeLabel: 'switch',
      // Multi-instance: connect the pick (adding to the list) rather than the
      // default single-selection write. Fire-and-forget by design — the chooser
      // closes on pick; a concurrent second connect (re-opening the chooser before
      // this one finishes) is dropped by the notifier's _acting re-entrancy guard,
      // and the server dedups a same-device reconnect anyway.
      onPick: (device) async {
        try {
          await ref.read(switchListProvider.notifier).connect(device);
        } catch (e) {
          messenger.showSnackBar(SnackBar(
            content: Text("Couldn't connect ${device.name}: ${_msg(e)}"),
            backgroundColor: AraColors.accentError,
          ));
        }
      },
    );
  }
}

/// One connected (or known) switch device: header (name + state + disconnect)
/// and its ports.
class _SwitchCard extends ConsumerWidget {
  final SwitchDevice device;
  const _SwitchCard({required this.device});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final title = device.name.isEmpty ? 'Switch ${device.alpacaDeviceNumber}' : device.name;
    return Card(
      color: AraColors.bgPanel,
      margin: const EdgeInsets.only(top: 12),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text(title, style: Theme.of(context).textTheme.titleMedium),
                ),
                _StateChip(state: device.connectionState),
                IconButton(
                  tooltip: 'Disconnect',
                  icon: const Icon(Icons.link_off, size: 18),
                  onPressed: () => _disconnect(context, ref),
                ),
              ],
            ),
            Text(
              'device #${device.alpacaDeviceNumber}',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
            const Divider(height: 20, color: AraColors.border),
            if (device.isConnected && device.ports.isNotEmpty)
              for (final p in device.ports)
                _PortRow(deviceNumber: device.alpacaDeviceNumber, port: p)
            else
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 4),
                child: Text(
                  device.isConnected
                      ? 'No ports reported by this switch.'
                      : 'Ports appear once the switch is connected.',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Future<void> _disconnect(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(switchListProvider.notifier).disconnect(device.alpacaDeviceNumber);
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't disconnect: ${_msg(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}

/// A single port. Boolean writable → a toggle; value writable → a slider;
/// read-only → the value as text.
class _PortRow extends ConsumerStatefulWidget {
  final int deviceNumber;
  final SwitchPort port;
  const _PortRow({required this.deviceNumber, required this.port});

  @override
  ConsumerState<_PortRow> createState() => _PortRowState();
}

class _PortRowState extends ConsumerState<_PortRow> {
  // Local drag value for a value-slider port, so the thumb follows the finger
  // before the write commits + the list re-reads. Synced from the port on
  // external change (didUpdateWidget).
  double? _dragValue;

  @override
  void didUpdateWidget(_PortRow old) {
    super.didUpdateWidget(old);
    if (old.port.value != widget.port.value) _dragValue = null;
  }

  Future<void> _write(double value) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(switchListProvider.notifier).setValue(
            deviceNumber: widget.deviceNumber,
            portId: widget.port.id,
            value: value,
          );
    } catch (e) {
      // The write was rejected, so the server value is unchanged and
      // didUpdateWidget won't reset us — snap the slider back off the (rejected)
      // drag position to the last confirmed value.
      if (mounted) setState(() => _dragValue = null);
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't set ${widget.port.name}: ${_msg(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }

  @override
  Widget build(BuildContext context) {
    final port = widget.port;
    final label = port.name.isEmpty ? 'Port ${port.id}' : port.name;

    if (!port.canWrite) {
      return _PortLine(label: label, trailing: Text(_fmt(port.value)));
    }
    if (port.isBoolean) {
      return _PortLine(
        label: label,
        trailing: Switch(
          value: port.value >= 0.5,
          onChanged: (on) => _write(on ? port.max : port.min),
        ),
      );
    }
    // A Slider asserts min < max. A malformed device can report min == max (the
    // ASCOM spec forbids it for a non-boolean port, but don't trust that) — fall
    // back to a read-only value rather than crash.
    if (port.min >= port.max) {
      return _PortLine(label: label, trailing: Text(_fmt(port.value)));
    }
    final value = (_dragValue ?? port.value).clamp(port.min, port.max);
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(child: Text(label)),
              Text(_fmt(value),
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary)),
            ],
          ),
          Slider(
            min: port.min,
            max: port.max,
            value: value.toDouble(),
            onChanged: (v) => setState(() => _dragValue = v),
            onChangeEnd: (v) {
              _dragValue = v;
              _write(v);
            },
          ),
        ],
      ),
    );
  }
}

class _PortLine extends StatelessWidget {
  final String label;
  final Widget trailing;
  const _PortLine({required this.label, required this.trailing});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [Expanded(child: Text(label)), trailing],
      ),
    );
  }
}

class _StateChip extends StatelessWidget {
  final SwitchConnectionState state;
  const _StateChip({required this.state});

  @override
  Widget build(BuildContext context) {
    final (color, text) = switch (state) {
      SwitchConnectionState.connected => (AraColors.accentConnected, 'Connected'),
      SwitchConnectionState.connecting => (AraColors.accentBusy, 'Connecting'),
      SwitchConnectionState.error => (AraColors.accentError, 'Error'),
      SwitchConnectionState.disconnected => (AraColors.textSecondary, 'Disconnected'),
      SwitchConnectionState.unknown => (AraColors.textSecondary, 'Unknown'),
    };
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Text(text, style: TextStyle(color: color, fontSize: 12)),
    );
  }
}

class _EmptyState extends StatelessWidget {
  const _EmptyState();
  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 28),
      child: Center(
        child: Column(
          children: [
            const Icon(Icons.power_outlined, size: 40, color: AraColors.textDisabled),
            const SizedBox(height: 8),
            Text('No switches connected',
                style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 4),
            Text('Use “Add switch” to discover and connect a power/relay box.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary)),
          ],
        ),
      ),
    );
  }
}

class _MessageRow extends StatelessWidget {
  final IconData icon;
  final Color color;
  final String text;
  final VoidCallback onRetry;
  const _MessageRow({required this.icon, required this.color, required this.text, required this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 16),
      child: Row(
        children: [
          Icon(icon, color: color, size: 20),
          const SizedBox(width: 8),
          Expanded(child: Text(text)),
          TextButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}

String _fmt(double v) => v == v.roundToDouble() ? v.toInt().toString() : v.toStringAsFixed(2);

String _msg(Object? e) => e == null ? 'unknown error' : e.toString().replaceFirst('Exception: ', '');
