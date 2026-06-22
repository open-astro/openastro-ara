import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart'; // AsyncValue/AsyncData/AsyncError

import '../../models/discovered_device.dart';
import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart';
import '../../state/settings/equipment_connection_state.dart';
import '../../theme/ara_colors.dart';
import 'alpaca_chooser_dialog.dart';
import 'equipment_state_chip.dart';

/// Shared connection card for the single-instance equipment panels (everything on
/// the generic [EquipmentDeviceNotifier] core — i.e. all but the multi-instance
/// Switch). Renders the four async shapes of a device's live status and owns the
/// connect (via the §52.2 chooser) / disconnect controls + their error SnackBars;
/// each panel supplies only the device-specific live body via [connectedBody].
class EquipmentConnectionCard<T extends EquipmentDeviceStatus>
    extends StatelessWidget {
  final AsyncValue<T?> status;
  final EquipmentDeviceType deviceType;
  final String deviceTypeLabel;

  /// Shown (with the disconnected chip) when no device of this type is connected.
  final String emptyLabel;

  /// Connect/disconnect actions — return whether the call was PERFORMED (`false`
  /// when dropped by the notifier's re-entrancy guard). The card surfaces a
  /// SnackBar on a dropped or failed action.
  final Future<bool> Function(DiscoveredDevice device) onConnect;
  final Future<bool> Function() onDisconnect;
  final VoidCallback onRetry;

  /// The device-specific live content shown once connected (e.g. a Safe/Unsafe
  /// indicator, a weather sensor grid). The header (name + state chip + disconnect)
  /// is rendered by the card.
  final Widget Function(BuildContext context, T status) connectedBody;

  const EquipmentConnectionCard({
    super.key,
    required this.status,
    required this.deviceType,
    required this.deviceTypeLabel,
    required this.emptyLabel,
    required this.onConnect,
    required this.onDisconnect,
    required this.onRetry,
    required this.connectedBody,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      color: AraColors.bgPanel,
      margin: const EdgeInsets.only(top: 4, bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
        child: switch (status) {
          AsyncData(:final value) => value == null
              ? _disconnected(context)
              : _connected(context, value),
          AsyncError(:final error) => _MessageRow(
              icon: Icons.error_outline,
              color: AraColors.accentError,
              text: "Couldn't read the $deviceTypeLabel: "
                  '${describeEquipmentError(error)}',
              onRetry: onRetry,
            ),
          _ => const Padding(
              padding: EdgeInsets.all(12),
              child: Center(child: CircularProgressIndicator()),
            ),
        },
      ),
    );
  }

  Widget _disconnected(BuildContext context) {
    return Row(
      children: [
        const EquipmentStateChip(state: EquipmentConnectionState.disconnected),
        const SizedBox(width: 12),
        Expanded(child: Text(emptyLabel)),
        TextButton.icon(
          onPressed: () => _connect(context),
          icon: const Icon(Icons.search, size: 16),
          label: const Text('Connect…'),
        ),
      ],
    );
  }

  Widget _connected(BuildContext context, T value) {
    final title = _titleFor(value);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(title, style: Theme.of(context).textTheme.titleMedium),
            ),
            EquipmentStateChip(state: value.connectionState),
            IconButton(
              // While connecting, the same action aborts the in-progress connect.
              tooltip: value.isConnecting ? 'Cancel connecting' : 'Disconnect',
              icon: Icon(value.isConnecting ? Icons.close : Icons.link_off,
                  size: 18),
              onPressed: () => _disconnect(context),
            ),
          ],
        ),
        const Divider(height: 20, color: AraColors.border),
        connectedBody(context, value),
      ],
    );
  }

  // The device's own name, or the capitalized device-type label as a fallback.
  String _titleFor(T value) {
    if (value.name.isNotEmpty) return value.name;
    return deviceTypeLabel[0].toUpperCase() + deviceTypeLabel.substring(1);
  }

  Future<void> _connect(BuildContext context) {
    final messenger = ScaffoldMessenger.of(context);
    return showAlpacaChooserDialog(
      context,
      deviceType,
      deviceTypeLabel: deviceTypeLabel,
      onPick: (device) async {
        try {
          final performed = await onConnect(device);
          if (!performed) {
            messenger.showSnackBar(const SnackBar(
              content: Text('Another connect/disconnect is still in progress.'),
            ));
          }
        } catch (e) {
          messenger.showSnackBar(SnackBar(
            content:
                Text("Couldn't connect ${device.name}: ${describeEquipmentError(e)}"),
            backgroundColor: AraColors.accentError,
          ));
        }
      },
    );
  }

  Future<void> _disconnect(BuildContext context) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed = await onDisconnect();
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another connect/disconnect is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't disconnect: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}

class _MessageRow extends StatelessWidget {
  final IconData icon;
  final Color color;
  final String text;
  final VoidCallback onRetry;
  const _MessageRow({
    required this.icon,
    required this.color,
    required this.text,
    required this.onRetry,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, color: color, size: 20),
        const SizedBox(width: 8),
        Expanded(child: Text(text)),
        TextButton(onPressed: onRetry, child: const Text('Retry')),
      ],
    );
  }
}
