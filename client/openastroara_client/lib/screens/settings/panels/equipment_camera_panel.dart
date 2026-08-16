import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/camera_status.dart';
import '../../../models/equipment_device_status.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/camera_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/help_icon.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/equipment/cooler_controls.dart';
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

  @override
  void dispose() {
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(
        children: [
          Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
          SizedBox(width: 8),
          Expanded(child: Text('Camera read failed — check the device.')),
        ],
      );
    }
    final caps = s.capabilities;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
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
        // §25.5.5 — cooler on/off + target presets (−10/−5/0/+5 °C) + custom
        // target + the cooling-fan toggle, shared with the Imaging tab. The
        // daemon owns the safety interlock (cooler on → fan auto-starts;
        // cooler off → fan stops; fan-off while cooling is refused).
        if (caps?.hasCooler ?? false) const CoolerControls(),

        const Divider(height: 20, color: AraColors.border),
        if (caps != null) ...[
          _row('Sensor', '${caps.sensorWidth} × ${caps.sensorHeight}'),
          if (caps.pixelSizeUm > 0)
            _row(
              'Pixel size',
              caps.pixelSizeUmY > 0 && caps.pixelSizeUmY != caps.pixelSizeUm
                  ? '${caps.pixelSizeUm.toStringAsFixed(2)} × ${caps.pixelSizeUmY.toStringAsFixed(2)} μm'
                  : '${caps.pixelSizeUm.toStringAsFixed(2)} μm',
            ),
          _row(
            'Sensor type',
            caps.isColor ? 'Colour (${caps.bayerPattern})' : 'Mono',
          ),
          if (caps.maxGain > caps.minGain)
            _row('Gain range', '${caps.minGain}–${caps.maxGain}'),
          if (caps.maxOffset > caps.minOffset)
            _row('Offset range', '${caps.minOffset}–${caps.maxOffset}'),
          _row('Max binning', '${caps.maxBinX}×${caps.maxBinY}'),
          // §25.5.5 — readout-mode picker (driver-defined list; select by
          // index) as chips, matching the cooler preset chips.
          if (caps.readoutModes.isNotEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    children: [
                      const Text('Readout mode'),
                      HelpIcon(
                          helpKey: 'eq.camera.readout_mode', device: s.name),
                    ],
                  ),
                  const SizedBox(height: 6),
                  Wrap(
                    spacing: 6,
                    runSpacing: 6,
                    children: [
                      for (var i = 0; i < caps.readoutModes.length; i++)
                        ChoiceChip(
                          label: Text(caps.readoutModes[i]),
                          selected:
                              _readoutIndex(caps.readoutModes, s.readoutMode) ==
                                  i,
                          onSelected: s.isBusy
                              ? null
                              : (_) => _run(() => ref
                                  .read(cameraStatusProvider.notifier)
                                  .setReadoutMode(i)),
                          visualDensity: VisualDensity.compact,
                          materialTapTargetSize:
                              MaterialTapTargetSize.shrinkWrap,
                        ),
                    ],
                  ),
                ],
              ),
            ),
        ],
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 2),
    child: Row(
      children: [
        Expanded(child: Text(label)),
        Text(value),
      ],
    ),
  );

  /// The dropdown's selected index: the runtime's current mode name located in
  /// the caps list (null → no selection shown, e.g. daemon didn't report one).
  static int? _readoutIndex(List<String> modes, String? current) {
    if (current == null) return null;
    final i = modes.indexOf(current);
    return i >= 0 ? i : null;
  }

  Future<void> _run(Future<bool> Function() action) async {
    final messenger = ScaffoldMessenger.of(context);
    try {
      final performed = await action();
      if (!performed) {
        messenger.showSnackBar(
          const SnackBar(content: Text('Another action is still in progress.')),
        );
      }
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't change that: ${describeEquipmentError(e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
    }
  }
}
