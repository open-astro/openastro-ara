import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/services.dart' show HapticFeedback, LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/equipment_device_status.dart';
import '../../services/equipment_device_api.dart' show describeEquipmentError;
import '../../services/equipment_driver_api.dart';
import '../../models/focuser_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../models/guider_status.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/guider/guider_equipment_state.dart';
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
import '../../state/app_locale_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/status_indicator.dart' show StatusBlink, StatusDot;
import 'simple_preview_panel.dart' show SimpleStopSquare, kSimpleText;
import 'simple_shell.dart' show SimpleSegment, simpleRailScale;

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
      EquipmentConnectionState.connecting => AraColors.accentInfo,
      EquipmentConnectionState.error => AraColors.accentError,
      _ => AraColors.textDisabled,
    };
    return Row(
      spacing: AraSpace.s8,
      children: [
        StatusDot(
          color: color,
          ring: s?.connectionState == EquipmentConnectionState.connecting,
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
    final children = <Widget>[
      SimpleDeviceHeader(type: context.l10n.deviceCamera, status: s),
    ];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? context.l10n.deviceConnecting
              : context.l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      final caps = s.capabilities;
      final now = [
        if (s.ccdTemperature != null) simpleCelsius(s.ccdTemperature!),
        if (s.coolerPowerPct != null)
          context.l10n.devicePowerPercent(s.coolerPowerPct!.round()),
      ].join(' · ');
      // What the sensor reads, first.
      if (now.isNotEmpty) {
        children.add(
          _row(
            'Current temperature',
            context.l10n.deviceCurrentTemperature,
            Text(now, style: kSimpleText),
          ),
        );
      }
      if (caps == null || !caps.hasCooler) {
        children.add(
          _row(
            'Cooler',
            context.l10n.deviceCooler,
            Text(context.l10n.deviceNoCooler, style: kSimpleText),
          ),
        );
      } else {
        children.add(
          _row(
            'Cooler',
            context.l10n.deviceCooler,
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
        // The target only while the cooler is on (On cools to the profile's).
        if (caps.canSetTemperature && s.coolerOn) {
          const lo = SimpleCameraWindow.targetMin,
              hi = SimpleCameraWindow.targetMax;
          children.add(
            _row(
              'Target',
              context.l10n.deviceTarget,
              Text(simpleCelsius(target), style: kSimpleText),
            ),
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
        _versions(context, ref.watch(deviceDriverProvider('camera')).value),
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
    final notify = userNotifier(context, origin: SimpleStatusOrigin.camera);
    final l10n = context.l10n;
    final api = ref.read(profileApiProvider);
    if (api != null) {
      try {
        await defaults.persistToServer(api);
      } catch (e) {
        notify(
          l10n.deviceCouldntSaveTarget,
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
    final notify = userNotifier(context, origin: SimpleStatusOrigin.camera);
    final l10n = context.l10n;
    try {
      if (!await action()) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntChangeCooler,
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  static Widget _row(String id, String label, Widget value) =>
      simpleWindowRow(label, value, id: id);
}

/// A tap the device's notifier dropped (its last command still running)
/// says so: one pill at a time, fading like a result (ALP-4982).
void _sayBusy(WidgetRef ref, AppLocalizations l10n) {
  final messages = ref.read(simpleMessagesProvider.notifier);
  if (ref.read(simpleMessagesProvider).any((m) => m.title == l10n.deviceBusy)) {
    return;
  }
  final id = messages.show(
    SimpleMessageKind.error,
    l10n.deviceBusy,
    detail: l10n.deviceBusyDetail,
  );
  Timer(kSimpleResultLife, () => messages.dismiss(id));
}

/// What the rig reports of a device's versions, set out like Rig's (only in
/// Settings, under a divider); nothing when it reports none.
List<Widget> _versions(BuildContext context, DeviceDriver? d) {
  final l10n = context.l10n;
  final rows = [
    if (d?.sdkVersion != null) (l10n.deviceSdk, d!.sdkVersion!),
    if (d?.driverVersion != null) (l10n.deviceDriver, d!.driverVersion!),
    if (d?.firmware != null) (l10n.deviceFirmware, d!.firmware!),
    if (d?.serialNumber != null) (l10n.deviceSerialNumber, d!.serialNumber!),
  ];
  return rows.isEmpty
      ? const []
      : [const Divider(height: AraSpace.s16), SimpleVersions(rows: rows)];
}

/// The width of a device window's steppers and segment rows, so the controls
/// under each other line up as one column.
const double kSimpleWindowControlWidth = 216;

/// − value +, for a device window's numbers (refocus, dither).
Widget simpleWindowStepper({
  required String id,
  required String text,
  required VoidCallback onLess,
  required VoidCallback onMore,
}) {
  Widget button(
    String dir,
    IconData icon,
    String Function(AppLocalizations) label,
    VoidCallback onTap,
  ) => Builder(
    builder: (context) => IconButton(
      key: Key('$id-$dir'),
      onPressed: onTap,
      icon: Icon(icon, semanticLabel: label(context.l10n)),
    ),
  );
  return DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(8),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        button('down', Icons.remove, (l) => l.deviceLess, onLess),
        // Gives way in a narrow window, keeping the buttons whole.
        Flexible(
          child: SizedBox(
            width: kSimpleWindowControlWidth - 96, // less the two 48 pt buttons
            child: Text(text, textAlign: TextAlign.center, style: kSimpleText),
          ),
        ),
        button('up', Icons.add, (l) => l.deviceMore, onMore),
      ],
    ),
  );
}

/// One height for every row of a device window (the 44 pt of its controls),
/// so a text-only row keeps the rhythm. [id] (English) names its Key. Too
/// narrow for label and control side by side (a one-third Split View), the
/// label goes over the control.
Widget simpleWindowRow(String label, Widget value, {required String id}) =>
    LayoutBuilder(
      key: Key('simple-window-row-$id'),
      builder: (context, box) => box.maxWidth < kSimpleWindowControlWidth + 96
          ? Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              spacing: AraSpace.s4,
              children: [
                Text(label, style: kSimpleText),
                value,
              ],
            )
          : ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 44),
              child: Row(
                children: [
                  Expanded(child: Text(label, style: kSimpleText)),
                  value,
                ],
              ),
            ),
    );

/// On / Off as named segments, never a bare toggle (DESIGN.md); [labels]
/// names them otherwise ("Auto" / "Off"), [ids] their Keys in English.
class _OnOff extends StatelessWidget {
  const _OnOff({
    required this.id,
    required this.on,
    required this.onChanged,
    this.labels,
    this.ids = ('on', 'off'),
    this.enabled = true,
  });

  final String id;
  final bool on;
  final ValueChanged<bool> onChanged;
  final (String, String)? labels;
  final (String, String) ids;

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
        for (final (v, key, word) in [
          (true, ids.$1, labels?.$1 ?? context.l10n.deviceOn),
          (false, ids.$2, labels?.$2 ?? context.l10n.deviceOff),
        ])
          SimpleSegment(
            key: Key('$id-$key'),
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
/// Rig, a device's page in Settings and the OpenAstro Ara sheet).
class SimpleVersions extends StatelessWidget {
  const SimpleVersions({super.key, required this.rows, this.title});

  final List<(String, String)> rows;

  /// "Versions" unless given.
  final String? title;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: AraSpace.s8,
    children: [
      Text(
        title ?? context.l10n.deviceVersions,
        style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
      ),
      Table(
        defaultColumnWidth: const IntrinsicColumnWidth(),
        // A value too long for its row wraps instead of running out.
        columnWidths: const {1: FlexColumnWidth()},
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

/// A park (true) or unpark (false) Simple Mode asked for, until the mount
/// gets there: the Mount window reads Parking… / Unparking… and the status
/// pill says so. The daemon reports a park as a slew, so the ask is what
/// tells a park from any other move. Null when none is under way.
class SimpleMountMotionNotifier extends Notifier<bool?> {
  /// Tracking On on a parked mount: track once the unpark lands.
  bool _trackAfter = false;

  /// The mount has moved since the ask: stopping now without getting there
  /// means the move ended short.
  bool _moved = false;

  /// The look again after the mount stopped short of the ask.
  Timer? _settle;

  void _unfinished(bool asked) {
    _settle?.cancel();
    _settle = null;
    _trackAfter = false;
    state = null;
    final l10n = l10nFor(ref.read(appLocaleProvider));
    ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.error,
          asked ? l10n.deviceParkUnfinished : l10n.deviceUnparkUnfinished,
          origin: SimpleStatusOrigin.mount,
        );
  }

  @override
  bool? build() {
    ref.onDispose(() => _settle?.cancel());
    ref.listen(mountProvider, (_, next) {
      final asked = state;
      if (asked == null || next.isLoading) return;
      final m = next.value;
      // Gone: say so at once, and give the Park row its control back.
      if (m == null || !m.isConnected) {
        _unfinished(asked);
        return;
      }
      // Stopped short of the ask (aborted, faulted)? Drivers often drop
      // Slewing a moment before AtPark, so look again in a moment first
      // (review of #1486); an unchanged mount sends no new reading.
      if (_moved && !m.isBusy && m.parked != asked) {
        _settle ??= Timer(const Duration(seconds: 2), () {
          _settle = null;
          final now = ref.read(mountProvider).value;
          if (state == asked &&
              (now == null || (!now.isBusy && now.parked != asked))) {
            _unfinished(asked);
          }
        });
        return;
      }
      _settle?.cancel();
      _settle = null;
      if (m.isBusy) _moved = true;
      // Done once the mount says so and has stopped moving out of park.
      if (m.parked == asked &&
          m.runtimeState != 'unparking' &&
          (asked || m.runtimeState != 'slewing')) {
        final track = _trackAfter && !asked;
        _trackAfter = false;
        state = null;
        if (track) _track().ignore();
      }
    });
    return null;
  }

  /// Tracking on after an unpark: a mount still settling refuses it, so it
  /// is asked again, up to five times a second apart.
  Future<void> _track() async {
    for (var i = 0; i < 5; i++) {
      if (!ref.mounted) return; // gone mid-retry (#1485)
      try {
        if (await ref.read(mountProvider.notifier).setTracking(true)) return;
      } catch (_) {
        // Still settling; ask again.
      }
      await Future<void>.delayed(const Duration(seconds: 1));
    }
  }

  /// Parks or unparks; true when the mount took the ask (it throws why not).
  Future<bool> park(bool park, {bool thenTrack = false}) async {
    final mount = ref.read(mountProvider.notifier);
    state = park;
    _trackAfter = thenTrack;
    _moved = false;
    try {
      final ok = await (park ? mount.park() : mount.unpark());
      if (!ok) state = null;
      return ok;
    } catch (_) {
      state = null;
      rethrow;
    }
  }
}

final simpleMountMotionProvider =
    NotifierProvider<SimpleMountMotionNotifier, bool?>(
      SimpleMountMotionNotifier.new,
    );

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
  /// Parked tapped while a plan runs: the window asks first.
  bool _asking = false;

  @override
  Widget build(BuildContext context) {
    final m = ref.watch(mountProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: context.l10n.deviceMount, status: m),
    ];
    if (m == null || !m.isConnected) {
      children.add(
        Text(
          m?.isConnecting ?? false
              ? context.l10n.deviceConnecting
              : context.l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      final caps = m.capabilities;
      final notifier = ref.read(mountProvider.notifier);
      // A park is done once the mount says so; until then it reads Parking….
      final parking = ref.watch(simpleMountMotionProvider);
      final moving = parking != null || m.runtimeState == 'unparking';
      // Watched, so it's known by the time Parked is tapped.
      final planRunning =
          ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
      if (caps?.canSetTracking ?? false) {
        children.add(
          simpleWindowRow(
            context.l10n.deviceTracking,
            id: 'Tracking',
            _OnOff(
              id: 'simple-mount-tracking',
              on: m.tracking && !m.parked,
              // A parked mount unparks first, then tracks; none while moving.
              enabled: !moving && (!m.parked || (caps?.canUnpark ?? false)),
              onChanged: (on) => on && m.parked
                  ? _park(context, false, thenTrack: true)
                  : _run(context, () => notifier.setTracking(on)),
            ),
          ),
        );
      }
      children.add(
        simpleWindowRow(
          context.l10n.devicePark,
          id: 'Park',
          moving
              ? SizedBox(
                  height: 44,
                  child: Center(
                    child: Text(
                      (parking ?? !m.parked)
                          ? context.l10n.deviceParking
                          : context.l10n.deviceUnparking,
                      style: kSimpleText.copyWith(color: AraColors.accentInfo),
                    ),
                  ),
                )
              : _OnOff(
                  id: 'simple-mount-park',
                  ids: const ('parked', 'unparked'),
                  labels: (
                    context.l10n.deviceParked,
                    context.l10n.deviceUnparked,
                  ),
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
                  context.l10n.devicePlanRunningParkAnyway,
                  style: kSimpleText.copyWith(color: AraColors.accentBusy),
                ),
              ),
              TextButton(
                onPressed: () => setState(() => _asking = false),
                child: Text(context.l10n.deviceCancel),
              ),
              TextButton(
                onPressed: () {
                  setState(() => _asking = false);
                  _park(context, true);
                },
                child: Text(context.l10n.deviceParkAnyway),
              ),
            ],
          ),
        );
      }
      children.add(
        simpleWindowRow(
          context.l10n.deviceMeridianFlip,
          id: 'Meridian flip',
          _OnOff(
            id: 'simple-mount-flip',
            ids: const ('auto', 'off'),
            labels: (context.l10n.deviceAuto, context.l10n.deviceOff),
            on: ref.watch(safetyPoliciesProvider).meridianFlipAuto,
            onChanged: (on) => _setFlip(context, on),
          ),
        ),
      );
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(context, ref.watch(deviceDriverProvider('telescope')).value),
      );
    }
    return Column(
      key: const Key('simple-device-mount'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  void _park(BuildContext context, bool park, {bool thenTrack = false}) => _run(
    context,
    () => ref
        .read(simpleMountMotionProvider.notifier)
        .park(park, thenTrack: thenTrack),
  );

  /// Set once: the profile keeps it.
  Future<void> _setFlip(BuildContext context, bool on) async {
    final policies = ref.read(safetyPoliciesProvider.notifier);
    policies.setMeridianFlipAuto(on);
    final notify = userNotifier(context, origin: SimpleStatusOrigin.mount);
    final l10n = context.l10n;
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    try {
      await policies.persistToServer(api);
    } catch (e) {
      notify(
        l10n.deviceCouldntSaveMeridianFlip,
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
    final notify = userNotifier(context, origin: SimpleStatusOrigin.mount);
    final l10n = context.l10n;
    try {
      final performed = await action();
      if (!performed) _sayBusy(ref, l10n);
      return performed;
    } catch (e) {
      notify(
        l10n.deviceCouldntMoveMount,
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
        simpleWindowRow(
          context.l10n.deviceGain,
          id: 'Gain',
          Text(text(gain), style: kSimpleText),
        ),
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

  /// The knob takes Tab like any control; the arrows move it a step.
  final _dialFocus = FocusNode(debugLabel: 'simple-focuser-dial');
  bool _dialFocused = false;

  @override
  void dispose() {
    _dialFocus.dispose();
    super.dispose();
  }

  int _clamp(int v, FocuserStatus s) {
    final c = s.capabilities;
    return c == null || c.maxPosition <= c.minPosition
        ? v
        : v.clamp(c.minPosition, c.maxPosition);
  }

  /// The dial moves nothing while autofocus runs (a touch would spoil its
  /// curve) or while the position is unknown (a turn would jump from 0).
  bool _locked(FocuserStatus s) =>
      s.position == null || ref.read(autofocusLiveProvider).run.isRunning;

  Future<void> _go(FocuserStatus s, int target) async {
    if (_locked(s)) return;
    final from = s.position!;
    if (target == from) return;
    setState(() => _sent = target);
    // A relative focuser takes the change, not a position.
    final absolute = s.capabilities?.absoluteFocuser ?? true;
    final notify = userNotifier(context, origin: SimpleStatusOrigin.focuser);
    final l10n = context.l10n;
    try {
      final performed = await ref
          .read(focuserProvider.notifier)
          .move(absolute ? target : target - from);
      if (!performed) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntMoveFocuser,
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  /// The profile's autofocus temperature trigger: every run Ara builds
  /// refocuses after the focuser drifts this much (0 = off).
  /// While the refocus thumb is held: the value it shows, saved on letting go.
  double? _dragRefocus;

  /// The profile's autofocus temperature trigger as a stepped slider under
  /// its row: whole degrees, 0 = never.
  List<Widget> _refocus() {
    final saved = ref.watch(autofocusSettingsProvider).triggerTempDeltaC;
    final c = _dragRefocus ?? saved;
    String text(double v) =>
        v <= 0 ? context.l10n.deviceNever : simpleCelsius(v);
    return [
      simpleWindowRow(
        context.l10n.deviceRefocusTemp,
        id: 'Refocus temp',
        Text(text(c), style: kSimpleText),
      ),
      Slider(
        key: const Key('simple-focuser-refocus'),
        min: 0,
        max: 10,
        divisions: 10,
        value: c.clamp(0, 10).roundToDouble(),
        label: text(c.roundToDouble()),
        onChanged: (v) => setState(() => _dragRefocus = v),
        onChangeEnd: (v) {
          setState(() => _dragRefocus = null);
          _setRefocus(v.roundToDouble());
        },
      ),
    ];
  }

  Future<void> _setRefocus(double c) async {
    final af = ref.read(autofocusSettingsProvider.notifier);
    af.setTriggerTempDeltaC(c.clamp(0, 10).toDouble());
    final api = ref.read(profileApiProvider);
    if (api == null) return;
    final notify = userNotifier(context, origin: SimpleStatusOrigin.focuser);
    final l10n = context.l10n;
    try {
      await af.persistToServer(api);
    } catch (e) {
      notify(
        l10n.deviceCouldntSaveThat,
        detail: describeEquipmentError(e),
        error: true,
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(focuserProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: context.l10n.deviceFocuser, status: s),
    ];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? context.l10n.deviceConnecting
              : context.l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      final pos = s.position ?? 0;
      children.addAll([
        // Where the focuser is; where it will go shows above the dial.
        simpleWindowRow(
          context.l10n.devicePosition,
          id: 'Position',
          Text(simpleSteps(pos), style: kSimpleText),
        ),
        if (s.temperature != null)
          simpleWindowRow(
            context.l10n.deviceTemperature,
            id: 'Temperature',
            Text(simpleCelsius(s.temperature!), style: kSimpleText),
          ),
        SizedBox(
          height: 18,
          // Moving is working: blinking green (DESIGN.md status rule).
          child: StatusBlink(
            on: _target == null && s.isMoving,
            child: Text(
            _target != null
                ? context.l10n.deviceLetGoToMoveTo(simpleSteps(_target!))
                : s.isMoving
                ? (_sent != null
                      ? context.l10n.deviceMovingTo(simpleSteps(_sent!))
                      : context.l10n.deviceMoving)
                : '',
            textAlign: TextAlign.center,
            style: AraText.caption.copyWith(color: AraColors.accentConnected),
          ),
          ),
        ),
        LayoutBuilder(
          builder: (context, box) => Center(
            child: FocusableActionDetector(
              focusNode: _dialFocus,
              onShowFocusHighlight: (v) => setState(() => _dialFocused = v),
              // Up or right turns it out, like the dial clockwise.
              shortcuts: const {
                SingleActivator(LogicalKeyboardKey.arrowUp): _DialStep(1),
                SingleActivator(LogicalKeyboardKey.arrowRight): _DialStep(1),
                SingleActivator(LogicalKeyboardKey.arrowDown): _DialStep(-1),
                SingleActivator(LogicalKeyboardKey.arrowLeft): _DialStep(-1),
              },
              actions: {
                _DialStep: CallbackAction<_DialStep>(
                  onInvoke: (i) => _go(s, _clamp(pos + i.dir * _step, s)),
                ),
              },
              child: Semantics(
                slider: true,
                label: context.l10n.deviceFocusPosition,
                value: simpleSteps(pos),
                increasedValue: simpleSteps(_clamp(pos + _step, s)),
                decreasedValue: simpleSteps(_clamp(pos - _step, s)),
                onIncrease: () => _go(s, _clamp(pos + _step, s)),
                onDecrease: () => _go(s, _clamp(pos - _step, s)),
                child: _FocusKnob(
                  key: const Key('simple-focuser-knob'),
                  focused: _dialFocused,
                  enabled:
                      s.position != null &&
                      !ref.watch(
                        autofocusLiveProvider.select((l) => l.run.isRunning),
                      ),
                  // Grows with the text like the rails, so the label in its
                  // middle fits; never wider than the window.
                  size: math.min(220 * simpleRailScale(context), box.maxWidth),
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
                  // The system took the finger: nothing moves.
                  onCancel: () => setState(() => _target = null),
                  // The middle runs autofocus (its square cancels it).
                  centre: const _AutofocusButton(),
                ),
              ),
            ),
          ),
        ),
        const SizedBox(height: AraSpace.s16),
        // Rows like the Guider window's: label left, control right.
        simpleWindowRow(
          context.l10n.deviceStep,
          id: 'Step',
          Container(
            key: const Key('simple-focuser-steps'),
            width: kSimpleWindowControlWidth,
            decoration: BoxDecoration(
              color: AraColors.tileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              children: [
                for (final n in const [10, 100, 1000])
                  Expanded(
                    child: SimpleSegment(
                      key: Key('simple-focuser-step-$n'),
                      label: '$n',
                      selected: _step == n,
                      radius: 8,
                      onTap: () => setState(() => _step = n),
                    ),
                  ),
              ],
            ),
          ),
        ),
        ..._refocus(),
      ]);
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(context, ref.watch(deviceDriverProvider('focuser')).value),
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
    required this.onCancel,
    required this.centre,
    this.focused = false,
    this.size = 220,
    this.enabled = true,
  });

  /// The dial's diameter: 220, more with large text, less in a narrow
  /// window.
  final double size;

  final ValueChanged<int> onDetent;
  final VoidCallback onRelease;

  /// A turn the system cut off (a pointer cancel), instead of [onRelease].
  final VoidCallback onCancel;
  final Widget centre;

  /// Off: the face is dimmed and turns nothing; the middle still works.
  final bool enabled;

  /// Keyboard focus: a ring round the face.
  final bool focused;

  @override
  State<_FocusKnob> createState() => _FocusKnobState();
}

class _FocusKnobState extends State<_FocusKnob> {
  double get _size => widget.size;
  static const _detent = math.pi / 6;

  /// The face's angle, and the turn not yet a whole detent.
  double _angle = 0;
  double _pending = 0;
  double? _last;

  double _at(Offset p) => math.atan2(p.dy - _size / 2, p.dx - _size / 2);

  void _turn(Offset p) {
    if (!widget.enabled) return;
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

  /// Set by a pointer cancel, which reaches the pan as an ordinary end.
  bool _cancelled = false;

  void _end() {
    if (_last == null) return;
    _last = null;
    _pending = 0;
    _cancelled ? widget.onCancel() : widget.onRelease();
    _cancelled = false;
  }

  @override
  Widget build(BuildContext context) => SizedBox.square(
    dimension: _size,
    child: Stack(
      alignment: Alignment.center,
      children: [
        if (widget.focused)
          DecoratedBox(
            decoration: const ShapeDecoration(
              shape: CircleBorder(
                side: BorderSide(color: AraColors.accentInfo, width: 2),
              ),
            ),
            child: SizedBox.square(dimension: _size),
          ),
        // A listener sees the cancel before the pan turns it into an end.
        Listener(
          onPointerDown: (_) => _cancelled = false,
          onPointerCancel: (_) => _cancelled = true,
          child: RawGestureDetector(
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
                    ..onCancel = () {
                      _cancelled = true;
                      _end();
                    };
                },
              ),
            },
            child: Opacity(
              opacity: widget.enabled ? 1 : 0.4,
              child: Transform.rotate(
                key: const Key('simple-focuser-dial-face'),
                angle: _angle,
                child: CustomPaint(
                  size: Size.square(_size),
                  painter: const _DialPainter(),
                ),
              ),
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

/// One keyboard step of the focuser knob: +1 out, −1 in.
class _DialStep extends Intent {
  const _DialStep(this.dir);
  final int dir;
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
          label: running
              ? context.l10n.deviceCancelAutofocus
              : context.l10n.deviceAutofocus,
          excludeSemantics: true,
          child: Center(
            child: running
                ? const SimpleStopSquare()
                : Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(Icons.center_focus_strong, size: 26),
                      Text(
                        context.l10n.deviceAutofocus,
                        style: AraText.caption,
                        textAlign: TextAlign.center,
                      ),
                    ],
                  ),
          ),
        ),
      ),
    );
  }
}

/// The guider's window, titled with its camera like Camera's: what it is
/// doing, the guiding RMS, the guide camera's gain, and how often and how far
/// a plan dithers (the profile's, so every run Ara builds uses them).
class SimpleGuiderWindow extends ConsumerStatefulWidget {
  const SimpleGuiderWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the guide camera's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleGuiderWindow> createState() => _SimpleGuiderWindowState();
}

class _SimpleGuiderWindowState extends ConsumerState<SimpleGuiderWindow> {
  @override
  Widget build(BuildContext context) {
    final g = ref.watch(guiderStatusProvider).value;
    final camera = ref.watch(deviceDriverProvider('guidecamera')).value;
    final colour = switch (g?.connectionState) {
      GuiderConnectionState.connected => AraColors.accentConnected,
      GuiderConnectionState.connecting => AraColors.accentInfo,
      GuiderConnectionState.error => AraColors.accentError,
      _ => AraColors.textDisabled,
    };
    final children = <Widget>[
      Row(
        spacing: AraSpace.s8,
        children: [
          StatusDot(
            color: colour,
            ring: g?.connectionState == GuiderConnectionState.connecting,
          ),
          Expanded(
            child: Text(
              camera?.name == null
                  ? context.l10n.deviceGuiderCamera
                  : '${context.l10n.deviceGuiderCamera} · ${camera!.name}',
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
              ? context.l10n.deviceConnecting
              : context.l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    }
    // The guide camera's gain, the same slider and value as Guide mode's.
    children.add(const SimpleGuideGainSlider());
    if (widget.inSettings) children.addAll(_versions(context, camera));
    return Column(
      key: const Key('simple-device-guider'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }
}

/// Changes the guider settings and saves them to the profile; [live] also
/// sends the guider its tuning (exposure, gain) without stopping guiding.
Future<void> simpleSaveGuider(
  BuildContext context,
  WidgetRef ref,
  void Function(Phd2SettingsNotifier) change, {
  bool live = false,
}) async {
  // The container, not ref: a mode change can unmount the panel while the
  // save is out, and a disposed ref would turn a saved setting into
  // "Couldn't save that" (review, #1490).
  final container = ProviderScope.containerOf(context, listen: false);
  final p = container.read(phd2SettingsProvider.notifier);
  change(p);
  final api = container.read(profileApiProvider);
  if (api == null) return;
  final notify = userNotifier(context);
  final l10n = context.l10n;
  try {
    await p.persistToServer(api);
    if (live &&
        container.read(guiderStatusProvider).value?.isConnected == true) {
      await container
          .read(guiderEquipmentProvider.notifier)
          .pushProfile(tuningOnly: true);
    }
  } catch (e) {
    notify(
      l10n.deviceCouldntSaveThat,
      detail: describeEquipmentError(e),
      error: true,
    );
  }
}

/// The guide camera's gain: the guider's own scale, percent of the camera's
/// range, set when the thumb is let go (Guide mode and the Guider Camera
/// window share it).
class SimpleGuideGainSlider extends ConsumerStatefulWidget {
  const SimpleGuideGainSlider({super.key});

  @override
  ConsumerState<SimpleGuideGainSlider> createState() =>
      _SimpleGuideGainSliderState();
}

class _SimpleGuideGainSliderState extends ConsumerState<SimpleGuideGainSlider> {
  double? _drag;

  @override
  Widget build(BuildContext context) {
    final gain =
        _drag ?? ref.watch(phd2SettingsProvider).guideCameraGain.toDouble();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        simpleWindowRow(
          context.l10n.deviceGain,
          id: 'Gain',
          Text('${gain.round()} %', style: kSimpleText),
        ),
        Slider(
          key: const Key('simple-guider-gain'),
          min: 0,
          max: 100,
          divisions: 20,
          value: gain.clamp(0, 100),
          label: '${gain.round()} %',
          onChanged: (v) => setState(() => _drag = v),
          onChangeEnd: (v) {
            setState(() => _drag = null);
            simpleSaveGuider(
              context,
              ref,
              (p) => p.setGuideCameraGain(v.round()),
              live: true,
            );
          },
        ),
      ],
    );
  }
}
