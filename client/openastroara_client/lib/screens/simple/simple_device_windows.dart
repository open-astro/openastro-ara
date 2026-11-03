import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart' show HapticFeedback;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../services/equipment_driver_api.dart';
import '../../models/focuser_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../models/guider_status.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
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
  static const double targetMin = -40, targetMax = 10, targetStep = 5;

  @override
  ConsumerState<SimpleCameraWindow> createState() => _SimpleCameraWindowState();
}

class _SimpleCameraWindowState extends ConsumerState<SimpleCameraWindow> {
  // While a thumb is held: the value it shows, sent only on letting go.
  double? _dragTarget;

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(cameraStatusProvider).value;
    final target =
        _dragTarget ?? ref.watch(imagingDefaultsProvider).coolerTargetC;
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
      // The camera's gain, shared with Preview's chip.
      children.add(
        SimpleGainSlider(
          id: 'simple-camera-gain',
          value: ref.watch(exposureControllerProvider).gain,
          onSet: _setGain,
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

  /// The camera's gain, the same one Preview's chip sets (a plan starts from
  /// it and keeps its own).
  void _setGain(int gain) =>
      ref.read(exposureControllerProvider.notifier).setGain(gain);

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

/// − value +, for a device window's numbers (refocus, dither).
Widget simpleWindowStepper({
  required String id,
  required String text,
  required VoidCallback onLess,
  required VoidCallback onMore,
}) {
  Widget button(String dir, IconData icon, String label, VoidCallback onTap) =>
      IconButton(
        key: Key('$id-$dir'),
        onPressed: onTap,
        icon: Icon(icon, semanticLabel: label),
      );
  return DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        button('down', Icons.remove, 'Less', onLess),
        SizedBox(
          width: 96,
          child: Text(text, textAlign: TextAlign.center, style: kSimpleText),
        ),
        button('up', Icons.add, 'More', onMore),
      ],
    ),
  );
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
    this.enabled = true,
  });

  final String id;
  final bool on;
  final ValueChanged<bool> onChanged;
  final (String, String) labels;

  /// False: both segments show the state but take no tap.
  final bool enabled;

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
            onTap: on == v || !enabled ? null : () => onChanged(v),
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

/// The mount's window: tracking (On / Off), Parked / Unparked, and the
/// automatic meridian flip (the profile's, so plans flip by it too).
class SimpleMountWindow extends ConsumerStatefulWidget {
  const SimpleMountWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the mount's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleMountWindow> createState() => _SimpleMountWindowState();
}

class _SimpleMountWindowState extends ConsumerState<SimpleMountWindow> {
  /// Asked for and not there yet (the daemon reports a park as a slew).
  bool? _parking;

  /// Parked tapped while a plan runs: the window asks first.
  bool _asking = false;

  @override
  Widget build(BuildContext context) {
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
      // A park is done once the mount says so; until then it reads Parking….
      if (_parking != null &&
          m.parked == _parking &&
          m.runtimeState != 'unparking') {
        _parking = null;
      }
      final moving = _parking != null || m.runtimeState == 'unparking';
      // Watched, so it's known by the time Parked is tapped.
      final planRunning =
          ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
      if (caps?.canSetTracking ?? false) {
        children.add(
          simpleWindowRow(
            'Tracking',
            _OnOff(
              id: 'simple-mount-tracking',
              on: m.tracking && !m.parked,
              // A parked (or parking) mount doesn't track.
              enabled: !m.parked && !moving,
              onChanged: (on) => _run(context, () => notifier.setTracking(on)),
            ),
          ),
        );
      }
      children.add(
        simpleWindowRow(
          'Park',
          moving
              ? SizedBox(
                  height: 44,
                  child: Center(
                    child: Text(
                      (_parking ?? !m.parked) ? 'Parking…' : 'Unparking…',
                      style: kSimpleText.copyWith(color: AraColors.accentBusy),
                    ),
                  ),
                )
              : _OnOff(
                  id: 'simple-mount-park',
                  labels: const ('Parked', 'Unparked'),
                  on: m.parked,
                  enabled: m.parked
                      ? caps?.canUnpark ?? false
                      : caps?.canPark ?? false,
                  onChanged: (park) {
                    if (park && planRunning) {
                      setState(() => _asking = true);
                    } else {
                      _park(context, park);
                    }
                  },
                ),
        ),
      );
      if (_asking) {
        children.add(
          Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(
                child: Text(
                  'A plan is running. Park the mount anyway?',
                  style: kSimpleText.copyWith(color: AraColors.accentBusy),
                ),
              ),
              TextButton(
                onPressed: () => setState(() => _asking = false),
                child: const Text('Cancel'),
              ),
              TextButton(
                onPressed: () {
                  setState(() => _asking = false);
                  _park(context, true);
                },
                child: const Text('Park anyway'),
              ),
            ],
          ),
        );
      }
      children.add(
        simpleWindowRow(
          'Meridian flip',
          _OnOff(
            id: 'simple-mount-flip',
            labels: const ('Auto', 'Off'),
            on: ref.watch(safetyPoliciesProvider).meridianFlipAuto,
            onChanged: (on) => _setFlip(context, on),
          ),
        ),
      );
    }
    if (widget.inSettings) {
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

  void _park(BuildContext context, bool park) {
    final notifier = ref.read(mountProvider.notifier);
    setState(() => _parking = park);
    _run(context, park ? notifier.park : notifier.unpark).then((ok) {
      if (!ok && mounted) setState(() => _parking = null);
    });
  }

  /// Set once: the profile keeps it.
  Future<void> _setFlip(BuildContext context, bool on) async {
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

  /// False when the mount refused (a pill says why).
  Future<bool> _run(
    BuildContext context,
    Future<bool> Function() action,
  ) async {
    final notify = userNotifier(context);
    try {
      return await action();
    } catch (e) {
      notify(
        "Couldn't move the mount",
        detail: describeEquipmentError(e),
        error: true,
      );
      return false;
    }
  }
}

/// Gain as a slider over the connected camera's range in steps of 10 (named
/// gain modes slide through their names), with its value beside the label;
/// set when the thumb is let go. The Camera window, Preview's settings and
/// Plan's sheet share it.
class SimpleGainSlider extends ConsumerStatefulWidget {
  const SimpleGainSlider({
    super.key,
    required this.id,
    required this.value,
    required this.onSet,
  });

  final String id;
  final int value;
  final ValueChanged<int> onSet;

  @override
  ConsumerState<SimpleGainSlider> createState() => _SimpleGainSliderState();
}

class _SimpleGainSliderState extends ConsumerState<SimpleGainSlider> {
  double? _drag;

  @override
  Widget build(BuildContext context) {
    final caps = ref.watch(cameraStatusProvider).value?.capabilities;
    final named = caps?.gains ?? const <String>[];
    final lo = named.isNotEmpty ? 0 : caps?.minGain ?? 0;
    final reported = caps?.maxGain ?? 0;
    final hi = named.isNotEmpty
        ? named.length - 1
        : (reported > 0 ? reported : 300);
    final gain = _drag?.round() ?? widget.value;
    String text(int g) =>
        named.isNotEmpty && g >= 0 && g < named.length ? named[g] : '$g';
    // A numeric gain moves in steps of 10 (the range's ends stay reachable).
    double snap(double v) =>
        named.isNotEmpty ? v : ((v / 10).round() * 10).clamp(lo, hi).toDouble();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        simpleWindowRow('Gain', Text(text(gain), style: kSimpleText)),
        Slider(
          key: Key(widget.id),
          min: lo.toDouble(),
          max: hi.toDouble(),
          divisions: hi > lo ? hi - lo : null,
          value: gain.clamp(lo, hi).toDouble(),
          label: text(gain),
          onChanged: (v) => setState(() => _drag = snap(v)),
          onChangeEnd: (v) {
            setState(() => _drag = null);
            widget.onSet(snap(v).round());
          },
        ),
      ],
    );
  }
}

/// "12 000": a focuser position, grouped in thousands.
String simpleSteps(int n) => n
    .abs()
    .toString()
    .replaceAllMapped(RegExp(r'\B(?=(\d{3})+$)'), (_) => ' ')
    .padLeft(n < 0 ? 1 : 0, '−');

/// The focuser's window: where it is, its temperature, and a knob to move
/// it. A detent (30°) is one step of the size picked under it; the target
/// shows while turning and the focuser goes there on letting go.
class SimpleFocuserWindow extends ConsumerStatefulWidget {
  const SimpleFocuserWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the focuser's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleFocuserWindow> createState() =>
      _SimpleFocuserWindowState();
}

class _SimpleFocuserWindowState extends ConsumerState<SimpleFocuserWindow> {
  int _step = 100;

  /// While the knob turns: where it will send the focuser.
  int? _target;

  /// The last place sent, for "Moving to …".
  int? _sent;

  int _clamp(int v, FocuserStatus s) {
    final c = s.capabilities;
    return c == null || c.maxPosition <= c.minPosition
        ? v
        : v.clamp(c.minPosition, c.maxPosition);
  }

  Future<void> _go(FocuserStatus s, int target) async {
    final from = s.position ?? 0;
    if (target == from) return;
    setState(() => _sent = target);
    // A relative focuser takes the change, not a position.
    final absolute = s.capabilities?.absoluteFocuser ?? true;
    final notify = userNotifier(context);
    try {
      await ref
          .read(focuserProvider.notifier)
          .move(absolute ? target : target - from);
    } catch (e) {
      notify(
        "Couldn't move the focuser",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  /// The profile's autofocus temperature trigger: every run Ara builds
  /// refocuses after the focuser drifts this much (0 = off).
  Widget _refocus() {
    final c = ref.watch(autofocusSettingsProvider).triggerTempDeltaC;
    return simpleWindowStepper(
      id: 'simple-focuser-refocus',
      text: c <= 0 ? 'Off' : simpleCelsius(c),
      onLess: () => _setRefocus(c - 0.5),
      onMore: () => _setRefocus(c + 0.5),
    );
  }

  Future<void> _setRefocus(double c) async {
    final af = ref.read(autofocusSettingsProvider.notifier);
    af.setTriggerTempDeltaC(c.clamp(0, 10).toDouble());
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    final notify = userNotifier(context);
    try {
      await af.persistToServer(api);
    } catch (e) {
      notify(
        "Couldn't save that",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(focuserProvider).value;
    final children = <Widget>[SimpleDeviceHeader(type: 'Focuser', status: s)];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false ? 'Connecting…' : 'Not connected',
          style: AraText.caption,
        ),
      );
    } else {
      final pos = s.position ?? 0;
      final shown = _target ?? pos;
      children.addAll([
        simpleWindowRow(
          'Position',
          Text(simpleSteps(shown), style: kSimpleText),
        ),
        if (s.temperature != null)
          simpleWindowRow(
            'Temperature',
            Text(simpleCelsius(s.temperature!), style: kSimpleText),
          ),
        SizedBox(
          height: 18,
          child: Text(
            _target != null
                ? 'Let go to move to ${simpleSteps(_target!)}'
                : s.isMoving
                ? (_sent != null
                      ? 'Moving to ${simpleSteps(_sent!)}…'
                      : 'Moving…')
                : '',
            textAlign: TextAlign.center,
            style: AraText.caption.copyWith(color: AraColors.accentBusy),
          ),
        ),
        Center(
          child: Semantics(
            slider: true,
            label: 'Focus position',
            value: simpleSteps(pos),
            increasedValue: simpleSteps(_clamp(pos + _step, s)),
            decreasedValue: simpleSteps(_clamp(pos - _step, s)),
            onIncrease: () => _go(s, _clamp(pos + _step, s)),
            onDecrease: () => _go(s, _clamp(pos - _step, s)),
            child: _FocusKnob(
              key: const Key('simple-focuser-knob'),
              onDetent: (d) {
                HapticFeedback.selectionClick();
                setState(
                  () => _target = _clamp((_target ?? pos) + d * _step, s),
                );
              },
              onRelease: () {
                final t = _target;
                setState(() => _target = null);
                if (t != null) _go(s, t);
              },
              // The middle runs autofocus (its square cancels it).
              centre: const _AutofocusButton(),
            ),
          ),
        ),
        const SizedBox(height: AraSpace.s16),
        Center(
          child: DecoratedBox(
            key: const Key('simple-focuser-steps'),
            decoration: BoxDecoration(
              color: AraColors.tileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                for (final n in const [10, 100, 1000])
                  SimpleSegment(
                    key: Key('simple-focuser-step-$n'),
                    label: '$n',
                    selected: _step == n,
                    radius: 8,
                    onTap: () => setState(() => _step = n),
                  ),
              ],
            ),
          ),
        ),
        Text(
          'steps per click',
          textAlign: TextAlign.center,
          style: AraText.caption,
        ),
        const SizedBox(height: AraSpace.s8),
        simpleWindowRow('Refocus after a change of', _refocus()),
      ]);
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(ref.watch(deviceDriverProvider('focuser')).value),
      );
    }
    return Column(
      key: const Key('simple-device-focuser'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }
}

/// A focuser knob drawn like a safe's dial: the whole face turns under a
/// fixed mark, reporting each 30° detent (clockwise out, +1;
/// counter-clockwise in, −1), then the release. [centre] sits in the middle
/// and takes its own taps. It claims the finger at once (no drag slop), so
/// the dial follows from the first touch. iOS has no rotary control of its
/// own.
class _FocusKnob extends StatefulWidget {
  const _FocusKnob({
    super.key,
    required this.onDetent,
    required this.onRelease,
    required this.centre,
  });

  final ValueChanged<int> onDetent;
  final VoidCallback onRelease;
  final Widget centre;

  @override
  State<_FocusKnob> createState() => _FocusKnobState();
}

class _FocusKnobState extends State<_FocusKnob> {
  static const _size = 220.0;
  static const _detent = math.pi / 6;

  /// The face's angle, and the turn not yet a whole detent.
  double _angle = 0;
  double _pending = 0;
  double? _last;

  double _at(Offset p) => math.atan2(p.dy - _size / 2, p.dx - _size / 2);

  void _turn(Offset p) {
    final a = _at(p);
    var d = a - (_last ?? a);
    if (d > math.pi) d -= 2 * math.pi;
    if (d < -math.pi) d += 2 * math.pi;
    _last = a;
    setState(() => _angle += d);
    _pending += d;
    final whole = (_pending / _detent).truncate();
    if (whole != 0) {
      _pending -= whole * _detent;
      widget.onDetent(whole);
    }
  }

  void _end() {
    if (_last == null) return;
    _last = null;
    _pending = 0;
    widget.onRelease();
  }

  @override
  Widget build(BuildContext context) => SizedBox.square(
    dimension: _size,
    child: Stack(
      alignment: Alignment.center,
      children: [
        RawGestureDetector(
          gestures: {
            _EagerPan: GestureRecognizerFactoryWithHandlers<_EagerPan>(
              _EagerPan.new,
              (r) {
                r
                  ..dragStartBehavior = DragStartBehavior.down
                  ..onStart = (d) {
                    _last = _at(d.localPosition);
                  }
                  ..onUpdate = (d) {
                    _turn(d.localPosition);
                  }
                  ..onEnd = (_) {
                    _end();
                  }
                  ..onCancel = _end;
              },
            ),
          },
          child: Transform.rotate(
            key: const Key('simple-focuser-dial-face'),
            angle: _angle,
            child: const CustomPaint(
              size: Size.square(_size),
              painter: _DialPainter(),
            ),
          ),
        ),
        // The fixed mark the face turns under; a finger on it turns the dial.
        const Positioned(
          top: 0,
          child: IgnorePointer(
            child: Icon(
              Icons.arrow_drop_down,
              size: 28,
              color: AraColors.accentInfo,
            ),
          ),
        ),
        SizedBox.square(dimension: _size * 0.42, child: widget.centre),
      ],
    ),
  );
}

/// A pan that wins the finger on touch-down, ahead of the sheet's scroll and
/// without waiting for the drag slop.
class _EagerPan extends PanGestureRecognizer {
  _EagerPan({super.debugOwner});

  @override
  void addAllowedPointer(PointerDownEvent event) {
    super.addAllowedPointer(event);
    resolve(GestureDisposition.accepted);
  }
}

/// The dial's face: a knurled rim and graduations, every 30° (a detent)
/// long, every 6° short.
class _DialPainter extends CustomPainter {
  const _DialPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.width / 2 - 14;
    canvas.drawCircle(c, r, Paint()..color = AraColors.tileFill);
    // The knurled grip round the edge.
    final grip = Paint()
      ..color = AraColors.border
      ..strokeWidth = 2;
    for (var i = 0; i < 90; i++) {
      final a = i * 2 * math.pi / 90;
      final dir = Offset(math.cos(a), math.sin(a));
      canvas.drawLine(c + dir * (r - 6), c + dir * r, grip);
    }
    for (var i = 0; i < 60; i++) {
      final a = i * math.pi / 30 - math.pi / 2;
      final dir = Offset(math.cos(a), math.sin(a));
      final major = i % 5 == 0;
      canvas.drawLine(
        c + dir * (r - (major ? 26 : 16)),
        c + dir * (r - 10),
        Paint()
          ..color = major ? AraColors.textPrimary : AraColors.textSecondary
          ..strokeWidth = major ? 3 : 1.5,
      );
    }
  }

  @override
  bool shouldRepaint(_DialPainter old) => false;
}

/// The dial's middle: Autofocus, a square to cancel while it runs.
class _AutofocusButton extends ConsumerWidget {
  const _AutofocusButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(autofocusLiveProvider);
    final running = live.run.isRunning;
    final af = ref.read(autofocusLiveProvider.notifier);
    return Material(
      color: AraColors.bgPrimary,
      shape: const CircleBorder(side: BorderSide(color: AraColors.border)),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: const Key('simple-focuser-autofocus'),
        onTap: live.busy ? null : (running ? af.cancel : af.start),
        child: Semantics(
          button: true,
          label: running ? 'Cancel autofocus' : 'Autofocus',
          excludeSemantics: true,
          child: Center(
            child: running
                ? const Icon(Icons.stop, color: AraColors.accentError, size: 32)
                : Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.center_focus_strong, size: 26),
                      Text('Autofocus', style: AraText.caption),
                    ],
                  ),
          ),
        ),
      ),
    );
  }
}

/// The guider's window: what it is doing, the guiding RMS, and how often a
/// plan dithers (the profile's, so every run Ara builds dithers by it).
class SimpleGuiderWindow extends ConsumerWidget {
  const SimpleGuiderWindow({super.key});

  static String _arcsec(double v) => '${v.toStringAsFixed(2)}″';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final g = ref.watch(guiderStatusProvider).value;
    final colour = switch (g?.connectionState) {
      GuiderConnectionState.connected => AraColors.accentConnected,
      GuiderConnectionState.connecting => AraColors.accentBusy,
      GuiderConnectionState.error => AraColors.accentError,
      _ => AraColors.textDisabled,
    };
    final children = <Widget>[
      Row(
        spacing: AraSpace.s8,
        children: [
          Container(
            width: 10,
            height: 10,
            decoration: BoxDecoration(color: colour, shape: BoxShape.circle),
          ),
          Expanded(
            child: Text(
              g == null ? 'Guider' : 'Guider · ${g.name}',
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
        ],
      ),
    ];
    if (g == null || !g.isConnected) {
      children.add(
        Text(
          g?.connectionState == GuiderConnectionState.connecting
              ? 'Connecting…'
              : 'Not connected',
          style: AraText.caption,
        ),
      );
    } else {
      children.add(
        simpleWindowRow('State', Text(g.runtimeSummary, style: kSimpleText)),
      );
      if (g.rmsTotalArcsec != null) {
        children.add(
          simpleWindowRow(
            'RMS',
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(_arcsec(g.rmsTotalArcsec!), style: kSimpleText),
                if (g.rmsRaArcsec != null && g.rmsDecArcsec != null)
                  Text(
                    'RA ${_arcsec(g.rmsRaArcsec!)} · Dec ${_arcsec(g.rmsDecArcsec!)}',
                    style: AraText.caption,
                  ),
              ],
            ),
          ),
        );
      }
    }
    final phd2 = ref.watch(phd2SettingsProvider);
    final every = phd2.ditherEnabled ? math.max(1, phd2.ditherEveryNFrames) : 0;
    children.add(
      simpleWindowRow(
        'Dither',
        simpleWindowStepper(
          id: 'simple-guider-dither',
          text: switch (every) {
            0 => 'Off',
            1 => 'Every frame',
            _ => '$every frames',
          },
          onLess: () => _setDither(context, ref, every - 1),
          onMore: () => _setDither(context, ref, every + 1),
        ),
      ),
    );
    return Column(
      key: const Key('simple-device-guider'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  /// 0 turns dithering off; 1 and up dithers every that many frames.
  Future<void> _setDither(BuildContext context, WidgetRef ref, int n) async {
    if (n < 0 || n > 20) return;
    final p = ref.read(phd2SettingsProvider.notifier);
    p.setDitherEnabled(n > 0);
    if (n > 0) p.setDitherEveryNFrames(n);
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    final notify = userNotifier(context);
    try {
      await p.persistToServer(api);
    } catch (e) {
      notify(
        "Couldn't save that",
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }
}
