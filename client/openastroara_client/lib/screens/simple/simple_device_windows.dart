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
class SimpleCameraWindow extends ConsumerStatefulWidget {
  const SimpleCameraWindow({super.key});

  /// The target slider's range and step (user, 2026-11-03: a stepped slider).
  static const double targetMin = -30, targetMax = 10, targetStep = 5;

  @override
  ConsumerState<SimpleCameraWindow> createState() => _SimpleCameraWindowState();
}

class _SimpleCameraWindowState extends ConsumerState<SimpleCameraWindow> {
  // While a thumb is held: the value it shows, sent only on letting go.
  double? _dragTarget;
  double? _dragGain;

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(cameraStatusProvider).value;
    final target =
        _dragTarget ?? ref.watch(imagingDefaultsProvider).coolerTargetC;
    final gain =
        _dragGain?.round() ?? ref.watch(imagingDefaultsProvider).defaultGain;
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
                () => ref
                    .read(cameraStatusProvider.notifier)
                    .setCooler(on, targetTemperatureC: on ? target : null),
              ),
            ),
          ),
        );
        if (caps.canSetTemperature) {
          const lo = SimpleCameraWindow.targetMin,
              hi = SimpleCameraWindow.targetMax;
          children.add(
            _row('Target', Text(simpleCelsius(target), style: kSimpleText)),
          );
          children.add(
            Slider(
              key: const Key('simple-camera-target'),
              min: lo,
              max: hi,
              divisions: ((hi - lo) / SimpleCameraWindow.targetStep).round(),
              value: target.clamp(lo, hi),
              label: simpleCelsius(target),
              onChanged: (v) => setState(() => _dragTarget = v),
              onChangeEnd: (v) {
                setState(() => _dragTarget = null);
                _setTarget(v, s.coolerOn);
              },
            ),
          );
        }
      }
      if (now.isNotEmpty) {
        children.add(_row('Now', Text(now, style: kSimpleText)));
      }
      // The profile's gain: what plans shoot at (Preview's chip sets its own).
      // Named gain modes slide through their names.
      final named = caps?.gains ?? const <String>[];
      final gMin = named.isNotEmpty ? 0 : caps?.minGain ?? 0;
      final reported = caps?.maxGain ?? 0;
      final gMax = named.isNotEmpty
          ? named.length - 1
          : (reported > 0 ? reported : 300);
      String gainText(int g) =>
          named.isNotEmpty && g >= 0 && g < named.length ? named[g] : '$g';
      children.add(_row('Gain', Text(gainText(gain), style: kSimpleText)));
      children.add(
        Slider(
          key: const Key('simple-camera-gain'),
          min: gMin.toDouble(),
          max: gMax.toDouble(),
          divisions: gMax > gMin ? gMax - gMin : null,
          value: gain.clamp(gMin, gMax).toDouble(),
          label: gainText(gain),
          onChanged: (v) => setState(() => _dragGain = v),
          onChangeEnd: (v) {
            setState(() => _dragGain = null);
            _setGain(v.round());
          },
        ),
      );
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
  Future<void> _setTarget(double celsius, bool coolerOn) async {
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
    if (coolerOn && mounted) {
      await _run(
        () => ref
            .read(cameraStatusProvider.notifier)
            .setCooler(true, targetTemperatureC: celsius),
      );
    }
  }

  /// Set once, like the target: the profile keeps it.
  Future<void> _setGain(int gain) async {
    final defaults = ref.read(imagingDefaultsProvider.notifier);
    defaults.setGain(gain);
    final notify = userNotifier(context);
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    try {
      await defaults.persistToServer(api);
    } catch (e) {
      notify(
        "Couldn't save the gain",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  Future<void> _run(Future<bool> Function() action) async {
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

  // One height for every row (the 44 pt of the On / Off and − / + controls),
  // so a text-only row like Now keeps the rhythm.
  static Widget _row(String label, Widget value) => ConstrainedBox(
    key: Key('simple-window-row-$label'),
    constraints: const BoxConstraints(minHeight: 44),
    child: Row(
      children: [
        Expanded(child: Text(label, style: kSimpleText)),
        value,
      ],
    ),
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
