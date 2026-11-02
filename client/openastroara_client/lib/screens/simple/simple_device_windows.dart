import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../state/equipment/camera_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_preview_panel.dart' show kSimpleText;
import 'simple_shell.dart' show SimpleSegment;

/// One window per device in Simple Mode (design/briefs/simple-mode-settings.md):
/// the same widget opens from the device's dot in the top bar and as its page
/// in Settings. Bare minimum, no Connect / Disconnect (Settings › Devices has
/// those).

/// The status dot and "Type · name" every device window starts with.
class SimpleDeviceHeader extends StatelessWidget {
  const SimpleDeviceHeader({super.key, required this.type, this.status});

  final String type;
  final EquipmentDeviceStatus? status;

  @override
  Widget build(BuildContext context) {
    final s = status;
    final color = switch (s?.connectionState) {
      EquipmentConnectionState.connected => AraColors.accentConnected,
      EquipmentConnectionState.connecting => AraColors.accentBusy,
      EquipmentConnectionState.error => AraColors.accentError,
      _ => AraColors.textDisabled,
    };
    return Row(
      spacing: AraSpace.s8,
      children: [
        Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        Expanded(
          child: Text(
            s == null || s.name.isEmpty ? type : '$type · ${s.name}',
            style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
          ),
        ),
      ],
    );
  }
}

/// A °C value as the windows show it: a real minus sign, no ".0".
String simpleCelsius(double c) {
  final rounded = c == c.roundToDouble()
      ? c.toStringAsFixed(0)
      : c.toStringAsFixed(1);
  return '${rounded.replaceFirst('-', '−')} °C';
}

/// The camera's window: the cooler (On / Off), its target and what the
/// sensor reads now. The target is the profile's, so plans cool to it too.
class SimpleCameraWindow extends ConsumerWidget {
  const SimpleCameraWindow({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(cameraStatusProvider).value;
    final target = ref.watch(imagingDefaultsProvider).coolerTargetC;
    final children = <Widget>[SimpleDeviceHeader(type: 'Camera', status: s)];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false ? 'Connecting…' : 'Not connected',
          style: AraText.caption,
        ),
      );
    } else {
      final caps = s.capabilities;
      final now = [
        if (s.ccdTemperature != null) simpleCelsius(s.ccdTemperature!),
        if (s.coolerPowerPct != null) '${s.coolerPowerPct!.round()} % power',
      ].join(' · ');
      if (caps == null || !caps.hasCooler) {
        children.add(
          _row('Cooler', const Text('No cooler', style: kSimpleText)),
        );
      } else {
        children.add(
          _row(
            'Cooler',
            _OnOff(
              id: 'simple-camera-cooler',
              on: s.coolerOn,
              onChanged: (on) => _run(
                context,
                ref,
                () => ref
                    .read(cameraStatusProvider.notifier)
                    .setCooler(on, targetTemperatureC: on ? target : null),
              ),
            ),
          ),
        );
        if (caps.canSetTemperature) {
          children.add(
            _row(
              'Target',
              Row(
                mainAxisSize: MainAxisSize.min,
                spacing: AraSpace.s8,
                children: [
                  _Step(
                    key: const Key('simple-camera-target-colder'),
                    icon: Icons.remove,
                    label: 'Colder',
                    onTap: () =>
                        _setTarget(context, ref, target - 1, s.coolerOn),
                  ),
                  SizedBox(
                    width: 64,
                    child: Text(
                      simpleCelsius(target),
                      style: kSimpleText,
                      textAlign: TextAlign.center,
                    ),
                  ),
                  _Step(
                    key: const Key('simple-camera-target-warmer'),
                    icon: Icons.add,
                    label: 'Warmer',
                    onTap: () =>
                        _setTarget(context, ref, target + 1, s.coolerOn),
                  ),
                ],
              ),
            ),
          );
        }
      }
      if (now.isNotEmpty) {
        children.add(_row('Now', Text(now, style: kSimpleText)));
      }
    }
    return Column(
      key: const Key('simple-device-camera'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s12,
      children: children,
    );
  }

  /// Set once: the profile keeps it (plans cool to it), and a running cooler
  /// follows it at once.
  Future<void> _setTarget(
    BuildContext context,
    WidgetRef ref,
    double celsius,
    bool coolerOn,
  ) async {
    final defaults = ref.read(imagingDefaultsProvider.notifier);
    final before = ref.read(imagingDefaultsProvider).coolerTargetC;
    defaults.setCoolerTargetC(celsius);
    if (ref.read(imagingDefaultsProvider).coolerTargetC == before) return;
    final notify = userNotifier(context);
    final api = ref.read(profileApiProvider);
    if (api != null) {
      try {
        await defaults.persistToServer(api);
      } catch (e) {
        notify(
          "Couldn't save the target",
          detail: describeEquipmentError(e),
          error: true,
        );
      }
    }
    if (coolerOn && context.mounted) {
      await _run(
        context,
        ref,
        () => ref
            .read(cameraStatusProvider.notifier)
            .setCooler(true, targetTemperatureC: celsius),
      );
    }
  }

  Future<void> _run(
    BuildContext context,
    WidgetRef ref,
    Future<bool> Function() action,
  ) async {
    final notify = userNotifier(context);
    try {
      await action();
    } catch (e) {
      notify(
        "Couldn't change the cooler",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  static Widget _row(String label, Widget value) => Row(
    children: [
      Expanded(child: Text(label, style: kSimpleText)),
      value,
    ],
  );
}

/// On / Off as named segments, never a bare toggle (DESIGN.md).
class _OnOff extends StatelessWidget {
  const _OnOff({required this.id, required this.on, required this.onChanged});

  final String id;
  final bool on;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final (v, word) in const [(true, 'On'), (false, 'Off')])
          SimpleSegment(
            key: Key('$id-${word.toLowerCase()}'),
            label: word,
            selected: on == v,
            radius: 8,
            onTap: on == v ? null : () => onChanged(v),
          ),
      ],
    ),
  );
}

class _Step extends StatelessWidget {
  const _Step({
    super.key,
    required this.icon,
    required this.label,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Material(
    color: AraColors.tileFill,
    borderRadius: BorderRadius.circular(8),
    child: InkWell(
      borderRadius: BorderRadius.circular(8),
      onTap: onTap,
      child: Semantics(
        button: true,
        label: label,
        child: SizedBox(width: 44, height: 44, child: Icon(icon, size: 20)),
      ),
    ),
  );
}
