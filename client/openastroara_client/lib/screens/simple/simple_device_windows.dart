import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../services/equipment_driver_api.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/settings/safety_policies_state.dart';
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
  const SimpleCameraWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the camera's versions; the short one
  /// from the dot leaves them out.
  final bool inSettings;

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
    // In Settings, the versions the rig reports (support asks), set out like
    // Rig's.
    if (widget.inSettings) {
      children.addAll(
        _versions(ref.watch(deviceDriverProvider('camera')).value),
      );
    }
    return Column(
      key: const Key('simple-device-camera'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      // Tight: each row is already 44 pt, and a slider sits under its row.
      spacing: AraSpace.s4,
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

  static Widget _row(String label, Widget value) =>
      simpleWindowRow(label, value);
}

/// What the rig reports of a device's versions, set out like Rig's (only in
/// Settings, under a divider); nothing when it reports none.
List<Widget> _versions(DeviceDriver? d) {
  final rows = [
    if (d?.sdkVersion != null) ('SDK', d!.sdkVersion!),
    if (d?.driverVersion != null) ('Driver', d!.driverVersion!),
    if (d?.firmware != null) ('Firmware', d!.firmware!),
    if (d?.serialNumber != null) ('Serial number', d!.serialNumber!),
  ];
  return rows.isEmpty
      ? const []
      : [const Divider(height: AraSpace.s16), SimpleVersions(rows: rows)];
}

/// One height for every row of a device window (the 44 pt of its controls),
/// so a text-only row keeps the rhythm.
Widget simpleWindowRow(String label, Widget value) => ConstrainedBox(
  key: Key('simple-window-row-$label'),
  constraints: const BoxConstraints(minHeight: 44),
  child: Row(
    children: [
      Expanded(child: Text(label, style: kSimpleText)),
      value,
    ],
  ),
);

/// On / Off as named segments, never a bare toggle (DESIGN.md); [labels]
/// names them otherwise ("Auto" / "Off").
class _OnOff extends StatelessWidget {
  const _OnOff({
    required this.id,
    required this.on,
    required this.onChanged,
    this.labels = ('On', 'Off'),
  });

  final String id;
  final bool on;
  final ValueChanged<bool> onChanged;
  final (String, String) labels;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        for (final (v, word) in [(true, labels.$1), (false, labels.$2)])
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

/// A titled two-column list of versions, each beside its name (Settings ›
/// Rig, and a device's page in Settings).
class SimpleVersions extends StatelessWidget {
  const SimpleVersions({super.key, required this.rows});

  final List<(String, String)> rows;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: AraSpace.s8,
    children: [
      Text(
        'Versions',
        style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
      ),
      Table(
        defaultColumnWidth: const IntrinsicColumnWidth(),
        defaultVerticalAlignment: TableCellVerticalAlignment.baseline,
        textBaseline: TextBaseline.alphabetic,
        children: [
          for (final (name, value) in rows)
            TableRow(
              children: [
                Padding(
                  padding: const EdgeInsets.only(
                    right: AraSpace.s24,
                    bottom: AraSpace.s4,
                  ),
                  child: Text(
                    name,
                    style: kSimpleText.copyWith(color: AraColors.textSecondary),
                  ),
                ),
                Text(value, style: kSimpleText),
              ],
            ),
        ],
      ),
    ],
  );
}

/// The mount's window: tracking (On / Off), park or unpark, and the
/// automatic meridian flip (the profile's, so plans flip by it too).
class SimpleMountWindow extends ConsumerWidget {
  const SimpleMountWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the mount's versions.
  final bool inSettings;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final m = ref.watch(mountProvider).value;
    final children = <Widget>[SimpleDeviceHeader(type: 'Mount', status: m)];
    if (m == null || !m.isConnected) {
      children.add(
        Text(
          m?.isConnecting ?? false ? 'Connecting…' : 'Not connected',
          style: AraText.caption,
        ),
      );
    } else {
      final caps = m.capabilities;
      final notifier = ref.read(mountProvider.notifier);
      if (caps?.canSetTracking ?? false) {
        children.add(
          simpleWindowRow(
            'Tracking',
            _OnOff(
              id: 'simple-mount-tracking',
              on: m.tracking,
              onChanged: (on) => _run(context, () => notifier.setTracking(on)),
            ),
          ),
        );
      }
      final canAct = m.parked
          ? caps?.canUnpark ?? false
          : caps?.canPark ?? false;
      children.add(
        simpleWindowRow(
          'Park',
          Row(
            mainAxisSize: MainAxisSize.min,
            spacing: AraSpace.s12,
            children: [
              if (m.parked) const Text('Parked', style: kSimpleText),
              TextButton(
                key: const Key('simple-mount-park'),
                // 44 pt like the window's other controls.
                style: TextButton.styleFrom(
                  tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                ),
                onPressed: canAct
                    ? () => _run(
                        context,
                        m.parked ? notifier.unpark : notifier.park,
                      )
                    : null,
                child: Text(m.parked ? 'Unpark' : 'Park now'),
              ),
            ],
          ),
        ),
      );
      children.add(
        simpleWindowRow(
          'Meridian flip',
          _OnOff(
            id: 'simple-mount-flip',
            labels: const ('Auto', 'Off'),
            on: ref.watch(safetyPoliciesProvider).meridianFlipAuto,
            onChanged: (on) => _setFlip(context, ref, on),
          ),
        ),
      );
    }
    if (inSettings) {
      children.addAll(
        _versions(ref.watch(deviceDriverProvider('telescope')).value),
      );
    }
    return Column(
      key: const Key('simple-device-mount'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  /// Set once: the profile keeps it.
  Future<void> _setFlip(BuildContext context, WidgetRef ref, bool on) async {
    final policies = ref.read(safetyPoliciesProvider.notifier);
    policies.setMeridianFlipAuto(on);
    final notify = userNotifier(context);
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    try {
      await policies.persistToServer(api);
    } catch (e) {
      notify(
        "Couldn't save the meridian flip",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  Future<void> _run(
    BuildContext context,
    Future<bool> Function() action,
  ) async {
    final notify = userNotifier(context);
    try {
      await action();
    } catch (e) {
      notify(
        "Couldn't move the mount",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }
}
