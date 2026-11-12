import 'dart:async';
import 'dart:math' as math;

import 'package:clock/clock.dart';
import 'package:flutter/cupertino.dart' show CupertinoIcons;
import 'package:flutter/material.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/scheduler.dart' show Ticker;
import 'package:flutter/services.dart' show HapticFeedback, LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/equipment_device_status.dart';
import '../../services/equipment_driver_api.dart';
import '../../models/filter_wheel_status.dart';
import '../../models/focuser_status.dart';
import '../../models/switch_device.dart';
import '../../models/weather_status.dart';
import '../../widgets/equipment/cooling_fan_picker.dart';
import '../../state/equipment/camera_state.dart';
import '../../models/guider_status.dart';
import '../../models/mount_status.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/equipment/filter_wheel_command_state.dart';
import '../../state/equipment/dome_state.dart';
import '../../state/equipment/dome_shutter_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/equipment/rotator_state.dart';
import '../../state/equipment/safety_monitor_state.dart';
import '../../state/equipment/switch_state.dart';
import '../../state/equipment/weather_state.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/settings/cooling_fan_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/imaging/main_camera_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/app_locale_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/tonight_sky_local.dart' show moonPhase;
import '../../widgets/equipment/equipment_status_chip.dart'
    show switchChipLevel;
import '../../widgets/equipment/switch_port_layout.dart'
    show
        SwitchPortKind,
        classifySwitchPort,
        findModePort,
        isAutoRegulating,
        isDewModePort,
        snapToSwitchStep,
        switchPortUnit,
        switchSliderDivisions,
        usableSwitchStep;
import '../../widgets/equipment/equipment_glyphs.dart' show EquipmentGlyph;
import '../../widgets/status_indicator.dart' show StatusBlink, StatusDot;
import 'simple_preview_panel.dart' show SimpleStopSquare, kSimpleText;
import 'simple_shell.dart' show SimpleSegment, SimpleSegments, simpleRailScale;
import 'simple_mount_controls.dart';
import '../../util/tracking_rate_words.dart';
import '../../util/friendly_error.dart';

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

/// A focus offset in steps as Simple Mode shows it: signed, a real minus.
String simpleSigned(int steps) =>
    steps > 0 ? '+$steps' : '$steps'.replaceFirst('-', '−');

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
      if (caps != null && caps.hasCooler) {
        children.addAll([
          const Divider(height: AraSpace.s16),
          const ProfileCoolingFanPicker(style: kSimpleText),
        ]);
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
          detail: failureReason(l10n, e),
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
        detail: failureReason(l10n, e),
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
  // What it changes: read with Less and More on the buttons.
  String? name,
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
      icon: Icon(
        icon,
        semanticLabel: name == null ? label(context.l10n) : '$name, ${label(context.l10n)}',
      ),
    ),
  );
  return DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.container),
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
/// label goes over the control. [labelInControl]: the control says the name
/// to a screen reader, so the label isn't read a second time.
Widget simpleWindowRow(
  String label,
  Widget value, {
  required String id,
  bool labelInControl = false,
}) => LayoutBuilder(
  key: Key('simple-window-row-$id'),
  builder: (context, box) => box.maxWidth < kSimpleWindowControlWidth + 96
      ? Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: AraSpace.s4,
          children: [
            ExcludeSemantics(
              excluding: labelInControl,
              child: Text(label, style: kSimpleText),
            ),
            value,
          ],
        )
      : ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 44),
          child: Row(
            children: [
              Expanded(
                child: ExcludeSemantics(
                  excluding: labelInControl,
                  child: Text(label, style: kSimpleText),
                ),
              ),
              value,
            ],
          ),
        ),
);

/// A slider's level after it ("60 %"), as wide as "100 %", so the slider
/// keeps its width as the level changes.
Widget simpleSliderLevel(BuildContext context, String level, {Key? key}) {
  // The style the Text below draws in, so the measure matches it.
  final style = DefaultTextStyle.of(context).style.merge(kSimpleText);
  final widest = TextPainter(
    text: TextSpan(text: '100 %', style: style),
    textDirection: TextDirection.ltr,
    textScaler: MediaQuery.textScalerOf(context),
  )..layout();
  // Rounded up: a fractional width wrapped "100 %" onto two lines.
  final width = widest.width.ceilToDouble() + 1;
  widest.dispose();
  return SizedBox(
    width: width,
    child: Text(
      level,
      key: key,
      maxLines: 1,
      softWrap: false,
      textAlign: TextAlign.end,
      style: kSimpleText,
    ),
  );
}

/// A window's ask before a move that would disturb a running plan: the
/// question with Cancel and [confirm]. Too narrow for the question beside
/// its buttons (as [simpleWindowRow]), the buttons go under it.
Widget simpleWindowConfirm({
  required String question,
  required String confirm,
  required VoidCallback onCancel,
  required VoidCallback onConfirm,
}) => LayoutBuilder(
  builder: (context, box) {
    final text = Text(
      question,
      style: kSimpleText.copyWith(color: AraColors.accentBusy),
    );
    final buttons = [
      TextButton(onPressed: onCancel, child: Text(context.l10n.deviceCancel)),
      TextButton(onPressed: onConfirm, child: Text(confirm)),
    ];
    return box.maxWidth < kSimpleWindowControlWidth + 96
        ? Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              text,
              Wrap(alignment: WrapAlignment.end, children: buttons),
            ],
          )
        : Row(
            spacing: AraSpace.s8,
            children: [
              Expanded(child: text),
              ...buttons,
            ],
          );
  },
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
    this.fill = false,
    this.known = true,
    this.group,
  });

  final String id;
  final bool on;

  /// What the segments choose, read to a screen reader with each segment's
  /// word and state ("Follows the mount, On, selected").
  final String? group;

  /// False: the state isn't known (a faulted shutter), so neither segment
  /// reads as selected and both take a tap.
  final bool known;
  final ValueChanged<bool> onChanged;
  final (String, String)? labels;
  final (String, String) ids;

  /// False: both segments show the state but take no tap.
  final bool enabled;

  /// True: the two segments share the width they're given.
  final bool fill;

  @override
  Widget build(BuildContext context) => DecoratedBox(
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.container),
    ),
    child: Row(
      mainAxisSize: fill ? MainAxisSize.max : MainAxisSize.min,
      children: [
        for (final (v, key, word) in [
          (true, ids.$1, labels?.$1 ?? context.l10n.deviceOn),
          (false, ids.$2, labels?.$2 ?? context.l10n.deviceOff),
        ])
          Flexible(
            fit: fill ? FlexFit.tight : FlexFit.loose,
            child: SimpleSegment(
              key: Key('$id-$key'),
              label: word,
              group: group,
              selected: known && on == v,
              radius: AraRadius.container,
              onTap: (known && on == v) || !enabled ? null : () => onChanged(v),
            ),
          ),
      ],
    ),
  );
}

/// A titled two-column list of versions, each beside its name (Settings ›
/// Rig, a device's page in Settings and the OpenAstro Ara sheet).
class SimpleVersions extends StatelessWidget {
  const SimpleVersions({
    super.key,
    required this.rows,
    this.title,
    this.titleAction,
  });

  final List<(String, String)> rows;

  /// "Versions" unless given.
  final String? title;

  /// A tall button at the title line's end; its height replaces the gap.
  final Widget? titleAction;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: titleAction == null ? AraSpace.s8 : 0,
    children: [
      Row(
        children: [
          Expanded(
            child: Text(
              title ?? context.l10n.deviceVersions,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
          ?titleAction,
        ],
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

/// Home asked in the Mount window (#1649), from the tap until the mount stops
/// moving, as the Dome window's asks: the window and the status pill read
/// Finding home…. Nothing moving within a few seconds (already home, or a
/// fast mount between two reads) clears it.
class SimpleMountHomeNotifier extends Notifier<bool> {
  bool _moved = false;
  Timer? _still;

  @override
  bool build() {
    ref.onDispose(() => _still?.cancel());
    ref.listen(mountProvider.select((s) => s.value), (_, m) {
      if (!state) return;
      if (m == null || !m.isConnected) return clear();
      if (m.isBusy) {
        _moved = true;
        _still?.cancel();
        _still = null;
      } else if (_moved) {
        clear();
      }
    });
    return false;
  }

  /// Sends the mount home; true when it took the ask (it throws why not).
  Future<bool> home() async {
    state = true;
    _moved = false;
    _still?.cancel();
    _still = Timer(const Duration(seconds: 3), () {
      if (!_moved) clear();
    });
    try {
      final ok = await ref.read(mountProvider.notifier).findHome();
      if (!ok) clear();
      return ok;
    } catch (_) {
      clear();
      rethrow;
    }
  }

  void clear() {
    _still?.cancel();
    _still = null;
    state = false;
  }
}

final simpleMountHomeProvider = NotifierProvider<SimpleMountHomeNotifier, bool>(
  SimpleMountHomeNotifier.new,
);

/// The mount's window: tracking (On / Off) and its speed, Parked / Unparked
/// / Home, the automatic meridian flip (the profile's, so plans flip by it
/// too), and beside them the direction pad, its bottom level with the
/// Pointing row's Check (#1649, #1650); on a narrow window the pad sits under
/// the rows.
class SimpleMountWindow extends ConsumerStatefulWidget {
  const SimpleMountWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the mount's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleMountWindow> createState() => _SimpleMountWindowState();
}

/// Wide enough for the rows and the pad side by side.
const double kSimpleMountWideWidth = 560;

class _SimpleMountWindowState extends ConsumerState<SimpleMountWindow> {
  /// Parked (false: Home) tapped while a plan runs: the window asks first.
  bool? _asking;

  /// Read up front: dispose clears the Check answer, and ref is gone by then.
  late final SimplePointingNotifier _pointing;

  @override
  void initState() {
    super.initState();
    _pointing = ref.read(simplePointingProvider.notifier)..opened();
  }

  @override
  void dispose() {
    // After this frame: a provider can't change while the tree is finalized.
    final pointing = _pointing;
    Future.microtask(pointing.closed);
    super.dispose();
  }

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
      final rates = [
        for (final r in kTrackingRateOrder)
          if (caps?.trackingRates.contains(r) ?? false) r,
      ];
      if (rates.length > 1) {
        final rate = m.trackingRate;
        children.add(
          simpleWindowRow(
            context.l10n.deviceTrackingSpeed,
            id: 'Tracking speed',
            SimpleSegments<String>(
              keyPrefix: 'simple-mount-rate',
              group: context.l10n.deviceTrackingSpeed,
              expand: false,
              shrinkToFit: true,
              options: [
                for (final r in rates)
                  (r, trackingRateWord(context.l10n, r), r.toLowerCase()),
              ],
              selected: rate,
              onPick: m.parked || moving
                  ? null
                  : (r) => _run(
                      context,
                      () => notifier.setTrackingRate(r),
                      failure: context.l10n.deviceCouldntSetTrackingSpeed,
                    ),
            ),
          ),
        );
        if (rate != null && rate != 'Sidereal' && rates.contains(rate)) {
          children.add(
            Text(
              context.l10n.deviceTrackingOffSidereal(
                rate == 'King'
                    ? trackingRateWord(context.l10n, rate)
                    : trackingRateWord(context.l10n, rate).toLowerCase(),
              ),
              key: const Key('simple-mount-rate-caption'),
              style: kSimpleCaption,
            ),
          );
        }
      }
      final homing = ref.watch(simpleMountHomeProvider);
      final canHome = caps?.canFindHome ?? false;
      final position = m.parked ? 'parked' : 'unparked';
      children.add(
        simpleWindowRow(
          // Home is the pad's centre now; the row still says where it is.
          m.atHome && canHome && !homing
              ? '${context.l10n.deviceMountPosition} · ${context.l10n.deviceAtHome}'
              : context.l10n.deviceMountPosition,
          id: 'Position',
          moving || homing
              ? SizedBox(
                  height: 44,
                  child: Center(
                    child: StatusBlink(
                      // The mount moving is working: blinking green, as the
                      // Dome window's Parking… (user, 2026-11-11).
                      on: true,
                      child: Text(
                      homing
                          ? context.l10n.deviceFindingHome
                          : (parking ?? !m.parked)
                          ? context.l10n.deviceParking
                          : context.l10n.deviceUnparking,
                      style: kSimpleText.copyWith(
                        color: AraColors.accentConnected,
                      ),
                    ),
                    ),
                  ),
                )
              : DecoratedBox(
                  decoration: BoxDecoration(
                    color: AraColors.tileFill,
                    borderRadius: BorderRadius.circular(AraRadius.container),
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (final (id, word, enabled, onTap) in [
                        (
                          'parked',
                          context.l10n.deviceParked,
                          caps?.canPark ?? false,
                          () => planRunning
                              ? setState(() => _asking = true)
                              : _park(context, true),
                        ),
                        (
                          'unparked',
                          context.l10n.deviceUnparked,
                          !m.parked || (caps?.canUnpark ?? false),
                          () => _park(context, false),
                        ),
                      ])
                        Flexible(
                          child: SimpleSegment(
                            key: Key('simple-mount-park-$id'),
                            label: word,
                            group: context.l10n.deviceMountPosition,
                            shrinkToFit: true,
                            selected: position == id,
                            radius: AraRadius.container,
                            onTap: position == id || !enabled ? null : onTap,
                          ),
                        ),
                    ],
                  ),
                ),
        ),
      );
      if (_asking case final park?) {
        children.add(
          simpleWindowConfirm(
            question: park
                ? context.l10n.devicePlanRunningParkAnyway
                : context.l10n.devicePlanRunningHomeAnyway,
            confirm: park
                ? context.l10n.deviceParkAnyway
                : context.l10n.deviceHomeAnyway,
            onCancel: () => setState(() => _asking = null),
            onConfirm: () {
              setState(() => _asking = null);
              park ? _park(context, true) : _home(context);
            },
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
      _addPadAndPointing(context, children, m, planRunning: planRunning);
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

  /// Lays the rows built so far (after the header) out with the pad and the
  /// Pointing row: side by side when wide, the pad's bottom level with Check;
  /// the pad under the rows when narrow. Check's answer goes under both.
  void _addPadAndPointing(
    BuildContext context,
    List<Widget> children,
    MountStatus m, {
    required bool planRunning,
  }) {
    final l10n = context.l10n;
    final caps = m.capabilities;
    final scale = simpleRailScale(context);
    final run = ref.watch(sequenceRunStateProvider).value?.state;
    final planImaging = (run?.isActive ?? false) && !run!.isAnyPaused;
    final guiding =
        ref.watch(guiderStatusProvider).value?.stopEndsPhase ?? false;
    final polar = ref.watch(polarAlignLiveProvider).isActive;
    final cameraOn =
        ref.watch(cameraStatusProvider).asData?.value?.isConnected ?? false;
    final speeds = (caps?.canMoveAxis ?? false)
        ? simplePadSpeeds(caps?.padRateBands ?? const [])
        : const <(String, double)>[];
    final pad = speeds.isEmpty
        ? SizedBox(
            width: kSimplePadCell * scale * 3 + AraSpace.s8 * 2,
            child: Text(
              l10n.padNoRates,
              key: const Key('simple-mount-pad-none'),
              textAlign: TextAlign.center,
              style: kSimpleCaption,
            ),
          )
        : SimpleMountPad(
            speeds: speeds,
            // The centre: Home, and Stop while the mount moves on its own
            // (a Home, a GoTo, a park); a pad move stops on letting go.
            moving:
                ref.watch(simpleMountHomeProvider) ||
                (m.isBusy && !ref.watch(simplePadMovingProvider)),
            canHome: caps?.canFindHome ?? false,
            onHome: m.parked || m.isBusy
                ? null
                : () => planRunning
                      ? setState(() => _asking = false)
                      : _home(context),
            blocker: simplePadBlocker(
              l10n,
              m,
              planImaging: planImaging,
              polarAligning: polar,
              guiding: guiding,
              padMoving: ref.watch(simplePadMovingProvider),
            ),
          );
    final checkBusy = !cameraOn
        ? l10n.pointingNoCamera
        : polar
        ? l10n.padPolarRunning
        : ref.watch(mainCameraBusyProvider)
        ? l10n.shellMainCameraBusy
        : null;
    final pointing = SimpleMountPointingRow(mount: m, busy: checkBusy);
    const divider = Divider(height: AraSpace.s12, color: AraColors.border);
    final rows = children.sublist(1);
    children.removeRange(1, children.length);
    children
      ..add(
        LayoutBuilder(
          builder: (context, box) => box.maxWidth >= kSimpleMountWideWidth * scale
              ? Row(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  spacing: AraSpace.s16,
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        spacing: AraSpace.s4,
                        children: [...rows, divider, pointing],
                      ),
                    ),
                    pad,
                  ],
                )
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: AraSpace.s4,
                  children: [
                    ...rows,
                    divider,
                    Center(child: pad),
                    divider,
                    pointing,
                  ],
                ),
        ),
      )
      ..add(
        SimpleMountPointingResult(
          busy: checkBusy,
          idle: !planRunning && !guiding && !m.isBusy && !m.parked,
        ),
      );
  }

  void _park(BuildContext context, bool park, {bool thenTrack = false}) => _run(
    context,
    () => ref
        .read(simpleMountMotionProvider.notifier)
        .park(park, thenTrack: thenTrack),
  );

  void _home(BuildContext context) =>
      _run(context, () => ref.read(simpleMountHomeProvider.notifier).home());

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
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  /// False when the mount refused (a pill says why: [failure], or that it
  /// couldn't move).
  Future<bool> _run(
    BuildContext context,
    Future<bool> Function() action, {
    String? failure,
  }) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.mount);
    final l10n = context.l10n;
    try {
      final performed = await action();
      if (!performed) _sayBusy(ref, l10n);
      return performed;
    } catch (e) {
      notify(
        failure ?? l10n.deviceCouldntMoveMount,
        detail: failureReason(l10n, e),
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
String simpleSteps(int n) =>
    (n < 0 ? '−' : '') +
    n.abs().toString().replaceAllMapped(RegExp(r'\B(?=(\d{3})+$)'), (_) => ' ');

/// The focuser's window: where it is and its temperature on the left, and a
/// coarse and a fine knob on the right, upright like a focuser's knobs seen
/// from the side. Rolling a knob up goes in and down goes out, as the
/// drawtube does; the target shows while rolling and the focuser goes there
/// on letting go.
class SimpleFocuserWindow extends ConsumerStatefulWidget {
  const SimpleFocuserWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the focuser's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleFocuserWindow> createState() =>
      _SimpleFocuserWindowState();
}

/// How far a finger or a scroll rolls a focus knob for one click.
const double kSimpleFocusClick = 14;

class _SimpleFocuserWindowState extends ConsumerState<SimpleFocuserWindow> {
  /// While a knob rolls: where it will send the focuser.
  int? _target;

  /// The last place sent, for "Moving to …".
  int? _sent;

  /// Each knob takes Tab like any control; Up moves it in a click, Down out.
  final _coarseFocus = FocusNode(debugLabel: 'simple-focuser-coarse');
  final _fineFocus = FocusNode(debugLabel: 'simple-focuser-fine');
  FocusNode? _highlighted;

  @override
  void dispose() {
    _coarseFocus.dispose();
    _fineFocus.dispose();
    super.dispose();
  }

  /// One click of each knob: coarse about 1 % of the travel, fine a tenth of
  /// that; 100 and 10 for a focuser with no travel to scale by (a relative
  /// one).
  (int, int) _clicks(FocuserStatus s) {
    final c = s.capabilities;
    if (c == null || !c.absoluteFocuser || c.maxPosition <= c.minPosition) {
      return (100, 10);
    }
    final coarse = math.max(1, ((c.maxPosition - c.minPosition) / 100).round());
    return (coarse, math.max(1, (coarse / 10).round()));
  }

  int _clamp(int v, FocuserStatus s) {
    final c = s.capabilities;
    return c == null || c.maxPosition <= c.minPosition
        ? v
        : v.clamp(c.minPosition, c.maxPosition);
  }

  /// The knobs move nothing while autofocus runs (a touch would spoil its
  /// curve) or while the position is unknown (a roll would jump from 0).
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
        detail: failureReason(l10n, e),
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
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  /// One knob with its name under it ("Coarse"): a slider to
  /// screen readers, Up and Down a click from the keyboard. It sits centred
  /// in a slot [slot] tall (the coarse knob's height), so both knobs' marks
  /// and both captions line up.
  Widget _knob(
    FocuserStatus s,
    String id,
    String name,
    String label,
    int step,
    FocusNode focus,
    Size size,
    double slot,
  ) {
    final pos = s.position ?? 0;
    // Locked while autofocus runs or the position is unknown (see _locked).
    final enabled =
        s.position != null &&
        !ref.watch(autofocusLiveProvider.select((l) => l.run.isRunning));
    return Column(
      mainAxisSize: MainAxisSize.min,
      spacing: AraSpace.s4,
      children: [
        SizedBox(
          height: slot,
          child: Center(
            child: FocusableActionDetector(
              focusNode: focus,
              onShowFocusHighlight: (v) =>
                  setState(() => _highlighted = v ? focus : null),
              // Up goes in, like the knob rolled up.
              shortcuts: const {
                SingleActivator(LogicalKeyboardKey.arrowUp): _DialStep(-1),
                SingleActivator(LogicalKeyboardKey.arrowDown): _DialStep(1),
              },
              actions: {
                _DialStep: CallbackAction<_DialStep>(
                  onInvoke: (i) => _go(s, _clamp(pos + i.dir * step, s)),
                ),
              },
              child: Semantics(
                slider: true,
                enabled: enabled,
                label: label,
                // Increase is the bigger number, out; the knob rolled up and
                // the Up key go in, so the reader says which way it goes.
                hint: context.l10n.deviceKnobHint,
                value: simpleSteps(pos),
                increasedValue: simpleSteps(_clamp(pos + step, s)),
                decreasedValue: simpleSteps(_clamp(pos - step, s)),
                onIncrease: enabled
                    ? () => _go(s, _clamp(pos + step, s))
                    : null,
                onDecrease: enabled
                    ? () => _go(s, _clamp(pos - step, s))
                    : null,
                child: _FocusThumbwheel(
                  key: Key('simple-focuser-$id'),
                  size: size,
                  focused: _highlighted == focus,
                  enabled: enabled,
                  onClicks: (n) {
                    HapticFeedback.selectionClick();
                    setState(
                      () => _target = _clamp((_target ?? pos) + n * step, s),
                    );
                  },
                  onRelease: () {
                    final t = _target;
                    setState(() => _target = null);
                    if (t != null) _go(s, t);
                  },
                  // The system took the finger: nothing moves.
                  onCancel: () => setState(() => _target = null),
                ),
              ),
            ),
          ),
        ),
        Text(name, style: AraText.caption),
      ],
    );
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
      final (coarse, fine) = _clicks(s);
      final l10n = context.l10n;
      final scale = simpleRailScale(context);
      children.addAll([
        Row(
          spacing: AraSpace.s12,
          children: [
            // Where the focuser is, big; where it will go shows under it.
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                spacing: 2,
                children: [
                  Text(l10n.devicePosition, style: AraText.caption),
                  Text(
                    simpleSteps(pos),
                    style: kSimpleReadingLarge,
                  ),
                  // Moving is working: blinking green (DESIGN.md status rule).
                  StatusBlink(
                    on: _target == null && s.isMoving,
                    child: Text(
                      _target != null
                          ? l10n.deviceLetGoToMoveTo(simpleSteps(_target!))
                          : s.isMoving
                          ? (_sent != null
                                ? l10n.deviceMovingTo(simpleSteps(_sent!))
                                : l10n.deviceMoving)
                          : '',
                      style: AraText.caption.copyWith(
                        color: AraColors.accentConnected,
                      ),
                    ),
                  ),
                  if (s.temperature != null) ...[
                    const SizedBox(height: AraSpace.s8),
                    Text(l10n.deviceTemperature, style: AraText.caption),
                    Text(simpleCelsius(s.temperature!), style: kSimpleText),
                  ],
                ],
              ),
            ),
            Column(
              mainAxisSize: MainAxisSize.min,
              spacing: AraSpace.s4,
              children: [
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(
                      Icons.arrow_drop_up,
                      size: 20,
                      color: AraColors.textSecondary,
                    ),
                    Text(l10n.deviceIn, style: AraText.caption),
                  ],
                ),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.end,
                  spacing: AraSpace.s8,
                  children: [
                    _knob(
                      s,
                      'coarse',
                      l10n.deviceCoarse,
                      l10n.deviceCoarseFocus,
                      coarse,
                      _coarseFocus,
                      Size(72 * scale, 180 * scale),
                      180 * scale,
                    ),
                    _knob(
                      s,
                      'fine',
                      l10n.deviceFine,
                      l10n.deviceFineFocus,
                      fine,
                      _fineFocus,
                      Size(56 * scale, 140 * scale),
                      180 * scale,
                    ),
                  ],
                ),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(
                      Icons.arrow_drop_down,
                      size: 20,
                      color: AraColors.textSecondary,
                    ),
                    Text(l10n.deviceOut, style: AraText.caption),
                  ],
                ),
              ],
            ),
          ],
        ),
        const SizedBox(height: AraSpace.s12),
        const _AutofocusButton(),
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

/// A dial drawn like a safe's: the whole face turns under a fixed mark,
/// reporting each [detent] turned (clockwise +1, counter-clockwise −1), then
/// the release. A scroll over it turns it the same way and counts as let go
/// once the scrolling stops. [angle] turns the face to the reading the mark
/// shows; [centre] sits in the middle and takes its own taps. Turned on, it
/// claims the finger at once (no drag slop), so the dial follows from the
/// first touch; turned off, it takes no finger, so a swipe on it scrolls the
/// page. A finger that strays well off the face drops the turn, as a cancel.
/// iOS has no rotary control of its own.
class _Dial extends StatefulWidget {
  const _Dial({
    super.key,
    required this.detent,
    required this.angle,
    required this.painter,
    required this.onDetent,
    required this.onRelease,
    required this.onCancel,
    required this.centre,
    required this.size,
    this.focused = false,
    this.enabled = true,
  });

  /// One click's turn, in radians.
  final double detent;

  /// The face's turn at rest, in radians; the parent moves it a detent at
  /// each click, so the face keeps only the part of a turn not yet a click.
  final double angle;
  final CustomPainter painter;
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
  State<_Dial> createState() => _DialState();
}

class _DialState extends State<_Dial> {
  double get _size => widget.size;

  /// The turn not yet a whole detent, and the finger's last angle.
  double _pending = 0;
  double? _last;

  /// This drag is a trackpad's two-finger pan, with no usable position.
  bool _panned = false;
  double _scrolled = 0;

  /// A scroll counts as let go this long after its last tick.
  static const _scrollRest = Duration(milliseconds: 350);
  Timer? _scrollTimer;

  @override
  void dispose() {
    _scrollTimer?.cancel();
    super.dispose();
  }

  double _at(Offset p) => math.atan2(p.dy - _size / 2, p.dx - _size / 2);

  void _turnBy(double d) {
    if (!widget.enabled) return;
    setState(() => _pending += d);
    final whole = (_pending / widget.detent).truncate();
    if (whole != 0) {
      _pending -= whole * widget.detent;
      widget.onDetent(whole);
    }
  }

  void _turn(Offset p) {
    if (_last == null) return; // dropped: dragged off the face
    // Well off the face (a quarter of it past the rim): drop the turn.
    if ((p - Offset(_size / 2, _size / 2)).distance > _size * 0.75) {
      _cancelled = true;
      _end();
      return;
    }
    final a = _at(p);
    var d = a - (_last ?? a);
    if (d > math.pi) d -= 2 * math.pi;
    if (d < -math.pi) d += 2 * math.pi;
    _last = a;
    _turnBy(d);
  }

  /// Set by a pointer cancel, which reaches the pan as an ordinary end.
  bool _cancelled = false;

  void _end() {
    if (_last == null) return;
    _last = null;
    setState(() => _pending = 0);
    _cancelled ? widget.onCancel() : widget.onRelease();
    _cancelled = false;
  }

  void _scroll(PointerSignalEvent e) {
    if (e is! PointerScrollEvent || !widget.enabled) return;
    // Claimed, so the sheet under the window doesn't scroll as well.
    GestureBinding.instance.pointerSignalResolver.register(e, (e) {
      // One notch, one click, however many pixels the wheel reports; a
      // smooth wheel's small deltas add up to 7 first.
      _scrolled += (e as PointerScrollEvent).scrollDelta.dy;
      if (_scrolled.abs() < 7) return;
      final dir = _scrolled.sign;
      _scrolled = 0;
      _pending = 0;
      _turnBy(dir * widget.detent);
      _scrollTimer?.cancel();
      _scrollTimer = Timer(_scrollRest, () {
        setState(() => _pending = 0);
        widget.onRelease();
      });
    });
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
          onPointerSignal: _scroll,
          child: RawGestureDetector(
            gestures: {
              if (widget.enabled)
                _EagerPan: GestureRecognizerFactoryWithHandlers<_EagerPan>(
                  _EagerPan.new,
                  (r) {
                    r
                      ..dragStartBehavior = DragStartBehavior.down
                      ..onStart = (d) {
                        _panned = d.kind == PointerDeviceKind.trackpad;
                        _last = _panned ? 0 : _at(d.localPosition);
                      }
                      ..onUpdate = (d) {
                        if (_panned) {
                          // Two fingers: 7 pt of travel a degree, down
                          // clockwise, as the wheel.
                          if (_last != null) {
                            _turnBy(d.delta.dy * math.pi / 1260);
                          }
                        } else {
                          _turn(d.localPosition);
                        }
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
                angle: widget.angle + _pending,
                // Painted once and turned, not repainted every frame.
                child: RepaintBoundary(
                  child: CustomPaint(
                    size: Size.square(_size),
                    painter: widget.painter,
                  ),
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
        SizedBox.square(dimension: _size * 0.36, child: widget.centre),
      ],
    ),
  );
}

/// A focus knob seen from the side: an upright knurled cylinder whose ridges
/// roll with the finger, reporting each click ([kSimpleFocusClick] of
/// travel: down +1, out; up −1, in), then the release. A scroll over it
/// rolls it the same way and counts as let go once the scrolling stops.
/// Turned on, it claims the finger at once (no drag slop), so it follows from
/// the first touch; turned off, it takes no finger, so a swipe on it scrolls
/// the page. A finger that strays a knob's width off either side drops the
/// roll, as a cancel.
class _FocusThumbwheel extends StatefulWidget {
  const _FocusThumbwheel({
    super.key,
    required this.size,
    required this.onClicks,
    required this.onRelease,
    required this.onCancel,
    this.focused = false,
    this.enabled = true,
  });

  final Size size;
  final ValueChanged<int> onClicks;
  final VoidCallback onRelease;

  /// A roll the system cut off (a pointer cancel), instead of [onRelease].
  final VoidCallback onCancel;

  /// Off: dimmed, and it rolls nothing.
  final bool enabled;

  /// Keyboard focus: a ring round it.
  final bool focused;

  @override
  State<_FocusThumbwheel> createState() => _FocusThumbwheelState();
}

class _FocusThumbwheelState extends State<_FocusThumbwheel> {
  /// How far the surface has rolled, and the roll not yet a whole click.
  double _rolled = 0;
  double _pending = 0;
  bool _dragging = false;

  /// How far this drag has strayed sideways; past a knob's width off its
  /// side, the roll drops.
  double _sideways = 0;

  /// Set by a pointer cancel, which reaches the drag as an ordinary end.
  bool _cancelled = false;

  /// A scroll counts as let go this long after its last tick.
  static const _scrollRest = Duration(milliseconds: 350);
  Timer? _scrollTimer;
  double _scrolled = 0;

  @override
  void dispose() {
    _scrollTimer?.cancel();
    super.dispose();
  }

  void _roll(double dy) {
    if (!widget.enabled) return;
    setState(() => _rolled += dy);
    _pending += dy;
    final whole = (_pending / kSimpleFocusClick).truncate();
    if (whole != 0) {
      _pending -= whole * kSimpleFocusClick;
      widget.onClicks(whole);
    }
  }

  void _end() {
    if (!_dragging) return;
    _dragging = false;
    _pending = 0;
    _cancelled ? widget.onCancel() : widget.onRelease();
    _cancelled = false;
  }

  void _scroll(PointerSignalEvent e) {
    if (e is! PointerScrollEvent || !widget.enabled) return;
    // Claimed, so the sheet under the window doesn't scroll as well.
    GestureBinding.instance.pointerSignalResolver.register(e, (e) {
      // A wheel notch is tens of pixels: one notch, one click. A smooth
      // wheel's small deltas add up to half a click first.
      _scrolled += (e as PointerScrollEvent).scrollDelta.dy;
      if (_scrolled.abs() >= kSimpleFocusClick / 2) {
        final dir = _scrolled.sign;
        _scrolled = 0;
        _pending = 0;
        _roll(dir * kSimpleFocusClick);
      }
      _scrollTimer?.cancel();
      _scrollTimer = Timer(_scrollRest, () {
        _pending = 0;
        widget.onRelease();
      });
    });
  }

  @override
  Widget build(BuildContext context) => Listener(
    onPointerDown: (_) => _cancelled = false,
    onPointerCancel: (_) => _cancelled = true,
    onPointerSignal: _scroll,
    child: RawGestureDetector(
      gestures: {
        if (widget.enabled)
          _EagerPan: GestureRecognizerFactoryWithHandlers<_EagerPan>(
            _EagerPan.new,
            (r) {
              r
                ..dragStartBehavior = DragStartBehavior.down
                ..onStart = (_) {
                  _dragging = true;
                  _sideways = 0;
                }
                ..onUpdate = (d) {
                  if (!_dragging) return; // dropped: dragged off the knob
                  // Counted from the finger's own travel: a trackpad's
                  // two-finger pan reports no usable position.
                  _sideways += d.delta.dx;
                  if (_sideways.abs() > widget.size.width * 1.5) {
                    _cancelled = true;
                    _end();
                    return;
                  }
                  _roll(d.delta.dy);
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
        child: CustomPaint(
          size: widget.size,
          painter: _ThumbwheelPainter(_rolled, focused: widget.focused),
        ),
      ),
    ),
  );
}

/// The knob's side: a shaded cylinder with ridges round it at a fixed pitch,
/// drawn where they face the viewer, and the fixed marks it rolls past.
class _ThumbwheelPainter extends CustomPainter {
  const _ThumbwheelPainter(this.rolled, {this.focused = false});

  /// How far the surface has rolled, in logical pixels (down positive).
  final double rolled;
  final bool focused;

  @override
  void paint(Canvas canvas, Size size) {
    final body = RRect.fromRectAndRadius(
      Rect.fromLTWH(6, 0, size.width - 12, size.height),
      const Radius.circular(AraRadius.pill),
    );
    canvas.drawRRect(
      body,
      Paint()
        ..shader = const LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [
            AraColors.bgPrimary,
            AraColors.tileFill,
            AraColors.bgPrimary,
          ],
        ).createShader(body.outerRect),
    );
    canvas.drawRRect(
      body,
      Paint()
        ..style = PaintingStyle.stroke
        ..color = focused ? AraColors.accentInfo : AraColors.border
        ..strokeWidth = focused ? 2 : 1,
    );
    final r = size.height / 2 - 6;
    final cy = size.height / 2;
    // A ridge every 1.4 clicks of surface, rolling with the finger.
    final n = (2 * math.pi * r / (kSimpleFocusClick * 1.4)).round();
    final pitch = 2 * math.pi / n;
    final turned = -rolled / r;
    for (var i = 0; i < n; i++) {
      final a = turned + i * pitch;
      final facing = math.cos(a);
      if (facing <= 0.05) continue;
      final y = cy - math.sin(a) * r;
      canvas.drawLine(
        Offset(14, y),
        Offset(size.width - 14, y),
        Paint()
          ..color = AraColors.textPrimary.withValues(
            alpha: 0.08 + 0.32 * facing,
          )
          ..strokeWidth = 1 + 2.2 * facing,
      );
    }
    // The fixed marks either side that the ridges roll past.
    final mark = Paint()..color = AraColors.accentInfo;
    canvas.drawPath(
      Path()
        ..moveTo(0, cy - 6)
        ..lineTo(7, cy)
        ..lineTo(0, cy + 6),
      mark,
    );
    canvas.drawPath(
      Path()
        ..moveTo(size.width, cy - 6)
        ..lineTo(size.width - 7, cy)
        ..lineTo(size.width, cy + 6),
      mark,
    );
  }

  @override
  bool shouldRepaint(_ThumbwheelPainter old) =>
      old.rolled != rolled || old.focused != focused;
}

/// One keyboard step of a knob or dial (+1 or −1).
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

  /// A two-finger trackpad scroll (macOS sends it as a pan, not a wheel
  /// signal) rolls the control the same way, ahead of the page's scroll.
  @override
  void addAllowedPointerPanZoom(PointerPanZoomStartEvent event) {
    super.addAllowedPointerPanZoom(event);
    resolve(GestureDisposition.accepted);
  }
}

/// The rotator dial's face: a knurled rim, a tick every 5°, and a long one
/// with its number every 30°, running clockwise from 0 at the top. The
/// numbers follow the user's text size, as the dial itself does.
class _AngleScalePainter extends CustomPainter {
  const _AngleScalePainter(this.textScaler);

  final TextScaler textScaler;

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
    for (var deg = 0; deg < 360; deg += 5) {
      final a = (deg - 90) * math.pi / 180;
      final dir = Offset(math.cos(a), math.sin(a));
      final major = deg % 30 == 0;
      canvas.drawLine(
        c + dir * (r - (major ? 22 : 15)),
        c + dir * (r - 9),
        Paint()
          ..color = major ? AraColors.textPrimary : AraColors.textSecondary
          ..strokeWidth = major ? 2.5 : 1.2,
      );
      if (!major) continue;
      final label = TextPainter(
        text: TextSpan(
          text: '$deg',
          style: AraText.caption.copyWith(
            color: AraColors.textPrimary,
            fontWeight: FontWeight.w600,
          ),
        ),
        textDirection: TextDirection.ltr,
        textScaler: textScaler,
      )..layout();
      label.paint(
        canvas,
        c + dir * (r - 32) - Offset(label.width / 2, label.height / 2),
      );
      label.dispose();
    }
  }

  @override
  bool shouldRepaint(_AngleScalePainter old) => old.textScaler != textScaler;
}

/// Autofocus under the knobs, full width: green to start, red with Cancel
/// autofocus while it runs (user, 2026-11-07). Dark words on the green and
/// white on Emergency Stop's red (5:1; Fault Red gives 4.2:1).
class _AutofocusButton extends ConsumerWidget {
  const _AutofocusButton();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(autofocusLiveProvider);
    final running = live.run.isRunning;
    final af = ref.read(autofocusLiveProvider.notifier);
    final label = running
        ? context.l10n.deviceCancelAutofocus
        : context.l10n.deviceAutofocus;
    final fg = running ? AraColors.selectionFg : AraColors.bgPrimary;
    return Material(
      color: running ? AraColors.emergencyStop : AraColors.accentConnected,
      borderRadius: BorderRadius.circular(AraRadius.simplePanel),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: const Key('simple-focuser-autofocus'),
        onTap: live.busy ? null : (running ? af.cancel : af.start),
        child: Semantics(
          button: true,
          enabled: !live.busy,
          label: label,
          excludeSemantics: true,
          child: Container(
            constraints: const BoxConstraints(minHeight: 48),
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.center,
              spacing: AraSpace.s8,
              children: [
                if (!running)
                  Icon(Icons.center_focus_strong, size: 22, color: fg),
                Flexible(
                  child: Text(
                    label,
                    style: kSimpleText.copyWith(
                      fontWeight: FontWeight.w600,
                      color: fg,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The guide camera's window, titled with its camera like Camera's: its
/// connection and gain. The guider's own settings are in the Guider panel.
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
      detail: failureReason(l10n, e),
      error: true,
    );
  }
}

/// The Guider panel's slider row: label, slider and value on one line, so the
/// panel still fits an iPad without scrolling. [slider] carries the name and
/// value for screen readers, so the row's own text is left out of them.
Widget simpleSliderRow(
  BuildContext context,
  String label,
  Widget slider,
  String value,
) {
  final scale = MediaQuery.textScalerOf(context).scale;
  return Row(
    children: [
      ExcludeSemantics(
        child: SizedBox(
          width: scale(44),
          child: Text(label, style: kSimpleText, maxLines: 1),
        ),
      ),
      Expanded(child: SizedBox(height: 44, child: slider)),
      ExcludeSemantics(
        child: SizedBox(
          width: scale(48),
          child: Text(
            value,
            style: kSimpleText,
            textAlign: TextAlign.end,
            maxLines: 1,
          ),
        ),
      ),
    ],
  );
}

/// The guide camera's gain: the guider's own scale, percent of the camera's
/// range, set when the thumb is let go (Guide mode and the Guide Camera
/// window share it).
class SimpleGuideGainSlider extends ConsumerStatefulWidget {
  const SimpleGuideGainSlider({super.key, this.oneLine = false});

  /// Label, slider and value on one line, for the Guider panel's height.
  final bool oneLine;

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
    // Merged: Slider's own node carries the value as its label, so a plain
    // Semantics parent would leave VoiceOver's adjustable node unnamed.
    final slider = MergeSemantics(
      child: Semantics(
        label: context.l10n.deviceGain,
        child: Slider(
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
      ),
    );
    final value = Text('${gain.round()} %', style: kSimpleText);
    if (widget.oneLine) {
      return simpleSliderRow(
        context,
        context.l10n.deviceGain,
        slider,
        '${gain.round()} %',
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        simpleWindowRow(context.l10n.deviceGain, id: 'Gain', value),
        slider,
      ],
    );
  }
}

/// A rotator angle as the window shows it: whole degrees, as the dial sets
/// them (user, 2026-11-07; the full UI keeps the decimals), 359.6 reading 0°.
String simpleDegrees(double a) => '${a.round() % 360}°';

/// The rotator's window: the sky angle big on the left (the mechanical angle
/// under it when they differ) and a dial on the right whose face is the sky
/// angle. The number under its mark is where the rotator is or, once turned,
/// where it will go: each click is a degree, and the target waits for the
/// dial's middle, Start (move there), which turns into Stop while the rotator
/// turns, or for Sync under the dial (tell the rotator it is there, without
/// moving; user, 2026-11-07). Reverse stays in the full UI.
class SimpleRotatorWindow extends ConsumerStatefulWidget {
  const SimpleRotatorWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the rotator's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleRotatorWindow> createState() =>
      _SimpleRotatorWindowState();
}

class _SimpleRotatorWindowState extends ConsumerState<SimpleRotatorWindow> {
  /// Where the dial was turned to, waiting for Rotate or Sync.
  double? _target;

  /// The last angle sent, for "Moving to …"; cleared once it stops.
  double? _sent;

  /// A Rotate while a plan runs: the window asks first.
  double? _asking;

  /// A Sync while a plan runs asks first too: it shifts the sky angle the
  /// plan frames by (#1670 re-audit). Holds the angle asked about, as
  /// [_asking] does, so turning the dial meanwhile cannot change what syncs.
  double? _askingSync;

  final _dialFocus = FocusNode(debugLabel: 'simple-rotator-dial');
  bool _dialFocused = false;

  @override
  void dispose() {
    _dialFocus.dispose();
    super.dispose();
  }

  static double _norm(double a) => (a % 360 + 360) % 360;

  static bool _same(double a, double b) => (_norm(a - b) + 0.05) % 360 < 0.1;

  /// [n] whole degrees from [from]: the first is the next whole degree that
  /// way (92.4: up 93, down 92).
  static double _snap(double from, int n) {
    final first = n > 0 ? (from + 1e-6).floor() : (from - 1e-6).ceil();
    return _norm((first + n).toDouble());
  }

  /// The signed shortest turn from [from] to [to], in (-180, 180].
  static double _delta(double from, double to) {
    final d = _norm(to - from);
    return d > 180 ? d - 360 : d;
  }

  /// Moves the target [n] degrees, the first click from the angle shown
  /// (92.4 reads 92°: down 91, up 93). Back to or across the rotator's angle
  /// there is nothing to do, so the target clears; across the far side, 180°
  /// away, it keeps counting.
  void _nudge(double sky, int n) {
    // One pan update can report several detents; each one is a click, so a
    // run that passes through the rotator's angle clears the target there.
    for (var i = 0; i < n.abs(); i++) {
      _step(sky, n.sign);
    }
  }

  void _step(double sky, int n) {
    final shown = sky.roundToDouble();
    final old = _target;
    final t = _snap(old ?? shown, n);
    final before = old == null ? 0.0 : _delta(sky, old);
    final after = _delta(sky, t);
    final crossed =
        old != null &&
        before.sign != after.sign &&
        before.abs() < 90 &&
        after.abs() < 90;
    setState(
      () => _target = crossed || _same(t, sky) || _same(t, shown) ? null : t,
    );
  }

  /// Rotate: a move while a plan runs asks first.
  void _rotate(bool planRunning) {
    final t = _target;
    if (t == null) return;
    if (planRunning) {
      setState(() => _asking = t);
    } else {
      setState(() => _target = null);
      _go(t);
    }
  }

  Future<void> _sync([double? asked]) async {
    final t = asked ?? _target;
    if (t == null) return;
    setState(() => _target = null);
    final notify = userNotifier(context, origin: SimpleStatusOrigin.rotator);
    final l10n = context.l10n;
    try {
      final performed = await ref.read(rotatorProvider.notifier).sync(t);
      if (!performed) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntSyncRotator,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  Future<void> _go(double target) async {
    setState(() => _sent = target);
    final notify = userNotifier(context, origin: SimpleStatusOrigin.rotator);
    final l10n = context.l10n;
    try {
      final performed = await ref
          .read(rotatorProvider.notifier)
          .move(target, useSkyAngle: true);
      if (!performed) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntMoveRotator,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  Future<void> _halt() async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.rotator);
    final l10n = context.l10n;
    try {
      await ref.read(rotatorProvider.notifier).halt();
    } catch (e) {
      notify(
        l10n.deviceCouldntStopRotator,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(rotatorProvider, (prev, next) {
      if ((prev?.value?.isMoving ?? false) &&
          !(next.value?.isMoving ?? false)) {
        setState(() => _sent = null);
      }
    });
    final l10n = context.l10n;
    final s = ref.watch(rotatorProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellRotator, status: s),
    ];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      // Watched, so it's known by the time the dial is let go.
      final planRunning =
          ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
      final sky = s.skyAngleDeg;
      final mech = s.mechanicalAngleDeg;
      final enabled = sky != null && !s.isMoving;
      final shown = _target ?? sky ?? 0;
      final scale = simpleRailScale(context);
      children.add(
        LayoutBuilder(
          // 180 pt, more with large text; never more than about half a narrow
          // window, so the reading keeps its room.
          builder: (context, box) {
            final dial = math.min(180 * scale, box.maxWidth * 0.55);
            return Row(
              spacing: AraSpace.s12,
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    spacing: 2,
                    children: [
                      Text(l10n.deviceSkyAngle, style: AraText.caption),
                      // Shrinks in a narrow window rather than wrap.
                      FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: AlignmentDirectional.centerStart,
                        child: Text(
                          sky == null ? '—' : simpleDegrees(sky),
                          maxLines: 1,
                          style: kSimpleReadingLarge,
                        ),
                      ),
                      // Moving is working: blinking green (DESIGN.md status rule).
                      StatusBlink(
                        on: s.isMoving,
                        child: Text(
                          s.isMoving
                              ? (_sent != null
                                    ? l10n.deviceMovingTo(simpleDegrees(_sent!))
                                    : l10n.deviceMoving)
                              : '',
                          style: AraText.caption.copyWith(
                            color: AraColors.accentConnected,
                          ),
                        ),
                      ),
                      if (sky != null &&
                          mech != null &&
                          simpleDegrees(sky) != simpleDegrees(mech)) ...[
                        const SizedBox(height: AraSpace.s8),
                        Text(
                          l10n.deviceMechanicalAngle,
                          style: AraText.caption,
                        ),
                        Text(simpleDegrees(mech), style: kSimpleText),
                      ],
                    ],
                  ),
                ),
                FocusableActionDetector(
                  focusNode: _dialFocus,
                  onShowFocusHighlight: (v) => setState(() => _dialFocused = v),
                  // Up or right a degree up, as the screen reader's increase.
                  shortcuts: const {
                    SingleActivator(LogicalKeyboardKey.arrowUp): _DialStep(1),
                    SingleActivator(LogicalKeyboardKey.arrowRight): _DialStep(
                      1,
                    ),
                    SingleActivator(LogicalKeyboardKey.arrowDown): _DialStep(
                      -1,
                    ),
                    SingleActivator(LogicalKeyboardKey.arrowLeft): _DialStep(
                      -1,
                    ),
                  },
                  actions: {
                    _DialStep: CallbackAction<_DialStep>(
                      onInvoke: (i) => enabled ? _nudge(sky, i.dir) : null,
                    ),
                  },
                  child: Semantics(
                    slider: true,
                    label: l10n.deviceSkyAngle,
                    // The dial reads what is under its mark: the target once
                    // turned, else where the rotator is.
                    value: sky == null ? null : simpleDegrees(shown),
                    increasedValue: sky == null
                        ? null
                        : simpleDegrees(
                            _snap(_target ?? sky.roundToDouble(), 1),
                          ),
                    decreasedValue: sky == null
                        ? null
                        : simpleDegrees(
                            _snap(_target ?? sky.roundToDouble(), -1),
                          ),
                    enabled: enabled,
                    onIncrease: enabled ? () => _nudge(sky, 1) : null,
                    onDecrease: enabled ? () => _nudge(sky, -1) : null,
                    child: _Dial(
                      key: const Key('simple-rotator-dial'),
                      size: dial,
                      detent: math.pi / 180,
                      // The reading sits under the mark at the top.
                      angle: -shown * math.pi / 180,
                      painter: _AngleScalePainter(
                        MediaQuery.textScalerOf(context),
                      ),
                      focused: _dialFocused,
                      enabled: enabled,
                      // Clockwise brings lower numbers under the mark.
                      onDetent: (d) {
                        HapticFeedback.selectionClick();
                        _nudge(sky!, -d);
                      },
                      // The target waits for Rotate or Sync.
                      onRelease: () {},
                      // The system took the finger: the target drops.
                      onCancel: () => setState(() => _target = null),
                      centre: _RotatorHub(
                        moving: s.isMoving,
                        label: s.isMoving
                            ? l10n.deviceStopRotator
                            : _target == null
                            ? l10n.deviceRotate
                            : l10n.deviceRotateTo(simpleDegrees(_target!)),
                        onTap: s.isMoving
                            ? _halt
                            : enabled && _target != null
                            ? () => _rotate(planRunning)
                            : null,
                      ),
                    ),
                  ),
                ),
              ],
            );
          },
        ),
      );
      if (_asking != null) {
        final target = _asking!;
        children.add(
          simpleWindowConfirm(
            question: l10n.devicePlanRunningRotate,
            confirm: l10n.deviceRotateAnyway,
            onCancel: () => setState(() => _asking = null),
            onConfirm: () {
              setState(() {
                _asking = null;
                _target = null; // spent, as a plain Start
              });
              _go(target);
            },
          ),
        );
      }
      if (_askingSync != null) {
        final asked = _askingSync!;
        children.add(
          simpleWindowConfirm(
            question: l10n.devicePlanRunningSync,
            confirm: l10n.deviceSyncAnyway,
            onCancel: () => setState(() => _askingSync = null),
            onConfirm: () {
              setState(() => _askingSync = null);
              _sync(asked);
            },
          ),
        );
      }
      final target = _target == null ? null : simpleDegrees(_target!);
      children.addAll([
        const SizedBox(height: AraSpace.s12),
        SimpleWindowButton(
          key: const Key('simple-rotator-sync'),
          label: target == null ? l10n.deviceSync : l10n.deviceSyncTo(target),
          onTap: enabled && _target != null
              ? () => planRunning
                    ? setState(() => _askingSync = _target)
                    : _sync()
              : null,
        ),
      ]);
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(context, ref.watch(deviceDriverProvider('rotator')).value),
      );
    }
    return Column(
      key: const Key('simple-device-rotator'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }
}

/// A window's plain button: Sync under the rotator's dial, full width,
/// carrying the target ("Sync to 75°"; dimmed and taking no tap with no
/// target), the Dome's Park and Home, the Mount window's Check, Center and Sync.
class SimpleWindowButton extends StatelessWidget {
  const SimpleWindowButton({
    super.key,
    required this.label,
    required this.onTap,
    this.outlined = false,
  });

  final String label;
  final VoidCallback? onTap;

  /// The second of a pair (Delete beside Rebuild): a Hairline outline, no fill.
  final bool outlined;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    enabled: onTap != null,
    label: label,
    excludeSemantics: true,
    child: Opacity(
      opacity: onTap == null ? 0.4 : 1,
      child: Material(
        color: outlined ? Colors.transparent : AraColors.tileFill,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          side: outlined
              ? const BorderSide(color: AraColors.border)
              : BorderSide.none,
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Container(
            constraints: const BoxConstraints(minHeight: 48),
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
            alignment: Alignment.center,
            child: Text(
              label,
              textAlign: TextAlign.center,
              style: kSimpleText.copyWith(
                fontWeight: FontWeight.w600,
                color: AraColors.textPrimary,
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

/// The rotator dial's middle, like the shutter: Start (a play triangle,
/// green with a target to go to, grey and taking no tap without one), which
/// turns into Stop (the red stop square) while the rotator turns.
class _RotatorHub extends StatelessWidget {
  const _RotatorHub({
    required this.moving,
    required this.label,
    required this.onTap,
    this.inkKey = const Key('simple-rotator-hub'),
  });

  final bool moving;
  final String label;
  final VoidCallback? onTap;

  /// The tap target's Key (the Dome window's dial has one too).
  final Key inkKey;

  @override
  Widget build(BuildContext context) => Semantics(
    button: true,
    enabled: onTap != null,
    label: label,
    excludeSemantics: true,
    child: Material(
      color: AraColors.bgPrimary,
      shape: const CircleBorder(side: BorderSide(color: AraColors.border)),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        key: inkKey,
        onTap: onTap,
        child: Center(
          child: moving
              ? const SimpleStopSquare()
              : Icon(
                  Icons.play_arrow,
                  size: 40,
                  color: onTap == null
                      ? AraColors.textDisabled
                      : AraColors.accentConnected,
                ),
        ),
      ),
    ),
  );
}

/// The filter wheel's window: the filter in place, with its saved focus
/// offset once any slot has one (#1675), and a tap on a filter moves to it. A
/// manual wheel asks the user to swap it by hand and confirm. Names and labels
/// stay in the full UI.
class SimpleFilterWheelWindow extends ConsumerStatefulWidget {
  const SimpleFilterWheelWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the wheel's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleFilterWheelWindow> createState() =>
      _SimpleFilterWheelWindowState();
}

class _SimpleFilterWheelWindowState
    extends ConsumerState<SimpleFilterWheelWindow> {
  String _name(FilterSlot slot) =>
      slot.name.isEmpty ? context.l10n.deviceSlot(slot.position) : slot.name;

  /// A filter's segment: its position first, as the full UI's list shows
  /// it, so the slot is known before the tap.
  String _labelled(FilterSlot slot) => slot.name.isEmpty
      ? context.l10n.deviceSlot(slot.position)
      : context.l10n.deviceSlotFilter(slot.position, slot.name);

  /// The filter in place with its position, as the full UI shows it.
  String _placed(FilterSlot slot) => slot.name.isEmpty
      ? context.l10n.deviceSlot(slot.position)
      : context.l10n.deviceFilterInSlot(slot.name, slot.position);

  @override
  Widget build(BuildContext context) {
    // The change itself is the shared device command's (ADR 0013); a refused
    // one says why in a pill.
    ref.listen(filterWheelCommandProvider, (prev, next) {
      if (next.outcomes == prev?.outcomes || next.by != this) return;
      final l10n = context.l10n;
      switch (next.refusal) {
        case DeviceCommandRefusal.busy:
          _sayBusy(ref, l10n);
        case DeviceCommandRefusal.failed:
          userNotifier(context, origin: SimpleStatusOrigin.filterWheel)(
            l10n.deviceCouldntChangeFilter,
            detail: failureReason(context.l10n, next.error!),
            error: true,
          );
        case DeviceCommandRefusal.didntMove:
          userNotifier(context, origin: SimpleStatusOrigin.filterWheel)(
            l10n.filterDropdownDidntMove,
            error: true,
          );
        case null:
          break;
      }
    });
    final command = ref.watch(filterWheelCommandProvider);
    final l10n = context.l10n;
    final w = ref.watch(filterWheelProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellFilterWheel, status: w),
    ];
    if (w == null || !w.isConnected) {
      children.add(
        Text(
          w?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      final sentSlot = command.phase == DeviceCommandPhase.moving
          ? command.target
          : null;
      final sent = sentSlot == null
          ? null
          : w.slots.where((s) => s.position == sentSlot).firstOrNull;
      final moving = sent != null || w.isMoving;
      final pending = w.isAwaitingUser ? w.pending : null;
      children.add(
        simpleWindowRow(
          l10n.deviceFilter,
          id: 'Filter',
          // Moving is working: blinking green (DESIGN.md status rule).
          StatusBlink(
            on: moving,
            child: Text(
              sent != null
                  ? l10n.deviceMovingTo(_name(sent))
                  : w.isMoving
                  ? l10n.deviceMoving
                  : w.current == null
                  ? '—'
                  : _placed(w.current!),
              style: kSimpleText.copyWith(
                color: moving ? AraColors.accentConnected : null,
              ),
            ),
          ),
        ),
      );
      // The saved offset of the filter in place, once any slot has one.
      if (!moving &&
          w.current != null &&
          w.slots.any((s) => s.focusOffset != 0)) {
        children.add(
          Text(
            l10n.deviceFocusOffset(simpleSigned(w.current!.focusOffset)),
            key: const Key('simple-filterwheel-offset'),
            style: AraText.caption,
          ),
        );
      }
      if (command.phase == DeviceCommandPhase.asking && command.by == this) {
        final cmd = ref.read(filterWheelCommandProvider.notifier);
        children.add(
          simpleWindowConfirm(
            question: l10n.devicePlanRunningChangeFilter,
            confirm: l10n.deviceChangeAnyway,
            onCancel: cmd.cancel,
            onConfirm: cmd.confirm,
          ),
        );
      }
      if (pending != null) {
        children.addAll([
          Text(
            l10n.deviceSwapIn(_name(pending)),
            style: kSimpleText.copyWith(color: AraColors.accentBusy),
          ),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            spacing: AraSpace.s8,
            children: [
              TextButton(
                onPressed: () => _run((n) => n.cancelManualSwap()),
                child: Text(l10n.deviceCancelSwap),
              ),
              TextButton(
                onPressed: () =>
                    _run((n) => n.reportInstalled(pending.position)),
                child: Text(l10n.deviceFilterIsIn(_name(pending))),
              ),
            ],
          ),
        ]);
      }
      children.add(
        w.slots.isEmpty
            ? Text(l10n.deviceNoFilters, style: AraText.caption)
            : LayoutBuilder(
                builder: (context, box) {
                  // Full-width rows; the labels left-justified and centred as
                  // a block, so the slot numbers line up.
                  final scaler = MediaQuery.textScalerOf(context);
                  final widest = w.slots
                      .map((slot) {
                        final painter = TextPainter(
                          text: TextSpan(
                            text: _labelled(slot),
                            style: kSimpleText,
                          ),
                          textScaler: scaler,
                          maxLines: 1,
                          textDirection: Directionality.of(context),
                        )..layout();
                        final width = painter.width;
                        painter.dispose();
                        return width;
                      })
                      .reduce(math.max);
                  final inset = math.max(14.0, (box.maxWidth - widest) / 2);
                  return DecoratedBox(
                    decoration: BoxDecoration(
                      color: AraColors.tileFill,
                      borderRadius: BorderRadius.circular(AraRadius.container),
                    ),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        for (final slot in w.slots)
                          SimpleSegment(
                            key: Key(
                              'simple-filterwheel-slot-${slot.position}',
                            ),
                            alignment: Alignment.centerLeft,
                            padding: EdgeInsetsDirectional.only(
                              start: inset,
                              end: 14,
                            ),
                            label: _labelled(slot),
                            selected:
                                (sentSlot ?? w.currentSlot) == slot.position,
                            radius: AraRadius.container,
                            onTap:
                                moving ||
                                    pending != null ||
                                    slot.position == w.currentSlot
                                ? null
                                : () => ref
                                      .read(filterWheelCommandProvider.notifier)
                                      .request(slot.position, by: this),
                          ),
                      ],
                    ),
                  );
                },
              ),
      );
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(
          context,
          ref.watch(deviceDriverProvider('filterwheel')).value,
        ),
      );
    }
    return Column(
      key: const Key('simple-device-filterwheel'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  /// False when the wheel refused or dropped it (a pill says why).
  Future<bool> _run(Future<bool> Function(FilterWheelNotifier) action) async {
    final notify = userNotifier(
      context,
      origin: SimpleStatusOrigin.filterWheel,
    );
    final l10n = context.l10n;
    try {
      final performed = await action(ref.read(filterWheelProvider.notifier));
      if (!performed) _sayBusy(ref, l10n);
      return performed;
    } catch (e) {
      notify(
        l10n.deviceCouldntChangeFilter,
        detail: failureReason(l10n, e),
        error: true,
      );
      return false;
    }
  }
}

/// A switch port's level or reading as Simple Mode shows it: no ".0", at
/// most 2 places.
String _simpleLevel(double v) {
  final r = (v * 100).roundToDouble() / 100;
  return r == r.roundToDouble()
      ? r.toStringAsFixed(0)
      : r.toStringAsFixed(2).replaceFirst(RegExp(r'0$'), '');
}

/// A slider port's level as a percent of its range ("50 %"); anything off
/// the bottom reads at least 1 % and anything short of the top at most 99 %,
/// so only the ends read 0 % and 100 %. The rig is still sent its own value.
String _switchPercent(SwitchPort p, double v) {
  final pct = v <= p.min
      ? 0
      : v >= p.max
      ? 100
      : ((v - p.min) * 100 / (p.max - p.min)).round().clamp(1, 99);
  return '$pct %';
}

/// A port the rig can't set, as Settings › Switches lists it: On / Off for an
/// on/off reading, else its value and the unit its name gives (#1683).
String simpleSwitchReading(AppLocalizations l10n, SwitchPort p) {
  if (p.isBoolean) return p.value >= 0.5 ? l10n.deviceOn : l10n.deviceOff;
  // The unit the full UI infers from the name; none when it recognises none.
  final unit = switchPortUnit(p);
  return unit.isEmpty
      ? _simpleLevel(p.value)
      : '${_simpleLevel(p.value)} $unit';
}

/// The port a Switches window write is on its way to, for the status pill
/// (#1668); null when none.
class SimpleSwitchSettingNotifier extends Notifier<String?> {
  @override
  String? build() => null;

  void set(String? port) => state = port;
}

final simpleSwitchSettingProvider =
    NotifierProvider<SimpleSwitchSettingNotifier, String?>(
      SimpleSwitchSettingNotifier.new,
    );

/// The switch devices' window (#1651): each connected device's ports by
/// name, as On / Off or a level slider. In Settings ([inSettings]) each device
/// also lists its readings (ports the rig can't set) under Sensors, and its
/// versions, like any device. The camera's cooling fan, which the rig runs
/// with the cooler, is left out.
class SimpleSwitchesWindow extends ConsumerStatefulWidget {
  const SimpleSwitchesWindow({super.key, this.inSettings = false});

  final bool inSettings;

  @override
  ConsumerState<SimpleSwitchesWindow> createState() =>
      _SimpleSwitchesWindowState();
}

class _SimpleSwitchesWindowState extends ConsumerState<SimpleSwitchesWindow> {
  /// The port written ("device-port") and the value asked, until the rig
  /// answers: the control shows it meanwhile.
  ({String port, double value})? _asked;

  /// A slider being dragged, and where.
  ({String port, double value})? _drag;

  /// Locked for longer than [_dimAfter]: the other ports dim. A rig that
  /// answers sooner never dims them, so a write doesn't flash the window.
  bool _dim = false;
  Timer? _dimTimer;
  static const _dimAfter = Duration(milliseconds: 300);

  /// Called whenever the lock may have changed (a write asked or answered,
  /// the notifier busy or free), never from build.
  void _relock() {
    if (!mounted) return;
    final locked = ref.read(switchActingProvider) || _asked != null;
    setState(() {
      if (!locked) {
        _dimTimer?.cancel();
        _dimTimer = null;
        _dim = false;
        return;
      }
      // A drag the lock cut short sends nothing: show the rig's value again.
      _drag = null;
      if (_dimTimer == null && !_dim) {
        _dimTimer = Timer(_dimAfter, () {
          _dimTimer = null;
          if (mounted) setState(() => _dim = true);
        });
      }
    });
  }

  /// A dew output whose mode port reads Auto: the controller sets it.
  static bool _autoRegulated(SwitchDevice d, SwitchPort p) =>
      isAutoRegulating(findModePort(p, d.ports));

  @override
  void dispose() {
    _dimTimer?.cancel();
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    // The list has no live updates: read it fresh as the window opens.
    Future.microtask(() => ref.read(switchListProvider.notifier).refresh());
  }

  /// On the device's step (#1680); without one, whole numbers over a range of
  /// 10 or more, else to 0.01.
  static double _round(SwitchPort p, double v) =>
      snapToSwitchStep(p, v) ??
      (p.max - p.min >= 10
          ? (p.min + (v - p.min).roundToDouble()).clamp(p.min, p.max)
          : (v * 100).roundToDouble() / 100);

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final async = ref.watch(switchListProvider);
    final acting = ref.watch(switchActingProvider);
    final fan = ref.watch(coolingFanProvider).value ?? CoolingFan.none;
    final connected = [
      for (final d in async.value ?? const <SwitchDevice>[])
        if (d.isConnected) d,
    ];
    // Only what can be switched: a port the rig can't set is a reading,
    // listed under Sensors in Settings only (user, 2026-11-07).
    final devices = [
      for (final d in connected)
        (
          device: d,
          ports: [
            for (final p in d.ports)
              // A range of one value has nothing to set. A dew output its
              // controller regulates shows, as a readout, even when the
              // driver reports it read-only meanwhile.
              if (!isCoolingFanPort(d, p, fan) &&
                  (p.canWrite && p.max > p.min || _autoRegulated(d, p)))
                p,
          ],
          readings: [
            for (final p in d.ports)
              if (!p.canWrite &&
                  !isCoolingFanPort(d, p, fan) &&
                  !_autoRegulated(d, p))
                p,
          ],
        ),
    ].where((e) => widget.inSettings || e.ports.isNotEmpty).toList();
    final children = <Widget>[
      Row(
        spacing: AraSpace.s8,
        children: [
          StatusDot.of(switchChipLevel(async, acting: acting)),
          Expanded(
            child: Text(
              l10n.shellSwitches,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
          ),
        ],
      ),
    ];
    if (devices.isEmpty) {
      children.add(
        Text(
          connected.isEmpty
              ? l10n.deviceNotConnected
              : l10n.deviceNothingToSwitch,
          style: AraText.caption,
        ),
      );
    }
    ref.listen(switchActingProvider, (_, _) => _relock());
    // The notifier drops a write while another is on its way: lock them all.
    final locked = acting || _asked != null;
    for (final (i, (:device, :ports, :readings)) in devices.indexed) {
      final name = device.name.isEmpty ? l10n.shellSwitches : device.name;
      children.add(
        Column(
          key: Key('simple-switch-device-${device.deviceId}'),
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s4,
          children: [
            Padding(
              // A device after another one's tables gets more room above its
              // title than the tables have between them, so it reads as the
              // next device, not one more table.
              padding: EdgeInsets.only(
                top: i > 0 && widget.inSettings ? AraSpace.s24 : AraSpace.s12,
              ),
              child: Text(
                ports.isEmpty
                    ? name
                    : l10n.deviceSwitchPorts(name, ports.length),
                style: kSimpleText.copyWith(
                  color: AraColors.textSecondary,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ),
            for (final p in ports) _port(device, p, locked),
            if (widget.inSettings && readings.isNotEmpty) ...[
              const Divider(height: AraSpace.s16),
              SimpleVersions(
                title: l10n.deviceSensors,
                rows: [
                  for (final p in readings)
                    (p.label, simpleSwitchReading(l10n, p)),
                ],
              ),
            ],
            if (widget.inSettings)
              ..._versions(
                context,
                ref
                    .watch(
                      deviceDriverProvider(
                        'switch/${Uri.encodeComponent(device.deviceId)}',
                      ),
                    )
                    .value,
              ),
          ],
        ),
      );
    }
    return Column(
      key: const Key('simple-device-switches'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  Widget _port(SwitchDevice d, SwitchPort p, bool locked) {
    final l10n = context.l10n;
    final id = '${d.deviceId}-${p.id}';
    final asked = _asked?.port == id ? _asked!.value : null;
    // Locked past [_dimAfter], the other ports read locked; the asked one
    // stays bright.
    final dimmed = locked && _dim && asked == null;
    final Widget control;
    if (_autoRegulated(d, p)) {
      // The full UI's readout: fighting the regulation helps nobody.
      control = Text(
        l10n.deviceSetAutomatically,
        style: kSimpleText.copyWith(color: AraColors.textSecondary),
      );
    } else if (classifySwitchPort(p) == SwitchPortKind.mode) {
      // A few named states (a dew channel's Auto / Manual / Switch, a fan's
      // speeds) are segments, as the full UI's mode picker, not a level.
      final lo = p.min.round();
      final names = isDewModePort(p)
          ? [l10n.deviceAuto, l10n.deviceModeManual, l10n.deviceModeSwitch]
          : [for (var v = lo; v <= p.max.round(); v++) '$v'];
      final on = (asked ?? p.value).round();
      control = Semantics(
        label: p.label,
        container: true,
        child: Opacity(
          opacity: dimmed ? AraOpacity.locked : 1,
          child: SimpleSegments<int>(
            options: [
              for (final (i, name) in names.indexed)
                (lo + i, name, '${lo + i}'),
            ],
            selected: on,
            onPick: locked ? null : (v) => _write(d, p, v.toDouble()),
            keyPrefix: 'simple-switch-$id',
            expand: false,
          ),
        ),
      );
    } else if (p.isBoolean) {
      control = Semantics(
        label: p.label,
        container: true,
        child: Opacity(
          opacity: dimmed ? AraOpacity.locked : 1,
          child: _OnOff(
            id: 'simple-switch-$id',
            on: (asked ?? p.value) >= 0.5,
            enabled: !locked,
            onChanged: (on) => _write(d, p, on ? 1 : 0),
          ),
        ),
      );
    } else {
      final v = (_drag?.port == id ? _drag!.value : asked ?? p.value).clamp(
        p.min,
        p.max,
      );
      // The level after the slider, as the Flat panel window's brightness:
      // after the name it wrapped "Light Box · 60 %" onto two lines.
      control = SizedBox(
        width: kSimpleWindowControlWidth,
        child: Row(
          children: [
            Expanded(
              child: Opacity(
                opacity: dimmed ? AraOpacity.locked : 1,
                child: IgnorePointer(
                  ignoring: dimmed,
                  child: Semantics(
                    label: p.label,
                    child: Slider(
                      key: Key('simple-switch-$id'),
                      min: p.min,
                      max: p.max,
                      divisions: usableSwitchStep(p) == null
                          ? null
                          : switchSliderDivisions(p),
                      value: v,
                      semanticFormatterCallback: (x) =>
                          _switchPercent(p, _round(p, x)),
                      // Locked: it takes nothing; once dimmed it fades as
                      // the segments do and takes no touch or screen-reader
                      // action, so a fast answer doesn't flash it.
                      onChanged: (x) {
                        if (!locked) {
                          setState(() => _drag = (port: id, value: x));
                        }
                      },
                      onChangeEnd: (x) {
                        if (locked) return;
                        setState(() => _drag = null);
                        _write(d, p, x);
                      },
                    ),
                  ),
                ),
              ),
            ),
            // The slider already reads its level to a screen reader.
            ExcludeSemantics(
              child: Opacity(
                opacity: dimmed ? AraOpacity.locked : 1,
                child: simpleSliderLevel(
                  context,
                  _switchPercent(p, _round(p, v)),
                  key: Key('simple-switch-$id-level'),
                ),
              ),
            ),
          ],
        ),
      );
    }
    return simpleWindowRow(
      p.label,
      control,
      id: 'switch-$id',
      // The readout has no name of its own: the row's label says it.
      labelInControl: !_autoRegulated(d, p),
    );
  }

  Future<void> _write(SwitchDevice d, SwitchPort p, double value) async {
    final v = _round(p, value);
    final l10n = context.l10n;
    final notify = userNotifier(context, origin: SimpleStatusOrigin.switches);
    final setting = ref.read(simpleSwitchSettingProvider.notifier);
    setState(() => _asked = (port: '${d.deviceId}-${p.id}', value: v));
    _relock();
    // Two devices with a port of this name: the pill says which device.
    final shared = (ref.read(switchListProvider).value ?? const []).any(
      (o) =>
          o.deviceId != d.deviceId &&
          o.isConnected &&
          o.ports.any((q) => q.label == p.label),
    );
    setting.set(
      shared
          ? l10n.deviceSwitchPortOnDevice(
              p.label,
              d.name.isEmpty ? l10n.shellSwitches : d.name,
            )
          : p.label,
    );
    try {
      // Re-reads the list once the rig takes it.
      final performed = await ref
          .read(switchListProvider.notifier)
          .setValue(deviceId: d.deviceId, portId: p.id, value: v);
      if (!performed && mounted) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntSetPort(p.label),
        detail: failureReason(l10n, e),
        error: true,
      );
    } finally {
      setting.set(null);
      if (mounted) {
        setState(() => _asked = null);
        _relock();
      }
    }
  }
}

/// The flat panel's window: the cover (Open / Closed), the light (On / Off)
/// and its brightness, with a drawing of the panel face beside them. Only
/// the parts the panel has show. Auto-brightness, auto-flats and the
/// safety policies stay in the full UI.
class SimpleFlatPanelWindow extends ConsumerStatefulWidget {
  const SimpleFlatPanelWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the panel's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleFlatPanelWindow> createState() =>
      _SimpleFlatPanelWindowState();
}

class _SimpleFlatPanelWindowState extends ConsumerState<SimpleFlatPanelWindow> {
  /// The light asked for, while its command is on its way, so the segment
  /// moves under the finger (the light has no moving state to poll).
  bool? _pendingLight;

  /// The brightness under the finger, then sent, until the command lands.
  int? _level;

  /// A command is on its way: a second ask says busy and leaves
  /// [_pendingLight] and [_level] to the first.
  bool _applying = false;

  /// A change asked for while a plan runs: the window asks first.
  ({bool? openCover, bool? lightOn, int? brightness})? _asking;

  /// The panel face's size beside the rows, at most.
  static const double _face = 140;

  /// The narrowest the rows go beside the face (both segments' words whole).
  static const double _rowsMin = 176;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final s = ref.watch(flatPanelProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellFlatPanel, status: s),
    ];
    if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      // Watched, so it's known by the time a control is used.
      final planRunning =
          ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
      final asking = _asking != null;
      void send({bool? openCover, bool? lightOn, int? brightness}) =>
          planRunning
          ? setState(
              () => _asking = (
                openCover: openCover,
                lightOn: lightOn,
                brightness: brightness,
              ),
            )
          : _apply(
              openCover: openCover,
              lightOn: lightOn,
              brightness: brightness,
            );
      final lightOn = _pendingLight ?? s.lightOn;
      final max = s.maxBrightness;
      final level = (_level ?? s.brightness).clamp(0, max);
      // Each row's label over its segments, a state beside the label (blinking
      // green Moving…, blue Warming up…) while the segments stay, locked, so
      // the window keeps its size.
      Widget row(String label, Widget? state, Widget control) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s4,
        children: [
          Row(
            spacing: AraSpace.s8,
            children: [
              Text(label, style: kSimpleText),
              if (state != null) Flexible(child: state),
            ],
          ),
          control,
        ],
      );
      final rows = <Widget>[
        if (s.hasCover)
          row(
            l10n.deviceCover,
            // Moving is working: blinking green (DESIGN.md status rule). The
            // cover is one motor, so neither segment takes a tap meanwhile.
            s.isMoving
                ? StatusBlink(
                    on: true,
                    child: Text(
                      l10n.deviceMoving,
                      overflow: TextOverflow.ellipsis,
                      style: kSimpleText.copyWith(
                        color: AraColors.accentConnected,
                      ),
                    ),
                  )
                : null,
            _OnOff(
              id: 'simple-flat-cover',
              ids: ('open', 'closed'),
              labels: (l10n.deviceOpen, l10n.deviceClosed),
              on: s.coverOpen,
              enabled: !asking && !s.isMoving,
              fill: true,
              onChanged: (open) => send(openCover: open),
            ),
          ),
        if (s.hasCalibrator)
          row(
            l10n.deviceLight,
            // Warming up is a change between two states: blue (status rule).
            s.lightWarming
                ? Text(
                    l10n.deviceWarmingUp,
                    overflow: TextOverflow.ellipsis,
                    style: kSimpleText.copyWith(color: AraColors.accentInfo),
                  )
                : null,
            _OnOff(
              id: 'simple-flat-light',
              on: lightOn,
              enabled: !asking && !_applying && !s.lightWarming,
              fill: true,
              onChanged: (on) => send(lightOn: on),
            ),
          ),
      ];
      children.add(
        LayoutBuilder(
          builder: (context, box) {
            // As big as the rows leave room for, up to 140 pt; under 96 pt
            // (a narrow window, large text) it gives way.
            final face = math.min(
              _face,
              box.maxWidth -
                  AraSpace.s12 -
                  MediaQuery.textScalerOf(context).scale(_rowsMin),
            );
            final column = Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: AraSpace.s8,
              children: rows,
            );
            return face < 96
                ? column
                : Row(
                    spacing: AraSpace.s12,
                    children: [
                      Expanded(child: column),
                      // Says nothing the rows don't, so screen readers skip it.
                      ExcludeSemantics(
                        child: _FlatPanelFace(
                          key: const Key('simple-flat-face'),
                          size: face,
                          hasCover: s.hasCover,
                          cover: s.isMoving ? 0.5 : (s.coverOpen ? 1 : 0),
                          glow: !s.hasCalibrator || !lightOn
                              ? 0
                              : s.lightWarming
                              ? 0.25
                              : max == 0
                              ? 1
                              : 0.15 + 0.85 * level / max,
                        ),
                      ),
                    ],
                  );
          },
        ),
      );
      if (_asking != null) {
        final ask = _asking!;
        children.add(
          simpleWindowConfirm(
            question: l10n.devicePlanRunningChangeFlatPanel,
            confirm: l10n.deviceChangeAnyway,
            onCancel: () => setState(() {
              _asking = null;
              _level = null;
            }),
            onConfirm: () {
              setState(() => _asking = null);
              _apply(
                openCover: ask.openCover,
                lightOn: ask.lightOn,
                brightness: ask.brightness,
              );
            },
          ),
        );
      }
      if (s.hasCalibrator) {
        children.addAll([
          Text(l10n.deviceBrightness, style: kSimpleText),
          Row(
            children: [
              Expanded(
                child: Slider(
                  key: const Key('simple-flat-brightness'),
                  value: level.toDouble(),
                  // Slider needs max > min: held at 1 and disabled until the
                  // panel reports its range.
                  max: (max > 0 ? max : 1).toDouble(),
                  semanticFormatterCallback: (v) =>
                      max == 0 ? '—' : _percent(v.round(), max),
                  onChanged: max == 0 || asking || _applying
                      ? null
                      : (v) => setState(() => _level = v.round()),
                  // Sent on release only: each change is a blocking round
                  // trip to the panel.
                  onChangeEnd: max == 0 || asking || _applying
                      ? null
                      : (v) => send(brightness: v.round()),
                ),
              ),
              simpleSliderLevel(context, max == 0 ? '—' : _percent(level, max)),
            ],
          ),
        ]);
      }
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(context, ref.watch(deviceDriverProvider('flatdevice')).value),
      );
    }
    return Column(
      key: const Key('simple-device-flat'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  /// A level as a percent of the panel's maximum ("50 %"); anything above
  /// 0 reads at least 1 %, so a lit panel never reads 0 %. The panel is still
  /// sent its own units.
  static String _percent(int level, int max) {
    assert(max > 0);
    return '${level == 0 ? 0 : math.max(1, (level * 100 / max).round())} %';
  }

  Future<void> _apply({bool? openCover, bool? lightOn, int? brightness}) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.flatPanel);
    final l10n = context.l10n;
    if (_applying) return _sayBusy(ref, l10n);
    _applying = true;
    // A level alone sets the light too: above 0 on, 0 off (as the daemon does).
    final light = lightOn ?? (brightness == null ? null : brightness > 0);
    setState(() {
      _pendingLight = light;
      if (brightness != null) _level = brightness;
    });
    try {
      final notifier = ref.read(flatPanelProvider.notifier);
      final performed = await notifier.apply(
        openCover: openCover,
        lightOn: lightOn,
        brightness: brightness,
      );
      if (!performed) {
        _sayBusy(ref, l10n);
      } else if (!notifier.lastApplyConfirmed) {
        notify(
          l10n.deviceFlatPanelUnconfirmed,
          detail: l10n.deviceFlatPanelUnconfirmedDetail,
          error: true,
        );
      }
    } catch (e) {
      notify(
        l10n.deviceCouldntChangeFlatPanel,
        detail: failureReason(l10n, e),
        error: true,
      );
    } finally {
      // The panel's own reading takes over again, landed or not.
      _applying = false;
      if (mounted) {
        setState(() {
          _pendingLight = null;
          _level = null;
        });
      }
    }
  }
}

/// The panel face as it sits on the scope: two lids that slide apart as the
/// cover opens ([cover] 0 closed to 1 open), over a face that lights up with
/// the brightness ([glow] 0 dark to 1 full).
class _FlatPanelFace extends StatelessWidget {
  const _FlatPanelFace({
    super.key,
    required this.size,
    required this.hasCover,
    required this.cover,
    required this.glow,
  });

  final double size;
  final bool hasCover;
  final double cover;
  final double glow;

  @override
  Widget build(BuildContext context) => TweenAnimationBuilder<double>(
    tween: Tween(end: cover),
    duration: MediaQuery.disableAnimationsOf(context)
        ? Duration.zero
        : const Duration(milliseconds: 600),
    curve: Curves.easeInOut,
    builder: (context, open, _) => CustomPaint(
      size: Size.square(size),
      painter: _FlatPanelFacePainter(
        open: hasCover ? open : 1,
        hasCover: hasCover,
        glow: glow,
      ),
    ),
  );
}

class _FlatPanelFacePainter extends CustomPainter {
  _FlatPanelFacePainter({
    required this.open,
    required this.hasCover,
    required this.glow,
  });

  final double open;
  final bool hasCover;
  final double glow;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.shortestSide / 2;
    final face = Rect.fromCircle(center: c, radius: r - 5);
    canvas.drawCircle(c, r - 5, Paint()..color = AraColors.flatPanelFace);
    if (glow > 0) {
      canvas.drawCircle(
        c,
        r - 5,
        Paint()
          ..shader = RadialGradient(
            colors: [
              AraColors.flatPanelLightCentre.withValues(alpha: glow),
              AraColors.flatPanelLightEdge.withValues(alpha: glow),
            ],
          ).createShader(face),
      );
    }
    if (hasCover && open < 1) {
      canvas.save();
      canvas.clipPath(Path()..addOval(face));
      final lid = Paint()..color = AraColors.flatPanelLid;
      final seam = Paint()
        ..color = AraColors.flatPanelSeam
        ..strokeWidth = 1;
      final shift = face.width / 2 * open;
      for (final (left, dir) in [(face.left, -1.0), (face.center.dx, 1.0)]) {
        final half = Rect.fromLTWH(
          left + dir * shift,
          face.top,
          face.width / 2,
          face.height,
        );
        canvas.drawRect(half, lid);
        // Ribs across each lid, so the two halves read as hinged panels.
        for (var x = half.left + 6; x < half.right; x += 6) {
          canvas.drawLine(Offset(x, half.top), Offset(x, half.bottom), seam);
        }
      }
      canvas.restore();
    }
    canvas.drawCircle(
      c,
      r - 3,
      Paint()
        ..color = AraColors.border
        ..style = PaintingStyle.stroke
        ..strokeWidth = 5,
    );
  }

  @override
  bool shouldRepaint(_FlatPanelFacePainter old) =>
      old.open != open || old.hasCover != hasCover || old.glow != glow;
}

/// The weather station's window: its current readings as tiles, two across,
/// the moon after them, and how old the readings are. Read-only, so no pill
/// and no plan guard (#1662, mock `design/preview/simple-weather-mock.html`).
class SimpleWeatherWindow extends ConsumerStatefulWidget {
  const SimpleWeatherWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the station's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleWeatherWindow> createState() =>
      _SimpleWeatherWindowState();
}

class _SimpleWeatherWindowState extends ConsumerState<SimpleWeatherWindow> {
  /// The safety limits couldn't be read: the defaults mark nothing, so the
  /// window says so rather than look calm.
  bool _limitsUnknown = false;

  @override
  void initState() {
    super.initState();
    // The safety limits the tiles are checked against: Simple Mode never
    // opens the policies page that loads them otherwise.
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) return;
      final api = ref.read(profileApiProvider);
      if (api == null) {
        setState(() => _limitsUnknown = true);
        return;
      }
      try {
        await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
      } catch (_) {
        if (mounted) setState(() => _limitsUnknown = true);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final s = ref.watch(weatherProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellWeather, status: s),
    ];
    if (s?.connectionState == EquipmentConnectionState.error) {
      // A failed read shows no numbers, so old ones can't pass for live.
      children.add(
        Text(
          l10n.deviceSensorReadFailed,
          style: kSimpleText.copyWith(color: AraColors.errorText),
        ),
      );
    } else if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      children.add(_tiles(context, s));
      if (_limitsUnknown) {
        children.add(
          Text(
            l10n.deviceSafetyLimitsNotLoaded,
            style: kSimpleCaption.copyWith(
              color: AraColors.accentWarning,
            ),
          ),
        );
      }
      final at = s.capturedAt;
      if (at != null) {
        children.add(
          Padding(
            padding: const EdgeInsets.only(top: AraSpace.s4),
            child: _WeatherAge(at),
          ),
        );
      }
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(
          context,
          ref.watch(deviceDriverProvider('observingconditions')).value,
        ),
      );
    }
    return Column(
      key: const Key('simple-device-weather'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  /// A reading as the tiles show it: whole numbers bare, otherwise one
  /// decimal, a real minus sign.
  static String _num(double v, {int decimals = 1}) =>
      (v == v.roundToDouble() && decimals == 1
              ? v.toStringAsFixed(0)
              : v.toStringAsFixed(decimals))
          .replaceFirst('-', '−');

  static bool _ok(double? v) => v != null && v.isFinite;

  /// A tile's icon from Material or Cupertino.
  static Widget Function(Color) _icon(IconData icon) =>
      (color) => Icon(icon, size: 22, color: color);

  /// Material's dew point mark reads as a second thermometer: a leaf with a
  /// drop instead (user, 2026-11-08).
  static Widget _dew(Color color) =>
      EquipmentGlyph.dewPoint(size: 22, color: color);

  Widget _tiles(BuildContext context, WeatherStatus s) {
    final l10n = context.l10n;
    final p = ref.watch(safetyPoliciesProvider);
    // Marked only when these limits act (the daemon's same checks, §35.1).
    final mark = p.weatherTriggersEnabled;
    final t = s.temperatureC, dew = s.dewPointC, hum = s.humidityPct;
    final speed = s.windSpeedMs, gust = s.windGustMs;
    final tiles = <_WeatherTile>[
      if (_ok(s.cloudCoverPct))
        _WeatherTile(
          'cloud',
          _icon(Icons.cloud_outlined),
          _num(s.cloudCoverPct!),
          '%',
          l10n.deviceCloudCover,
        ),
      if (_ok(s.skyQualityMagArcsec2))
        _WeatherTile(
          'sqm',
          _icon(Icons.auto_awesome_outlined),
          _num(s.skyQualityMagArcsec2!, decimals: 2),
          'mag/arcsec²',
          l10n.deviceSkyQuality,
          // Too long beside the number in a half tile: it goes under it.
          unitBeside: false,
        ),
      if (_ok(t))
        _WeatherTile(
          'temperature',
          _icon(Icons.thermostat),
          _num(t!),
          '°C',
          l10n.deviceTemperature,
        ),
      if (_ok(dew))
        _WeatherTile(
          'dew',
          _dew,
          _num(dew!),
          '°C',
          l10n.deviceDewPoint,
          past: mark && _ok(t) && t! - dew < p.minDewDeltaC
              ? l10n.deviceMargin('${_num(p.minDewDeltaC)} °C')
              : null,
        ),
      if (_ok(hum))
        _WeatherTile(
          'humidity',
          _icon(Icons.water_drop_outlined),
          _num(hum!),
          '%',
          l10n.deviceHumidity,
          past: mark && hum > p.maxHumidityPct
              ? l10n.deviceLimit('${p.maxHumidityPct} %')
              : null,
        ),
      // A number even when dry: "Nenhuma" didn't fit a narrow half tile.
      // Drizzle under 0.1 would read "0.0", a hair from dry's "0".
      if (_ok(s.rainRate))
        _WeatherTile(
          'rain',
          _icon(CupertinoIcons.cloud_rain),
          s.rainRate! > 0 && s.rainRate! < 0.1 ? '< 0.1' : _num(s.rainRate!),
          'mm/h',
          l10n.deviceRain,
        ),
    ];
    if (_ok(speed) || _ok(gust)) {
      // Checked as the daemon does: the worse of speed and gust, and nothing
      // when either reads NaN (its Math.Max gives NaN, which breaches nothing).
      final nan =
          (speed != null && !speed.isFinite) ||
          (gust != null && !gust.isFinite);
      bool over(double? ms) =>
          mark && !nan && _ok(ms) && ms! * 3.6 > p.maxWindKmh;
      final shown = _ok(speed) ? speed : gust;
      final gustOver = _ok(speed) && over(gust);
      final dir = s.windDirectionDeg;
      const points = ['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw'];
      tiles.add(
        _WeatherTile(
          'wind',
          _icon(Icons.air),
          _num(shown! * 3.6),
          'km/h',
          l10n.deviceWind,
          details: [
            if (_ok(dir))
              (
                l10n.deviceCompass(points[(dir! / 45).round() % 8]),
                null,
                false,
              ),
            if (_ok(speed) && _ok(gust))
              (
                l10n.deviceGusts(_num(gust! * 3.6)),
                l10n.deviceGusts('${_num(gust * 3.6)} km/h'),
                gustOver,
              ),
          ],
          // A gust past the limit marks the tile and its gust, not the lower
          // sustained speed the number shows.
          past: over(shown) || gustOver
              ? l10n.deviceLimit('${p.maxWindKmh} km/h')
              : null,
          numberPast: over(shown),
        ),
      );
    }
    // Computed, not read: how much moon washes out the sky tonight.
    final moon = moonPhase(clock.now().toUtc());
    final phase = moon.phaseName.split(' ');
    tiles.add(
      _WeatherTile(
        'moon',
        _icon(Icons.nightlight_outlined),
        '${(moon.illuminatedFraction * 100).round()}',
        '%',
        l10n.deviceMoon,
        details: [
          (
            l10n.deviceMoonPhase(
              phase[0] + phase[1][0].toUpperCase() + phase[1].substring(1),
            ),
            null,
            false,
          ),
        ],
      ),
    );
    final rows = <Widget>[
      for (var i = 0; i < tiles.length; i += 2)
        // An odd count's last tile spans the row, so no row has a hole.
        i + 1 < tiles.length
            ? Row(
                spacing: AraSpace.s8,
                children: [
                  Expanded(child: tiles[i]),
                  Expanded(child: tiles[i + 1]),
                ],
              )
            : tiles[i],
    ];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: rows,
    );
  }
}

/// How old the readings are, redrawn once a second on its own so the tiles
/// don't rebuild with it; amber past a minute.
class _WeatherAge extends StatefulWidget {
  const _WeatherAge(this.capturedAt);

  final DateTime capturedAt;

  @override
  State<_WeatherAge> createState() => _WeatherAgeState();
}

class _WeatherAgeState extends State<_WeatherAge> {
  /// Older readings than this read amber: the daemon reads every 2 s and Ara
  /// asks every 15 s, so a live station never gets near it.
  static const Duration _stale = Duration(seconds: 60);

  late final Timer _tick;

  @override
  void initState() {
    super.initState();
    _tick = Timer.periodic(const Duration(seconds: 1), (_) => setState(() {}));
  }

  @override
  void dispose() {
    _tick.cancel();
    super.dispose();
  }

  /// "8 s", "4 min", "2 h".
  static String _age(Duration d) {
    final s = math.max(0, d.inSeconds);
    if (s < 60) return '$s s';
    if (s < 3600) return '${s ~/ 60} min';
    return '${s ~/ 3600} h';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final age = clock.now().toUtc().difference(widget.capturedAt);
    final stale = age > _stale;
    return Text(
      stale
          ? l10n.deviceLastReadingAgo(_age(age))
          : l10n.deviceUpdatedAgo(_age(age)),
      key: const Key('simple-weather-age'),
      style: kSimpleCaption.copyWith(
        color: stale ? AraColors.accentWarning : null,
      ),
    );
  }
}

/// One reading: the number big with its unit, its name and details under it,
/// and on a line of its own the limit it is past (so the caption can't cut
/// off why the tile is amber).
class _WeatherTile extends StatelessWidget {
  const _WeatherTile(
    this.id,
    this.icon,
    this.value,
    this.unit,
    this.label, {
    this.unitBeside = true,
    this.details = const [],
    this.past,
    bool? numberPast,
  }) : numberPast = numberPast ?? past != null;

  final String id;

  /// In front of the value, as large as its number, in the colour given.
  final Widget Function(Color color) icon;
  final String value;
  final String unit;
  final String label;

  /// The unit beside the number; else after the name (too long for a half
  /// tile beside the number: mag/arcsec²).
  final bool unitBeside;

  /// After the name: what it shows, what a screen reader hears (null: the
  /// same), and whether it is the part past the limit.
  final List<(String, String?, bool)> details;

  /// The limit it is past ("limit 85 %"), or null.
  final String? past;

  /// The number itself is past the limit (not only a detail, as a gust).
  final bool numberPast;

  @override
  Widget build(BuildContext context) {
    final warn = past != null;
    final caption = kSimpleCaption;
    final amber = caption.copyWith(color: AraColors.accentWarning);
    // A detail past the limit is all the caption shows after the name, so its
    // one line under a warning can't cut it off (screen readers hear them all).
    final shownDetails = details.any((d) => d.$3)
        ? details.where((d) => d.$3)
        : details;
    final parts = [
      TextSpan(text: label),
      for (final (shown, _, over) in shownDetails)
        TextSpan(text: shown, style: over ? amber : null),
    ];
    return Semantics(
      key: Key('simple-weather-$id'),
      container: true,
      // The value next to the name, then its details, then why it's amber.
      label: [
        '$label $value${unit.isEmpty ? '' : ' $unit'}',
        for (final (shown, spoken, _) in details) spoken ?? shown,
        if (warn) '${context.l10n.devicePastLimit} ($past)',
      ].join(', '),
      excludeSemantics: true,
      child: Container(
        // One height for every tile: the number and two lines under it.
        height: MediaQuery.textScalerOf(context).scale(80),
        padding: const EdgeInsets.symmetric(
          horizontal: AraSpace.s12,
          vertical: AraSpace.s8,
        ),
        decoration: BoxDecoration(
          color: AraColors.tileFill,
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
          border: warn
              ? Border.all(color: AraColors.accentWarning, width: 1.5)
              : null,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Row(
              spacing: AraSpace.s4,
              children: [
                icon(warn ? AraColors.accentWarning : AraColors.textSecondary),
                Flexible(
                  child: Text.rich(
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    TextSpan(
                      children: [
                        TextSpan(text: value),
                        if (unitBeside && unit.isNotEmpty)
                          TextSpan(
                            text: ' $unit',
                            style: kSimpleUnit,
                          ),
                      ],
                    ),
                    style: kSimpleReading.copyWith(
                      height: 1.2,
                      color: numberPast ? AraColors.accentWarning : null,
                    ),
                  ),
                ),
              ],
            ),
            Text.rich(
              TextSpan(
                children: [
                  for (final (i, part) in parts.indexed) ...[
                    if (i > 0) const TextSpan(text: ' · '),
                    part,
                  ],
                  // On its own line: after the name it broke at its slash.
                  if (!unitBeside && unit.isNotEmpty) TextSpan(text: '\n$unit'),
                ],
              ),
              maxLines: warn ? 1 : 2,
              overflow: TextOverflow.ellipsis,
              style: caption,
            ),
            if (warn)
              Text(
                past!,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: amber,
              ),
          ],
        ),
      ),
    );
  }
}

/// The weather readings past the rig's safety limits ([weatherBreaches]), in
/// words.
List<String> simpleWeatherBreaches(
  AppLocalizations l10n,
  WeatherStatus w,
  SafetyPolicies p,
) => [for (final b in weatherBreaches(w, p)) simpleWeatherBreachText(l10n, b)];

/// One breach in words: "Wind 41 km/h, over the 36 km/h limit".
String simpleWeatherBreachText(
  AppLocalizations l10n,
  ({WeatherBreach kind, double value, double limit}) b,
) {
  String n(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
  return switch (b.kind) {
    WeatherBreach.wind => l10n.deviceWindOverLimit(b.value.toStringAsFixed(0), b.limit.round()),
    WeatherBreach.humidity => l10n.deviceHumidityOverLimit(b.value.toStringAsFixed(0), b.limit.round()),
    WeatherBreach.dewMargin => l10n.deviceDewUnderMargin(n(b.value), n(b.limit)),
  };
}

/// A local time of day as the windows and pills show it: 24 h, "02:14".
String simpleClockTime(DateTime t) =>
    '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

/// A dome move that would disturb a running plan, so it asks first.
enum _DomeMove { open, close, park, home, turn }

/// A dome move Simple Mode asked for, and a turn's target.
typedef SimpleDomeAsk = ({SimpleDomeAskKind kind, int? target});

enum SimpleDomeAskKind { park, home, turn, open, close }

/// A dome move Simple Mode asked for, until the dome is done: the Dome
/// window's moving line and the status pill say what was asked ("Parking…",
/// "Turning to 245°…") from the tap, as Mount's Park does. A fast dome, or
/// one already there, moves between two reads and would show nothing
/// otherwise. Null when none is under way.
class SimpleDomeAskNotifier extends Notifier<SimpleDomeAsk?> {
  /// The dome was seen moving since the ask: still again means done.
  bool _moved = false;

  /// Nothing moved this long after the ask: it was already there.
  Timer? _still;

  @override
  SimpleDomeAsk? build() {
    ref.onDispose(() => _still?.cancel());
    ref.listen(domeProvider.select((s) => s.value), (_, d) {
      if (state == null) return;
      if (d == null || !d.isConnected || d.runtimeState == 'error') {
        return clear();
      }
      if (d.isBusy) {
        _moved = true;
        _still?.cancel();
        _still = null;
      } else if (_moved) {
        clear();
      }
    });
    return null;
  }

  void ask(SimpleDomeAsk a) {
    state = a;
    _moved = false;
    _still?.cancel();
    _still = Timer(const Duration(seconds: 3), () {
      if (!_moved) clear();
    });
  }

  void clear() {
    _still?.cancel();
    _still = null;
    state = null;
  }
}

final simpleDomeAskProvider =
    NotifierProvider<SimpleDomeAskNotifier, SimpleDomeAsk?>(
      SimpleDomeAskNotifier.new,
    );

/// What a dome ask reads while under way, and whether it is a change between
/// two states (the shutter: blue) rather than working (a move: green).
(String, bool) simpleDomeAskLabel(AppLocalizations l10n, SimpleDomeAsk a) =>
    switch (a.kind) {
      SimpleDomeAskKind.park => (l10n.deviceParking, false),
      SimpleDomeAskKind.home => (l10n.deviceFindingHome, false),
      SimpleDomeAskKind.turn => (l10n.deviceTurningTo('${a.target}°'), false),
      SimpleDomeAskKind.open => (l10n.deviceOpening, true),
      SimpleDomeAskKind.close => (l10n.deviceClosing, true),
    };

/// The dome's window: the azimuth with its compass point (and a turn's
/// target), then side by side the dome as it looks from the front, its two
/// pairs of shells fanning open and shut over about as long as this dome's
/// shutter takes, and a dial from above to turn it (drag to a target, play
/// in its middle; Stop while it moves). Then the shutter as Open / Closed,
/// Park and Home. Only what the dome says it can do shows. Sync, set park
/// and slaving stay in the full UI (#1662, mock
/// `design/preview/simple-dome-safety-mock.html` v12, the Baader AllSky
/// clamshell from the user's photos).
class SimpleDomeWindow extends ConsumerStatefulWidget {
  const SimpleDomeWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the dome's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleDomeWindow> createState() => _SimpleDomeWindowState();
}

class _SimpleDomeWindowState extends ConsumerState<SimpleDomeWindow> {
  /// A move asked for while a plan runs: the window asks first.
  _DomeMove? _asking;

  /// The dial's target, waiting for its play; whole degrees.
  int? _target;

  /// The target a Turn asked about while a plan runs.
  int? _askingTarget;

  /// The drawing and the dial, each at most.
  static const double _art = 150;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final s = ref.watch(domeProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellDome, status: s),
    ];
    if (s?.connectionState == EquipmentConnectionState.error) {
      children.add(
        Text(
          l10n.deviceDomeReadFailed,
          style: kSimpleText.copyWith(color: AraColors.errorText),
        ),
      );
    } else if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      // Watched, so it's known by the time a control is used.
      final planRunning =
          ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
      final shutter = ref.watch(domeShutterProvider);
      final caps = s.capabilities;
      final locked = _asking != null || s.isBusy;
      void send(_DomeMove move) =>
          planRunning ? setState(() => _asking = move) : _run(move);
      final turning = s.runtimeState == 'slewing';
      // Turning is working: blinking green; the shutter on its way between
      // open and closed is a change: blue (DESIGN.md status rule). Which way
      // it goes is Ara's, from the shutter's state before it moved.
      // The line keeps its room while still, so the window keeps its size
      // as the dome starts and stops (user, 2026-11-08).
      final ask = ref.watch(simpleDomeAskProvider);
      final asked = ask == null ? null : simpleDomeAskLabel(l10n, ask);
      final Widget moving = s.runtimeState == 'error'
          // The daemon reads the shutter as faulted: in the moving line's
          // place, so the window keeps its size.
          ? Text(
              l10n.deviceShutterFault,
              style: kSimpleText.copyWith(color: AraColors.errorText),
            )
          : asked != null
          ? (asked.$2
                ? Text(
                    asked.$1,
                    style: kSimpleText.copyWith(color: AraColors.accentInfo),
                  )
                : StatusBlink(
                    on: true,
                    child: Text(
                      asked.$1,
                      style: kSimpleText.copyWith(
                        color: AraColors.accentConnected,
                      ),
                    ),
                  ))
          : !s.isBusy
          ? Visibility(
              visible: false,
              maintainSize: true,
              maintainAnimation: true,
              maintainState: true,
              child: Text(l10n.deviceShutterMoving, style: kSimpleText),
            )
          : turning
          ? StatusBlink(
              on: true,
              child: Text(
                // The follower is turning it (#1689): not a move the user asked for.
                s.following ? l10n.deviceFollowingMount : l10n.deviceMoving,
                style: kSimpleText.copyWith(color: AraColors.accentConnected),
              ),
            )
          : Text(switch (shutter?.opening) {
              true => l10n.deviceOpening,
              false => l10n.deviceClosing,
              null => l10n.deviceShutterMoving,
            }, style: kSimpleText.copyWith(color: AraColors.accentInfo));
      final where = s.parked
          ? l10n.deviceParked
          : s.atHome
          ? l10n.deviceAtHome
          : null;
      final az = s.azimuthDeg;
      // A dome that turns and reports where; a roll-off roof has no azimuth.
      final rotates = (caps?.canSetAzimuth ?? false) && az != null;
      if (rotates) {
        const points = ['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw'];
        children.add(
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            spacing: AraSpace.s4,
            children: [
              Wrap(
                crossAxisAlignment: WrapCrossAlignment.end,
                spacing: AraSpace.s4,
                children: [
                  Text(
                    simpleDegrees(az),
                    style: kSimpleText.copyWith(
                      fontSize: 26,
                      fontWeight: FontWeight.w600,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.only(bottom: 4),
                    child: Text(
                      l10n.deviceCompass(points[(az / 45).round() % 8]),
                      style: kSimpleText.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ),
                  if (_target != null)
                    Padding(
                      padding: const EdgeInsets.only(bottom: 4),
                      child: Text(
                        '→ ${_target!}°',
                        key: const Key('simple-dome-target'),
                        style: kSimpleText.copyWith(
                          color: AraColors.accentConnected,
                        ),
                      ),
                    ),
                ],
              ),
              Text(
                where == null
                    ? l10n.deviceAzimuth
                    : '${l10n.deviceAzimuth} · $where',
                style: kSimpleCaption,
              ),
              moving,
            ],
          ),
        );
      } else {
        if (where != null) children.add(Text(where, style: AraText.caption));
        children.add(moving);
      }
      final face = _DomeFront(
        key: const Key('simple-dome-front'),
        open: s.shutterOpen,
        move: s.runtimeState == 'shutter_moving' ? shutter : null,
      );
      children.add(
        LayoutBuilder(
          builder: (context, box) {
            if (!rotates) {
              return Align(
                alignment: Alignment.centerLeft,
                child: SizedBox.square(
                  dimension: math.min(_art, box.maxWidth),
                  child: face,
                ),
              );
            }
            // Side by side, each up to 150 pt; too narrow for both (under
            // 96 pt each), the dial alone: it is the control.
            final each = math.min(_art, (box.maxWidth - AraSpace.s12) / 2);
            final dial = _DomeDial(
              key: const Key('simple-dome-dial'),
              size: each < 96 ? math.min(_art, box.maxWidth) : each,
              azimuth: az,
              target: _target,
              enabled: !locked,
              onTarget: (t) => setState(() => _target = t),
              hub: _RotatorHub(
                inkKey: const Key('simple-dome-hub'),
                moving: s.isBusy,
                label: s.isBusy
                    ? l10n.deviceStopDome
                    : _target == null
                    ? l10n.deviceTurn
                    : l10n.deviceTurnTo('${_target!}°'),
                // Stop never locks and never asks.
                onTap: s.isBusy
                    ? _stop
                    : !locked && _target != null
                    ? () => planRunning
                          ? setState(() {
                              _asking = _DomeMove.turn;
                              _askingTarget = _target;
                            })
                          : _turn(_target!)
                    : null,
              ),
            );
            if (each < 96) return Center(child: dial);
            return Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              // The dial, the control, on the left; the drawing on the right
              // (user, 2026-11-08).
              children: [
                dial,
                SizedBox.square(dimension: each, child: face),
              ],
            );
          },
        ),
      );
      // Only a dome that can turn to an azimuth can follow the mount (#1689).
      if (caps?.canSetAzimuth ?? false) {
        children.add(const SizedBox(height: AraSpace.s12));
        children.add(
          simpleWindowRow(
            l10n.deviceFollowsMount,
            _OnOff(
              id: 'simple-dome-follow',
              on: s.following,
              group: l10n.deviceFollowsMount,
              onChanged: _follow,
            ),
            id: 'dome-follow',
            labelInControl: true,
          ),
        );
      }
      if (caps?.canSetShutter ?? false) {
        children.add(
          // Clear of the drawing's plinth (user, 2026-11-08).
          const SizedBox(height: AraSpace.s12),
        );
        children.add(
          simpleWindowRow(
            l10n.deviceShutter,
            _OnOff(
              id: 'simple-dome-shutter',
              ids: ('open', 'closed'),
              labels: (l10n.deviceOpen, l10n.deviceClosed),
              on: s.shutterOpen,
              // Faulted: neither reads as selected, so either clears it.
              known: s.runtimeState != 'error',
              enabled: !locked,
              onChanged: (open) =>
                  send(open ? _DomeMove.open : _DomeMove.close),
            ),
            id: 'dome-shutter',
          ),
        );
      }
      final buttons = [
        if (caps?.canPark ?? false)
          Expanded(
            child: SimpleWindowButton(
              key: const Key('simple-dome-park'),
              label: l10n.devicePark,
              onTap: locked ? null : () => send(_DomeMove.park),
            ),
          ),
        if (caps?.canFindHome ?? false)
          Expanded(
            child: SimpleWindowButton(
              key: const Key('simple-dome-home'),
              label: l10n.deviceHome,
              onTap: locked ? null : () => send(_DomeMove.home),
            ),
          ),
      ];
      if (buttons.isNotEmpty) {
        children.add(
          Padding(
            padding: const EdgeInsets.only(top: AraSpace.s4),
            child: Row(spacing: AraSpace.s8, children: buttons),
          ),
        );
      }
      // Without the dial's middle, Stop is a tile, dimmed while still so the
      // window keeps its size.
      if (!rotates) {
        children.add(
          SimpleWindowButton(
            key: const Key('simple-dome-stop'),
            label: l10n.deviceStop,
            onTap: s.isBusy ? _stop : null,
          ),
        );
      }
      if (_asking != null) {
        final move = _asking!;
        final target = _askingTarget;
        children.add(
          simpleWindowConfirm(
            question: l10n.devicePlanRunningChangeDome,
            confirm: l10n.deviceChangeAnyway,
            onCancel: () => setState(() {
              _asking = null;
              _askingTarget = null;
            }),
            onConfirm: () {
              setState(() {
                _asking = null;
                _askingTarget = null;
              });
              move == _DomeMove.turn ? _turn(target!) : _run(move);
            },
          ),
        );
      }
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(context, ref.watch(deviceDriverProvider('dome')).value),
      );
    }
    return Column(
      key: const Key('simple-device-dome'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }

  Future<void> _turn(int target) async {
    setState(() => _target = null); // spent, as the rotator's Start
    final notify = userNotifier(context, origin: SimpleStatusOrigin.dome);
    final l10n = context.l10n;
    final asks = ref.read(simpleDomeAskProvider.notifier)
      ..ask((kind: SimpleDomeAskKind.turn, target: target));
    final wasFollowing = ref.read(domeProvider).value?.following ?? false;
    try {
      final performed = await ref
          .read(domeProvider.notifier)
          .slew(target.toDouble());
      if (!performed) {
        asks.clear();
        _sayBusy(ref, l10n);
      } else if (wasFollowing) {
        // The daemon turned following off with the move: say so once.
        notify(l10n.deviceStoppedFollowingMount);
      }
    } catch (e) {
      asks.clear();
      notify(
        l10n.deviceCouldntMoveDome,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  Future<void> _run(_DomeMove move) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.dome);
    final l10n = context.l10n;
    final n = ref.read(domeProvider.notifier);
    final wasFollowing = ref.read(domeProvider).value?.following ?? false;
    if (move == _DomeMove.open || move == _DomeMove.close) {
      ref.read(domeShutterProvider.notifier).asked();
    }
    final asks = ref.read(simpleDomeAskProvider.notifier)
      ..ask((
        kind: switch (move) {
          _DomeMove.open => SimpleDomeAskKind.open,
          _DomeMove.close => SimpleDomeAskKind.close,
          _DomeMove.park => SimpleDomeAskKind.park,
          _ => SimpleDomeAskKind.home,
        },
        target: null,
      ));
    try {
      final performed = await switch (move) {
        _DomeMove.open => n.openShutter(),
        _DomeMove.close => n.closeShutter(),
        _DomeMove.park => n.park(),
        _DomeMove.home => n.findHome(),
        _DomeMove.turn => throw StateError('a turn goes through _turn'),
      };
      if (!performed) {
        asks.clear();
        ref.read(domeShutterProvider.notifier).cancelAsk();
        _sayBusy(ref, l10n);
      } else if (wasFollowing &&
          (move == _DomeMove.park || move == _DomeMove.home)) {
        // The daemon turned following off with the move: say so once.
        notify(l10n.deviceStoppedFollowingMount);
      }
    } catch (e) {
      asks.clear();
      ref.read(domeShutterProvider.notifier).cancelAsk();
      notify(
        l10n.deviceCouldntMoveDome,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  /// The Follows the mount segments: the daemon says why when it can't.
  Future<void> _follow(bool on) async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.dome);
    final l10n = context.l10n;
    try {
      final performed = await ref.read(domeProvider.notifier).followMount(on);
      if (!performed) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntFollowMount,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }

  Future<void> _stop() async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.dome);
    final l10n = context.l10n;
    ref.read(simpleDomeAskProvider.notifier).clear();
    try {
      final performed = await ref.read(domeProvider.notifier).abortSlew();
      if (!performed) _sayBusy(ref, l10n);
    } catch (e) {
      notify(
        l10n.deviceCouldntStopDome,
        detail: failureReason(l10n, e),
        error: true,
      );
    }
  }
}

/// The dome from the front: the shells closed ([open] false), open, or on
/// their way ([move]), redrawn each frame while they move.
class _DomeFront extends StatefulWidget {
  const _DomeFront({super.key, required this.open, required this.move});

  final bool open;
  final DomeShutterMove? move;

  @override
  State<_DomeFront> createState() => _DomeFrontState();
}

class _DomeFrontState extends State<_DomeFront>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker((_) => setState(() {}));

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _sync();
  }

  @override
  void didUpdateWidget(_DomeFront old) {
    super.didUpdateWidget(old);
    _sync();
  }

  /// Runs only while a move of known direction slides; with reduce motion
  /// the shells stay put, so nothing redraws every frame (#1690 audit).
  void _sync() {
    final run =
        widget.move?.opening != null &&
        !MediaQuery.disableAnimationsOf(context);
    if (run && !_ticker.isActive) _ticker.start();
    if (!run && _ticker.isActive) _ticker.stop();
  }

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final m = widget.move;
    // Reduce motion: no slide, the shells sit where they were until done.
    final p = MediaQuery.disableAnimationsOf(context)
        ? 0.0
        : m?.progressAt(clock.now()) ?? 0;
    final open = switch (m?.opening) {
      true => p,
      false => 1 - p,
      // A move whose start Ara didn't see: half open, still.
      null when m != null => 0.5,
      null => widget.open ? 1.0 : 0.0,
    };
    // While it slides each frame sets the shells; when the dome reports it
    // landed they ease the rest of the way rather than jump there (user,
    // 2026-11-08: the end was always glitchy).
    final still = m == null && !MediaQuery.disableAnimationsOf(context);
    // Says nothing the reading and the segments don't.
    return ExcludeSemantics(
      child: TweenAnimationBuilder<double>(
        tween: Tween(end: open),
        duration: still ? const Duration(milliseconds: 500) : Duration.zero,
        curve: Curves.easeOutCubic,
        builder: (context, shown, _) => CustomPaint(
          painter: _AllSkyDomePainter(open: shown, moving: m != null),
        ),
      ),
    );
  }
}

/// A Baader AllSky style clamshell from the front (user's photos,
/// 2026-11-08): a sphere on a base with sloped shoulders and a round plinth,
/// the door running up into the sphere. Each side of the top is two nested
/// shells fanned on a bolt at the centre. Closed ([open] 0) the outer shells
/// meet at the crown; opening, both fan outward and down, the outer one
/// twice as fast, until at 1 they lie stacked over the lower half, their
/// lower edges on the base's shoulders.
class _AllSkyDomePainter extends CustomPainter {
  _AllSkyDomePainter({required this.open, required this.moving});

  final double open;
  final bool moving;

  static const _lower = [AraColors.domeLowerLight, AraColors.domeLowerDark];
  static const _shell = [AraColors.domeShellLight, AraColors.domeShellDark];

  @override
  void paint(Canvas canvas, Size size) {
    // Drawn on a 120 grid so the dome fills its box, as tall as the dial
    // beside it is wide (user, 2026-11-08).
    final k = size.shortestSide / 120;
    final cx = size.width / 2, cy = 57 * k, r = 52 * k;
    final edge = Paint()
      ..color = moving ? AraColors.accentInfo : AraColors.textSecondary
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.2;
    final line = Paint()
      ..color = AraColors.textSecondary
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1;
    Paint fill(List<Color> c, Rect rect) => Paint()
      ..shader = LinearGradient(
        begin: Alignment.topLeft,
        end: Alignment.bottomRight,
        colors: c,
      ).createShader(rect);
    final sphere = Rect.fromCircle(center: Offset(cx, cy), radius: r);
    // The fixed lower half.
    final lower = Path()
      ..moveTo(cx - r, cy)
      ..arcTo(sphere, math.pi, -math.pi, false)
      ..close();
    canvas.drawPath(lower, fill(_lower, sphere));
    canvas.drawPath(lower, line);
    // The base: sloped shoulders out from the door, straight sides, a plinth.
    final sy = cy + r * 0.3, sh = cy + r * 0.8, by = cy + r * 1.08;
    final dw = 12 * k;
    final base = Path()
      ..moveTo(cx - dw, sy)
      ..lineTo(cx - r * 0.92, sh)
      ..lineTo(cx - r * 0.92, by)
      ..lineTo(cx + r * 0.92, by)
      ..lineTo(cx + r * 0.92, sh)
      ..lineTo(cx + dw, sy)
      ..close();
    canvas.drawPath(base, fill(_lower, base.getBounds()));
    canvas.drawPath(base, line);
    final plinth = RRect.fromRectAndRadius(
      Rect.fromLTWH(cx - r * 1.04, by, r * 2.08, 4 * k),
      Radius.circular(2 * k),
    );
    canvas.drawRRect(plinth, Paint()..color = AraColors.domePlinth);
    canvas.drawRRect(plinth, line);
    // The door, its top cut like a hexagon.
    final dy = cy + 14 * k;
    final door = Path()
      ..moveTo(cx - dw, by)
      ..lineTo(cx - dw, dy + 6 * k)
      ..lineTo(cx - dw / 2, dy)
      ..lineTo(cx + dw / 2, dy)
      ..lineTo(cx + dw, dy + 6 * k)
      ..lineTo(cx + dw, by)
      ..close();
    canvas.drawPath(door, fill(_lower, door.getBounds()));
    canvas.drawPath(door, line);
    // The shells: theta in degrees from straight up, clockwise on the right
    // side and mirrored on the left.
    for (final side in [-1.0, 1.0]) {
      // One hinge point for all four shells, so closed they meet with no gap
      // (user, 2026-11-08).
      final pivot = Offset(cx, cy);
      Path fan(double from, double to, double radius) {
        final rect = Rect.fromCircle(center: pivot, radius: radius);
        final start = side > 0
            ? (from - 90) * math.pi / 180
            : (-90 - from) * math.pi / 180;
        final sweep = side * (to - from) * math.pi / 180;
        return Path()
          ..moveTo(pivot.dx, pivot.dy)
          ..arcTo(rect, start, sweep, false)
          ..close();
      }

      // Fully open, both stop with their lower edge on the base's shoulder
      // (132° from straight up), not past it.
      final inner = fan(46 + 36 * open, 92 + 40 * open, r + 1 * k);
      final outer = fan(-3 + 83 * open, 52 + 80 * open, r + 3 * k);
      for (final shell in [inner, outer]) {
        canvas.drawPath(shell, fill(_shell, shell.getBounds()));
        canvas.drawPath(shell, edge);
      }
    }
    // The hinge bolt, over all four shells.
    final bolt = Offset(cx, cy);
    canvas.drawCircle(bolt, 3 * k, Paint()..color = AraColors.domeBolt);
    canvas.drawCircle(bolt, 3 * k, line);
  }

  @override
  bool shouldRepaint(_AllSkyDomePainter old) =>
      old.open != open || old.moving != moving;
}

/// The dome from above, North up: the slit at the azimuth, a turn's target
/// as a green slit. Drag round it or tap it to pick the target (whole
/// degrees); a mouse wheel steps it a degree a notch and two fingers on a
/// trackpad 7 pt a degree, down clockwise, as the rotator's dial, both from
/// the target already picked. [hub] in its middle goes there.
class _DomeDial extends StatefulWidget {
  const _DomeDial({
    super.key,
    required this.size,
    required this.azimuth,
    required this.target,
    required this.enabled,
    required this.onTarget,
    required this.hub,
  });

  final double size;
  final double azimuth;
  final int? target;
  final bool enabled;
  final ValueChanged<int> onTarget;
  final Widget hub;

  @override
  State<_DomeDial> createState() => _DomeDialState();
}

class _DomeDialState extends State<_DomeDial> {
  /// This drag is a trackpad's two-finger pan: it steps, it doesn't point
  /// (where the fingers rest is no target; #1662, the target jumped there).
  bool _panned = false;
  double _travel = 0;

  /// The target last sent: several scroll steps can land before the
  /// window rebuilds with it.
  int? _sent;

  final _focus = FocusNode(debugLabel: 'simple-dome-dial');
  bool _focused = false;

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  int get _from => _sent ?? widget.target ?? widget.azimuth.round() % 360;

  @override
  void didUpdateWidget(_DomeDial old) {
    super.didUpdateWidget(old);
    if (widget.target != _sent) _sent = widget.target; // spent, or set here
  }

  void _send(int deg) {
    _sent = deg;
    widget.onTarget(deg);
  }

  void _step(int n) {
    if (n == 0) return;
    HapticFeedback.selectionClick();
    _send(((_from + n) % 360 + 360) % 360);
  }

  void _point(Offset p) {
    final size = widget.size;
    final d = p - Offset(size / 2, size / 2);
    // The middle is the hub's.
    if (d.distance < size * 0.18) return;
    final deg = (math.atan2(d.dx, -d.dy) * 180 / math.pi + 360).round() % 360;
    if (deg != _from) {
      HapticFeedback.selectionClick();
      _send(deg);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final enabled = widget.enabled;
    final size = widget.size;
    const points = [('n', 0.0), ('e', 90.0), ('s', 180.0), ('w', 270.0)];
    final k = size / 150;
    final scaler = MediaQuery.textScalerOf(context);
    final letterBox = Size(scaler.scale(24 * k), scaler.scale(16 * k));
    // Up or right a degree clockwise, as the screen reader's increase and
    // the rotator's dial (#1690 audit: no keyboard path).
    return FocusableActionDetector(
      focusNode: _focus,
      enabled: enabled,
      onShowFocusHighlight: (v) => setState(() => _focused = v),
      shortcuts: const {
        SingleActivator(LogicalKeyboardKey.arrowUp): _DialStep(1),
        SingleActivator(LogicalKeyboardKey.arrowRight): _DialStep(1),
        SingleActivator(LogicalKeyboardKey.arrowDown): _DialStep(-1),
        SingleActivator(LogicalKeyboardKey.arrowLeft): _DialStep(-1),
      },
      actions: {
        _DialStep: CallbackAction<_DialStep>(
          onInvoke: (i) => enabled ? _step(i.dir) : null,
        ),
      },
      child: Semantics(
        slider: true,
        label: l10n.deviceAzimuth,
        value: '$_from°',
        increasedValue: '${(_from + 1) % 360}°',
        decreasedValue: '${(_from + 359) % 360}°',
        onIncrease: enabled ? () => _step(1) : null,
        onDecrease: enabled ? () => _step(-1) : null,
        child: SizedBox.square(
          dimension: size,
          child: Stack(
            alignment: Alignment.center,
            children: [
              if (_focused)
                DecoratedBox(
                  key: const Key('simple-dome-dial-focus'),
                  decoration: const ShapeDecoration(
                    shape: CircleBorder(
                      side: BorderSide(color: AraColors.accentInfo, width: 2),
                    ),
                  ),
                  child: SizedBox.square(dimension: size),
                ),
              Listener(
                onPointerSignal: (e) {
                  if (e is! PointerScrollEvent || !enabled) return;
                  // Claimed, so the sheet under the window doesn't scroll too.
                  GestureBinding.instance.pointerSignalResolver.register(e, (
                    e,
                  ) {
                    final dy = (e as PointerScrollEvent).scrollDelta.dy;
                    if (dy != 0) _step(dy > 0 ? 1 : -1);
                  });
                },
                child: GestureDetector(
                  dragStartBehavior: DragStartBehavior.down,
                  onTapUp: enabled ? (d) => _point(d.localPosition) : null,
                  onPanStart: enabled
                      ? (d) {
                          _panned = d.kind == PointerDeviceKind.trackpad;
                          _travel = 0;
                          if (!_panned) _point(d.localPosition);
                        }
                      : null,
                  onPanUpdate: enabled
                      ? (d) {
                          if (!_panned) return _point(d.localPosition);
                          _travel += d.delta.dy;
                          final n = (_travel / 7).truncate();
                          _travel -= n * 7;
                          _step(n);
                        }
                      : null,
                  child: Opacity(
                    opacity: enabled ? 1 : 0.4,
                    child: ExcludeSemantics(
                      child: CustomPaint(
                        size: Size.square(size),
                        painter: _DomeDialPainter(
                          azimuth: widget.azimuth,
                          target: widget.target,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
              // The compass points in the reader's language, as the
              // azimuth's own ("245° SO" beside an O, not a W).
              for (final (p, deg) in points)
                // A box that grows with the text, centred on the point, so
                // large text isn't cut off (#1690 re-audit, WCAG 1.4.4).
                Positioned(
                  left:
                      size / 2 +
                      math.sin(deg * math.pi / 180) * (size / 2 - 23 * k) -
                      letterBox.width / 2,
                  top:
                      size / 2 -
                      math.cos(deg * math.pi / 180) * (size / 2 - 23 * k) -
                      letterBox.height / 2,
                  width: letterBox.width,
                  height: letterBox.height,
                  child: IgnorePointer(
                    child: ExcludeSemantics(
                      child: Center(
                        child: Text(
                          l10n.deviceCompass(p),
                          maxLines: 1,
                          softWrap: false,
                          overflow: TextOverflow.visible,
                          style: TextStyle(
                            fontSize: 11 * k,
                            height: 1,
                            color: AraColors.textSecondary,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              SizedBox.square(dimension: size * 0.3, child: widget.hub),
            ],
          ),
        ),
      ),
    );
  }
}

class _DomeDialPainter extends CustomPainter {
  _DomeDialPainter({required this.azimuth, required this.target});

  final double azimuth;
  final int? target;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final r = size.shortestSide / 2 - 3;
    final k = size.shortestSide / 150;
    canvas.drawCircle(c, r, Paint()..color = AraColors.tileFill);
    canvas.drawCircle(
      c,
      r,
      Paint()
        ..color = AraColors.border
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.5,
    );
    Offset at(double deg, double radius) =>
        c +
        Offset(
          math.sin(deg * math.pi / 180) * radius,
          -math.cos(deg * math.pi / 180) * radius,
        );
    final tick = Paint()
      ..color = AraColors.textSecondary
      ..strokeWidth = 1;
    for (var i = 0; i < 36; i++) {
      final len = i % 9 == 0 ? 10 : (i % 3 == 0 ? 6 : 3);
      canvas.drawLine(at(i * 10.0, r - len * k), at(i * 10.0, r), tick);
    }
    Path slit(double deg) {
      // 32° wide (user, 2026-11-08: 16° read too narrow).
      const w = 16 * math.pi / 180;
      final mid = (deg - 90) * math.pi / 180;
      final r0 = 26 * k;
      return Path()
        ..moveTo(c.dx + math.cos(mid - w) * r0, c.dy + math.sin(mid - w) * r0)
        ..arcTo(
          Rect.fromCircle(center: c, radius: r - 1),
          mid - w,
          2 * w,
          false,
        )
        ..lineTo(c.dx + math.cos(mid + w) * r0, c.dy + math.sin(mid + w) * r0)
        ..close();
    }

    if (target != null) {
      // Dashed would need a path dash; a thin green outline reads as "to be".
      canvas.drawPath(
        slit(target!.toDouble()),
        Paint()
          ..color = AraColors.accentConnected
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.5,
      );
    }
    final now = slit(azimuth);
    canvas.drawPath(now, Paint()..color = AraColors.domeSlit);
    canvas.drawPath(
      now,
      Paint()
        ..color = AraColors.textPrimary
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.4,
    );
  }

  @override
  bool shouldRepaint(_DomeDialPainter old) =>
      old.azimuth != azimuth || old.target != target;
}

/// The safety monitor's window: Safe or Unsafe and since when, and while
/// Unsafe what the rig's safety policy does next. Read-only; the policies
/// stay in the full UI (#1662, mock
/// `design/preview/simple-dome-safety-mock.html`).
class SimpleSafetyMonitorWindow extends ConsumerStatefulWidget {
  const SimpleSafetyMonitorWindow({super.key, this.inSettings = false});

  /// In Settings the window ends with the monitor's versions.
  final bool inSettings;

  @override
  ConsumerState<SimpleSafetyMonitorWindow> createState() =>
      _SimpleSafetyMonitorWindowState();
}

class _SimpleSafetyMonitorWindowState
    extends ConsumerState<SimpleSafetyMonitorWindow> {
  /// The policies came from the rig: until then the defaults could say the
  /// wrong thing, so the window says nothing about what happens next.
  bool _policiesLoaded = false;

  /// "since 02:14 · 1 h 12 min" counts on.
  late final Timer _tick;

  @override
  void initState() {
    super.initState();
    _tick = Timer.periodic(const Duration(seconds: 30), (_) {
      if (mounted) setState(() {});
    });
    // Read, never written here: Simple Mode never opens the policies page
    // that loads them otherwise.
    WidgetsBinding.instance.addPostFrameCallback((_) async {
      if (!mounted) return;
      final api = ref.read(profileApiProvider);
      if (api == null) return;
      try {
        await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
        if (mounted) setState(() => _policiesLoaded = true);
      } catch (_) {
        // Stays unloaded: nothing said beats a wrong promise.
      }
    });
  }

  @override
  void dispose() {
    _tick.cancel();
    super.dispose();
  }

  /// "7 min", "1 h 12 min".
  static String _for(Duration d) {
    final min = math.max(0, d.inMinutes);
    return min >= 60 ? '${min ~/ 60} h ${min % 60} min' : '$min min';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final s = ref.watch(safetyMonitorProvider).value;
    final children = <Widget>[
      SimpleDeviceHeader(type: l10n.shellSafetyMonitor, status: s),
    ];
    if (s?.connectionState == EquipmentConnectionState.error) {
      // A failed read shows no state, so an old Safe can't pass for live.
      children.add(
        Text(
          l10n.deviceSensorReadFailed,
          style: kSimpleText.copyWith(color: AraColors.errorText),
        ),
      );
    } else if (s == null || !s.isConnected) {
      children.add(
        Text(
          s?.isConnecting ?? false
              ? l10n.deviceConnecting
              : l10n.deviceNotConnected,
          style: AraText.caption,
        ),
      );
    } else {
      final p = _policiesLoaded ? ref.watch(safetyPoliciesProvider) : null;
      final w = ref.watch(weatherProvider).value;
      // Weather past its limits makes the rig unsafe too (the daemon's same
      // checks, §35.1), even while the monitor reads Safe.
      final breaches = p != null && p.weatherTriggersEnabled && w != null
          ? simpleWeatherBreaches(l10n, w, p)
          : const <String>[];
      final name = s.name.isEmpty ? l10n.shellSafetyMonitor : s.name;
      final monitorSafe = s.safe;
      // The card says what the rig is: the monitor's word, or the weather
      // past its limits while the monitor reads Safe (#1690 audit: a big
      // green Safe over amber reasons). The monitor's own reading goes under.
      final safe = monitorSafe && breaches.isEmpty;
      // Unsafe takes the warning colour (#1662), not the error red: the
      // monitor works, the sky doesn't.
      final colour = safe ? AraColors.accentConnected : AraColors.accentWarning;
      final word = safe ? l10n.deviceSafe : l10n.deviceUnsafe;
      final at = s.lastTransitionAt;
      final since = monitorSafe && !safe
          ? l10n.deviceMonitorReadsSafe(name)
          : at == null
          ? null
          : l10n.deviceSafetySince(
              simpleClockTime(at.toLocal()),
              _for(clock.now().difference(at)),
            );
      children.add(
        Semantics(
          container: true,
          label: since == null ? word : '$word, $since',
          excludeSemantics: true,
          child: Container(
            key: const Key('simple-safety-state'),
            constraints: const BoxConstraints(minHeight: 80),
            padding: const EdgeInsets.symmetric(
              horizontal: AraSpace.s12,
              vertical: AraSpace.s8,
            ),
            decoration: BoxDecoration(
              color: AraColors.tileFill,
              borderRadius: BorderRadius.circular(AraRadius.simplePanel),
              border: safe ? null : Border.all(color: colour, width: 1.5),
            ),
            child: Row(
              spacing: AraSpace.s12,
              children: [
                Icon(
                  safe
                      ? Icons.verified_user_outlined
                      : Icons.gpp_maybe_outlined,
                  size: 34,
                  color: colour,
                ),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        word,
                        style: kSimpleText.copyWith(
                          fontSize: 26,
                          fontWeight: FontWeight.w600,
                          color: colour,
                        ),
                      ),
                      if (since != null)
                        Text(
                          since,
                          style: kSimpleCaption.copyWith(
                            fontFeatures: const [FontFeature.tabularFigures()],
                          ),
                        ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
      );
      if (!safe || breaches.isNotEmpty) {
        Widget heading(String text) => Padding(
          padding: const EdgeInsets.only(top: AraSpace.s8),
          child: Text(text, style: kSimpleCaption),
        );
        Widget row(IconData icon, String text, {bool warn = false}) => Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: AraSpace.s8,
          children: [
            Icon(
              icon,
              size: 18,
              color: warn ? AraColors.accentWarning : AraColors.textSecondary,
            ),
            Expanded(
              child: Text(
                text,
                style: kSimpleText.copyWith(
                  fontSize: AraFontSize.simpleUnit,
                  color: warn ? AraColors.accentWarning : null,
                ),
              ),
            ),
          ],
        );
        children.add(heading(l10n.deviceWhy));
        // An ASCOM safety monitor reports safe or unsafe, never why.
        if (!monitorSafe) {
          children.add(row(Icons.sensors, l10n.deviceMonitorNoReason(name)));
        }
        for (final b in breaches) {
          // The pill uses the same words without the stop.
          children.add(
            row(Icons.air, l10n.deviceReasonSentence(b), warn: true),
          );
        }
        // What the rig's policy does, once it has loaded: a default could
        // promise the wrong thing.
        if (p != null) {
          children.addAll([
            heading(l10n.deviceWhatAraDoes),
            row(Icons.shield_outlined, switch (p.onUnsafe) {
              UnsafeAction.pauseAndPark => l10n.deviceOnUnsafePause,
              UnsafeAction.parkOnly => l10n.deviceOnUnsafePark,
              UnsafeAction.abortAndPark => l10n.deviceOnUnsafeAbort,
              UnsafeAction.ignore => l10n.deviceOnUnsafeIgnore,
            }),
            // Only a paused plan resumes (the daemon tracks paused runs).
            if (p.onUnsafe == UnsafeAction.pauseAndPark)
              Padding(
                padding: const EdgeInsets.only(left: 26),
                child: Text(
                  p.autoResumeWhenSafe
                      ? l10n.deviceResumesAfterSafe(p.resumeDelayMin)
                      : l10n.deviceStaysPaused,
                  style: kSimpleCaption,
                ),
              ),
          ]);
        }
      }
    }
    if (widget.inSettings) {
      children.addAll(
        _versions(
          context,
          ref.watch(deviceDriverProvider('safetymonitor')).value,
        ),
      );
    }
    return Column(
      key: const Key('simple-device-safety'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: children,
    );
  }
}
