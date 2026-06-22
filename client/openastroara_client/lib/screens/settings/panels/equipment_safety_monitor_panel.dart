import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/safety_monitor_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/safety_monitor_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/alpaca_chooser_dialog.dart';
import '../../../widgets/equipment/equipment_state_chip.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Safety Monitor panel. Shows the device's live `is_safe` reading +
/// connection state with connect/disconnect; the §35 safety-policy rows below
/// (what to do when unsafe) stay as references to the profile policy.
class EquipmentSafetyMonitorPanel extends ConsumerWidget {
  const EquipmentSafetyMonitorPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(safetyMonitorProvider);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        _ConnectionCard(status: status),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.safetyMonitor),
          onChanged: (v) =>
              n.setAutoConnect(EquipmentDeviceType.safetyMonitor, v),
        ),
        const SettingsSectionHeader('Behavior'),
        const SettingsRow(
          label: 'On unsafe',
          value: 'Park + close',
          hint: '§35 safety policies — overrideable per profile',
        ),
        const SettingsRow(label: 'Min safe window (min)', value: '15'),
        const SettingsRow(label: 'Auto-resume when safe', value: 'On'),
        const SettingsRow(
          label: 'Resume delay (min)',
          value: '10',
          hint: 'wait this long after first "safe" reading',
        ),
      ],
    );
  }
}

/// Live connection + safe/unsafe card. Renders the four async shapes (loading /
/// error / not-connected / connected) over the device status.
class _ConnectionCard extends ConsumerWidget {
  final AsyncValue<SafetyMonitorStatus?> status;
  const _ConnectionCard({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Card(
      color: AraColors.bgPanel,
      margin: const EdgeInsets.only(top: 4, bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 12, 8, 12),
        child: switch (status) {
          AsyncData(:final value) => value == null
              ? _Disconnected(onConnect: () => _connect(context, ref))
              : _Connected(status: value),
          AsyncError(:final error) => _MessageRow(
              icon: Icons.error_outline,
              color: AraColors.accentError,
              text: "Couldn't read the safety monitor: ${describeEquipmentError(error)}",
              onRetry: () => ref.read(safetyMonitorProvider.notifier).refresh(),
            ),
          _ => const Padding(
              padding: EdgeInsets.all(12),
              child: Center(child: CircularProgressIndicator()),
            ),
        },
      ),
    );
  }

  Future<void> _connect(BuildContext context, WidgetRef ref) {
    final messenger = ScaffoldMessenger.of(context);
    return showAlpacaChooserDialog(
      context,
      EquipmentDeviceType.safetyMonitor,
      deviceTypeLabel: 'safety monitor',
      // Connect the pick directly (rather than the default selection write); the
      // notifier's _acting guard drops a concurrent second connect.
      onPick: (device) async {
        try {
          final performed =
              await ref.read(safetyMonitorProvider.notifier).connect(device);
          if (!performed) {
            messenger.showSnackBar(const SnackBar(
              content: Text('Another connect/disconnect is still in progress.'),
            ));
          }
        } catch (e) {
          messenger.showSnackBar(SnackBar(
            content: Text("Couldn't connect ${device.name}: ${describeEquipmentError(e)}"),
            backgroundColor: AraColors.accentError,
          ));
        }
      },
    );
  }
}

class _Disconnected extends StatelessWidget {
  final VoidCallback onConnect;
  const _Disconnected({required this.onConnect});

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        const EquipmentStateChip(state: EquipmentConnectionState.disconnected),
        const SizedBox(width: 12),
        const Expanded(
          child: Text('No safety monitor connected.'),
        ),
        TextButton.icon(
          onPressed: onConnect,
          icon: const Icon(Icons.search, size: 16),
          label: const Text('Connect…'),
        ),
      ],
    );
  }
}

class _Connected extends ConsumerWidget {
  final SafetyMonitorStatus status;
  const _Connected({required this.status});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final title = status.name.isEmpty ? 'Safety monitor' : status.name;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(title, style: Theme.of(context).textTheme.titleMedium),
            ),
            EquipmentStateChip(state: status.connectionState),
            IconButton(
              // While connecting, the same action aborts the in-progress connect —
              // relabel so the user knows tapping it cancels rather than drops a
              // live device.
              tooltip: status.isConnecting ? 'Cancel connecting' : 'Disconnect',
              icon: Icon(status.isConnecting ? Icons.close : Icons.link_off,
                  size: 18),
              onPressed: () => _disconnect(context, ref),
            ),
          ],
        ),
        const Divider(height: 20, color: AraColors.border),
        // The is_safe reading is only meaningful once connected. While connecting
        // it's still the daemon default (show a transient); an `error` state is a
        // failed read, not a flag — surface it distinctly rather than "Reading…".
        switch (status.connectionState) {
          EquipmentConnectionState.connected => _SafeIndicator(safe: status.safe),
          EquipmentConnectionState.error => Row(children: const [
              Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
              SizedBox(width: 8),
              Expanded(child: Text('Sensor read failed — check the device.')),
            ]),
          _ => const Text('Reading…'),
        },
        if (status.lastTransitionAt != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(
              'Last change: ${_formatTransition(status.lastTransitionAt!)}',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
          ),
      ],
    );
  }

  Future<void> _disconnect(BuildContext context, WidgetRef ref) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed =
          await ref.read(safetyMonitorProvider.notifier).disconnect();
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

class _SafeIndicator extends StatelessWidget {
  final bool safe;
  const _SafeIndicator({required this.safe});

  @override
  Widget build(BuildContext context) {
    final (color, icon, text) = safe
        ? (AraColors.accentConnected, Icons.check_circle, 'Safe')
        : (AraColors.accentError, Icons.warning_amber_rounded, 'Unsafe');
    return Row(
      children: [
        Icon(icon, color: color, size: 22),
        const SizedBox(width: 8),
        Text(
          text,
          style: Theme.of(context)
              .textTheme
              .titleMedium
              ?.copyWith(color: color, fontWeight: FontWeight.w600),
        ),
      ],
    );
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

/// Render the last-transition instant as `YYYY-MM-DD HH:MM UTC`. Shown in UTC
/// (with an explicit label) rather than device-local time — astronomy sessions
/// are conventionally logged in UTC, and an unmarked local time misleads remote/
/// observatory users.
String _formatTransition(DateTime dt) {
  final u = dt.toUtc();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${u.year}-${two(u.month)}-${two(u.day)} ${two(u.hour)}:${two(u.minute)} UTC';
}
