import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/switch_device.dart';
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
    if (fan == null) return const SizedBox.shrink();

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
    final ok = await ref
        .read(switchListProvider.notifier)
        .setValue(
          deviceId: fan.device.deviceId,
          portId: fan.port.id,
          value: on ? 1.0 : 0.0,
        );
    if (!ok && context.mounted) {
      messenger.showSnackBar(
        const SnackBar(content: Text('Another switch action is in progress.')),
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
