import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/camera_status.dart';
import '../../../models/equipment_device_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/camera_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 + §52 Camera panel. Shows the connected camera's live CCD temperature
/// and cooler with on/off + set-point control, plus the sensor/gain/offset/bin
/// capabilities, via the shared connection card.
class EquipmentCameraPanel extends ConsumerWidget {
  const EquipmentCameraPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(cameraStatusProvider);
    final notifier = ref.read(cameraStatusProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<CameraStatus>(
          status: status,
          deviceType: EquipmentDeviceType.camera,
          deviceTypeLabel: 'camera',
          emptyLabel: 'No camera connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _CameraBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.camera),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.camera, v),
          hint: '§52.1 connection lifecycle',
        ),
      ],
    );
  }
}

/// The connected camera's live body: cooling (temp/power/on-off + set-point) and
/// a read-only sensor/gain/offset/bin capability summary.
class _CameraBody extends ConsumerStatefulWidget {
  final CameraStatus status;
  const _CameraBody({required this.status});

  @override
  ConsumerState<_CameraBody> createState() => _CameraBodyState();
}

class _CameraBodyState extends ConsumerState<_CameraBody> {
  // The cooler target the user is setting (their intent, not a live mirror).
  late final TextEditingController _target = TextEditingController(text: '-10');

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
        Expanded(child: Text('Camera read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('CCD temperature',
            s.ccdTemperature == null ? '—' : '${s.ccdTemperature!.toStringAsFixed(1)} °C'),
        if (s.coolerPowerPct != null)
          _row('Cooler power', '${s.coolerPowerPct!.toStringAsFixed(0)} %'),
        if (s.isExposing)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: Text(
              s.exposureProgressPct == null
                  ? 'Exposing…'
                  : 'Exposing… ${s.exposureProgressPct!.toStringAsFixed(0)}%',
              style: const TextStyle(color: AraColors.accentBusy),
            ),
          ),
        if (caps?.canSetTemperature ?? false) ...[
          Row(children: [
            const Expanded(child: Text('Cooler')),
            Switch(
              value: s.coolerOn,
              onChanged: (v) => _setCooler(v),
            ),
          ]),
          Row(children: [
            SizedBox(
              width: 130,
              child: TextField(
                controller: _target,
                keyboardType:
                    const TextInputType.numberWithOptions(signed: true, decimal: true),
                inputFormatters: [
                  FilteringTextInputFormatter.allow(RegExp(r'^-?[0-9]*\.?[0-9]*$')),
                ],
                decoration: const InputDecoration(
                    isDense: true, labelText: 'Target (°C)'),
              ),
            ),
            const SizedBox(width: 12),
            OutlinedButton(
              onPressed: () => _setTarget(),
              child: const Text('Set target'),
            ),
          ]),
        ],
        const Divider(height: 20, color: AraColors.border),
        if (caps != null) ...[
          _row('Sensor', '${caps.sensorWidth} × ${caps.sensorHeight}'),
          if (caps.pixelSizeUm > 0)
            _row('Pixel size', '${caps.pixelSizeUm.toStringAsFixed(2)} μm'),
          _row('Sensor type', caps.isColor ? 'Colour (${caps.bayerPattern})' : 'Mono'),
          if (caps.maxGain > caps.minGain)
            _row('Gain range', '${caps.minGain}–${caps.maxGain}'),
          if (caps.maxOffset > caps.minOffset)
            _row('Offset range', '${caps.minOffset}–${caps.maxOffset}'),
          _row('Max binning', '${caps.maxBinX}×${caps.maxBinY}'),
        ],
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [Expanded(child: Text(label)), Text(value)]),
      );

  // The Switch only toggles the cooler. CoolerOn and the set-point are
  // independent ASCOM properties, so toggling never carries a (possibly stale)
  // target — that's the "Set target" button's job.
  Future<void> _setCooler(bool on) =>
      _run(() => ref.read(cameraStatusProvider.notifier).setCooler(on));

  Future<void> _setTarget() async {
    final messenger = ScaffoldMessenger.of(context);
    final t = _parseTarget();
    if (t == null) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter a target temperature.')));
      return;
    }
    // Setting a target turns the cooler on.
    await _run(() => ref
        .read(cameraStatusProvider.notifier)
        .setCooler(true, targetTemperatureC: t));
  }

  double? _parseTarget() => double.tryParse(_target.text.trim());

  Future<void> _run(Future<bool> Function() action) async {
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
        content: Text("Couldn't set cooler: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}
