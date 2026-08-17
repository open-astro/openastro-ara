import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/switch_device.dart';
import '../../services/equipment_device_api.dart';
import '../../theme/ara_colors.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/switch_state.dart';

/// Cooling-fan control for cameras whose fan is exposed by the bridge as a
/// ToupTek Thermal Switch element (the ATR2600M's `TOUPCAM_OPTION_FAN` port).
///
/// Hidden entirely when no connected switch has a "Fan" port. Safety: turning
/// the fan off while the TEC is cooling is refused — the fan vents the TEC's
/// heat sink, and cooling with the fan off can damage the camera.
class FanSwitchRow extends ConsumerWidget {
  const FanSwitchRow({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final switches = ref.watch(switchListProvider).maybeWhen(
        data: (v) => v,
        orElse: () => const <SwitchDevice>[],
      );
    final fan = _findFanPort(switches);
    // Only a boolean (on/off, range [0,1]) Fan port renders as a toggle — a
    // PWM/value fan port would be silently forced to full on/off otherwise.
    if (fan == null || !fan.port.isBoolean) return const SizedBox.shrink();

    final camera = ref.watch(cameraStatusProvider).maybeWhen(
        data: (v) => v,
        orElse: () => null,
      );
    final cooling = camera?.coolerOn ?? false;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 2),
      child: Row(
        children: [
          Expanded(
            child: Text(
              cooling
                  ? 'Cooling fan (needed while cooling)'
                  : 'Cooling fan',
            ),
          ),
          Switch(
            value: fan.port.value > 0,
            onChanged: (on) => _toggle(context, ref, fan, on),
          ),
        ],
      ),
    );
  }

  Future<void> _toggle(
    BuildContext context,
    WidgetRef ref,
    ({SwitchDevice device, SwitchPort port}) fan,
    bool on,
  ) async {
    final messenger = ScaffoldMessenger.of(context);
    final cooling = ref
          .read(cameraStatusProvider)
          .maybeWhen(data: (v) => v?.coolerOn ?? false, orElse: () => false);
    if (!on && cooling) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text(
              "Turn the cooler off before stopping the fan — cooling with "
              'the fan off can damage the camera.'),
          backgroundColor: Color(0xFFB3261E),
        ),
      );
      return;
    }
    try {
      final ok = await ref
          .read(switchListProvider.notifier)
          .setValue(
            deviceId: fan.device.deviceId,
            portId: fan.port.id,
            value: on ? 1.0 : 0.0,
          );
      if (!ok && context.mounted) {
        messenger.showSnackBar(
          const SnackBar(
              content: Text('Another switch action is in progress.')),
        );
      }
    } catch (e) {
      // A failed switch write must surface (mirror EquipmentSwitchPanel's
      // _PortRow) — otherwise the fire-and-forget onChanged swallows it and
      // the tap silently fails to flip.
      if (!context.mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text("Couldn't set the fan: ${describeEquipmentError(e)}"),
          backgroundColor: AraColors.accentError,
        ),
      );
    }
  }

  /// The first connected switch device that exposes a "Fan" port, or null.
  static ({SwitchDevice device, SwitchPort port})? _findFanPort(
      List<SwitchDevice> switches) {
    for (final device in switches) {
      if (!device.isConnected) continue;
      for (final port in device.ports) {
        if (port.name == 'Fan' && port.canWrite) {
          return (device: device, port: port);
        }
      }
    }
    return null;
  }
}
