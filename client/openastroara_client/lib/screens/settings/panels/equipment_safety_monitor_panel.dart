import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/safety_monitor_status.dart';
import '../../../state/equipment/safety_monitor_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/equipment/equipment_time_format.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Safety Monitor panel. Shows the device's live `is_safe` reading +
/// connection state with connect/disconnect (via the shared connection card); the
/// §35 safety-policy rows below stay as references to the profile policy.
class EquipmentSafetyMonitorPanel extends ConsumerWidget {
  const EquipmentSafetyMonitorPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(safetyMonitorProvider);
    final notifier = ref.read(safetyMonitorProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<SafetyMonitorStatus>(
          status: status,
          deviceType: EquipmentDeviceType.safetyMonitor,
          deviceTypeLabel: 'safety monitor',
          emptyLabel: 'No safety monitor connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _SafetyBody(status: s),
        ),
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

/// The connected device's live body: a Safe/Unsafe indicator (or a distinct
/// message for the connecting / error sub-states) + the last-transition time.
class _SafetyBody extends StatelessWidget {
  final SafetyMonitorStatus status;
  const _SafetyBody({required this.status});

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // is_safe is only meaningful once connected; while connecting it's the
        // daemon default (transient), and `error` is a failed read, not a flag.
        switch (status.connectionState) {
          EquipmentConnectionState.connected =>
            _SafeIndicator(safe: status.safe),
          EquipmentConnectionState.error => const Row(children: [
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
              'Last change: ${formatUtcMinute(status.lastTransitionAt!)}',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
          ),
      ],
    );
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
