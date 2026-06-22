import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/focuser_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/focuser_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Focuser panel. Shows the connected focuser's live position / temperature
/// and a move control via the shared connection card; the §37.11 autofocus rows
/// below stay as references to the autofocus settings.
class EquipmentFocuserPanel extends ConsumerWidget {
  const EquipmentFocuserPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(focuserProvider);
    final notifier = ref.read(focuserProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FocuserStatus>(
          status: status,
          deviceType: EquipmentDeviceType.focuser,
          deviceTypeLabel: 'focuser',
          emptyLabel: 'No focuser connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FocuserBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.focuser),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.focuser, v),
        ),
        const SettingsSectionHeader('Autofocus'),
        const SettingsRow(
          label: 'Use temp compensation',
          value: 'Off',
          hint: '§37.11 autofocus settings — overrideable per profile',
        ),
        const SettingsRow(label: 'Run AF after filter change', value: 'On'),
        const SettingsRow(label: 'Trigger temp delta (°C)', value: '2.0'),
      ],
    );
  }
}

/// The connected focuser's live body: position + temperature + temp-comp state,
/// and a move-to-target control. A ConsumerStatefulWidget so the target field has
/// a controller and can dispatch the move.
class _FocuserBody extends ConsumerStatefulWidget {
  final FocuserStatus status;
  const _FocuserBody({required this.status});

  @override
  ConsumerState<_FocuserBody> createState() => _FocuserBodyState();
}

class _FocuserBodyState extends ConsumerState<_FocuserBody> {
  // Seeded once from the current position. Intentionally NOT reseeded on live
  // updates — this is the user's target, not a live value (the live position is
  // shown separately), so a background poll can't clobber what they're typing.
  late final TextEditingController _target =
      TextEditingController(text: widget.status.position?.toString() ?? '');

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
        Expanded(child: Text('Focuser read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Position', s.position?.toString() ?? '—'),
        if (s.temperature != null)
          _row('Temperature', '${s.temperature!.toStringAsFixed(1)} °C'),
        _row('Temp. compensation', s.tempCompEnabled ? 'On' : 'Off'),
        if (s.isMoving)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Text('Moving…', style: TextStyle(color: AraColors.accentBusy)),
          ),
        const SizedBox(height: 8),
        Row(
          children: [
            SizedBox(
              width: 140,
              child: TextField(
                controller: _target,
                keyboardType: TextInputType.number,
                inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                decoration: InputDecoration(
                  isDense: true,
                  labelText: 'Target',
                  helperText:
                      caps != null ? 'Range ${caps.minPosition}–${caps.maxPosition}' : null,
                ),
              ),
            ),
            const SizedBox(width: 12),
            FilledButton(
              onPressed: s.isMoving ? null : () => _move(caps),
              child: const Text('Move'),
            ),
          ],
        ),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [
          Expanded(child: Text(label)),
          Text(value),
        ]),
      );

  Future<void> _move(FocuserCapabilities? caps) async {
    final messenger = ScaffoldMessenger.of(context);
    final raw = int.tryParse(_target.text.trim());
    if (raw == null) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter a target position.')));
      return;
    }
    // Clamp to the device's range when known, so a typo can't drive past limits.
    final target = caps != null ? raw.clamp(caps.minPosition, caps.maxPosition) : raw;
    try {
      final performed =
          await ref.read(focuserProvider.notifier).move(target);
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another action is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't move: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}
