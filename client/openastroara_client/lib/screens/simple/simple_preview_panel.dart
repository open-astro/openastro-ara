import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart' show Ticker;
import 'package:flutter/services.dart' show LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/guider_status.dart';
import '../../services/storage_space_api.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/simple_messages_state.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/stats/server_internal_state_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/focus/guide_focus_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../widgets/status_indicator.dart'
    show StatusBlink, StatusDot, StatusLevel;
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/equipment/dome_state.dart';
import '../../state/equipment/dome_shutter_state.dart';
import '../../state/equipment/flat_panel_state.dart';
import '../../state/equipment/rotator_state.dart';
import '../../state/equipment/safety_monitor_state.dart';
import '../../state/equipment/weather_state.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/equipment/switch_state.dart';
import '../../widgets/imaging/exposure_controls_panel.dart' show FilterDropdown;
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../imaging_actions.dart';
import 'simple_guide_panel.dart'
    show
        simpleGuideActive,
        simpleGuiderColour,
        simpleGuiderPendingProvider,
        simpleGuiding;
import 'simple_plan_panel.dart' show simpleRunProvider;
import 'simple_polar_panel.dart' show simplePolarActive, simplePolarPhase;
import 'simple_device_windows.dart'
    show
        SimpleGainSlider,
        simpleCelsius,
        simpleClockTime,
        simpleDomeAskLabel,
        simpleDomeAskProvider,
        simpleWeatherBreaches,
        simpleMountMotionProvider,
        simpleSwitchSettingProvider;
import 'simple_shell.dart'
    show
        SimplePanel,
        SimpleSegment,
        kSimpleRailWidth,
        kSimpleStatusMaxWidth,
        simpleFrost;

// kSimpleText moved to the theme so the Sky Album shares it; re-exported for Simple Mode's panels.
export '../../theme/ara_metrics.dart' show kSimpleText;


/// Single takes a saved frame (the daemon registers every Take One); Loop
/// runs live view, whose frames are not saved.
enum PreviewShot { loop, single }

final previewShotProvider = NotifierProvider<_PreviewShot, PreviewShot>(
  _PreviewShot.new,
);

class _PreviewShot extends Notifier<PreviewShot> {
  @override
  PreviewShot build() => PreviewShot.single;
  void set(PreviewShot v) => state = v;
}

/// "5 s · 100": the settings chip's caption on the rail.
String simpleSettingsSummary(WidgetRef ref) {
  final p = ref.watch(exposureControllerProvider);
  return '${formatExposure(p.exposure)} · ${p.gain}';
}

/// "3 h 12 min", "5 min", "2 d 4 h": uptime as Simple Mode writes time.
/// Pure — unit-tested.
String simpleUptime(double seconds) {
  final s = seconds.floor();
  final d = s ~/ 86400, h = (s % 86400) ~/ 3600, m = (s % 3600) ~/ 60;
  if (d > 0) return '$d d $h h';
  if (h > 0) return '$h h $m min';
  return '$m min';
}

/// "412 GB free" (whole GB from 100 up, one decimal below), or "Unplugged"
/// when the save drive can't be read. Pure — unit-tested.
String simpleFree(AppLocalizations l10n, int? bytes) {
  if (bytes == null) return l10n.previewUnplugged;
  final gb = bytes / 1073741824;
  return l10n.previewFree(gb.toStringAsFixed(gb >= 100 ? 0 : 1));
}

/// "512 MB" under 1 GB, "1.5 GB" from there: memory in use. Pure —
/// unit-tested.
String simpleMemory(int bytes) {
  const mb = 1048576;
  return bytes < 1024 * mb
      ? '${(bytes / mb).round()} MB'
      : '${(bytes / (1024 * mb)).toStringAsFixed(1)} GB';
}

/// The server's figures as tiles above Info's, turned on from the left
/// rail's System: storage free, CPU, memory and uptime. CPU and memory
/// are Ara's own process until the server reports the whole Pi. Re-read
/// every 5 s while it shows.
class SimpleSystemPanel extends ConsumerStatefulWidget {
  const SimpleSystemPanel({super.key, this.expand = false});

  /// Spread the tiles across the width they're given (Settings › This rig).
  final bool expand;

  @override
  ConsumerState<SimpleSystemPanel> createState() => _SimpleSystemPanelState();
}

class _SimpleSystemPanelState extends ConsumerState<SimpleSystemPanel> {
  late final Timer _poll = Timer.periodic(const Duration(seconds: 5), (_) {
    unawaited(ref.read(serverInternalStateProvider.notifier).refresh());
    ref.invalidate(storageSpaceProvider);
  });

  @override
  void initState() {
    super.initState();
    _poll; // start it
  }

  @override
  void dispose() {
    _poll.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(serverInternalStateProvider).value;
    final storage = ref.watch(storageSpaceProvider);
    final cpu = state?.cpuPercent;
    final l10n = context.l10n;
    final facts = [
      (
        l10n.previewStorage,
        storage.hasValue ? simpleFree(l10n, storage.value?.freeBytes) : '—',
      ),
      (l10n.previewCpu, cpu == null ? '—' : '${cpu.round()} %'),
      (
        l10n.previewMemory,
        state == null ? '—' : simpleMemory(state.workingSetBytes),
      ),
      (l10n.previewUp, state == null ? '—' : simpleUptime(state.uptimeSeconds)),
    ];
    return SimpleFactTiles(
      key: const Key('simple-system-line'),
      facts: facts,
      expand: widget.expand,
    );
  }
}

/// The last frame's facts as tiles in the bottom-left corner, turned on
/// from the left rail's Info:
/// HFR, stars, exposure · filter, gain · offset and clipped highlights.
class SimpleInfoPanel extends ConsumerWidget {
  const SimpleInfoPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final looping = ref.watch(liveViewControllerProvider);
    final id = ref.watch(lastCapturedFrameIdProvider);
    final params = ref.watch(exposureControllerProvider);
    // Stars and HFR exist for saved frames only (the daemon analyses those).
    final analysed = (looping || id == null)
        ? null
        : ref.watch(frameHistogramProvider(id)).asData?.value;
    // The frame's own header values, not the controls' (they differ when the
    // shot came from a sequence).
    final gainOffset = analysed?.gain == null
        ? '—'
        : '${analysed!.gain} · ${analysed.offset ?? '—'}';
    final clipped = analysed == null
        ? '—'
        : '${(analysed.highClipFraction * 100).toStringAsFixed(analysed.highClipFraction < 0.001 ? 0 : 2)} %';
    final l10n = context.l10n;
    final facts = [
      (l10n.previewHfr, analysed?.hfr?.toStringAsFixed(2) ?? '—'),
      (l10n.previewStars, analysed?.stars?.toString() ?? '—'),
      (
        l10n.previewExposure,
        '${formatExposure(params.exposure)} · ${params.filterSlot}',
      ),
      (l10n.previewGainOffset, gainOffset),
      (l10n.previewClipped, clipped),
    ];
    return SimpleFactTiles(facts: facts);
  }
}

/// A row of fact tiles (label over value, 72 × 56), the first fact at the
/// left end, nearest the corner: how Info and System read. Above 1.3x text
/// the tiles would squeeze it back down, so the facts read as one list
/// instead, a label and value per line (WCAG 1.4.4).
class SimpleFactTiles extends StatelessWidget {
  const SimpleFactTiles({super.key, required this.facts, this.expand = false});

  final List<(String, String)> facts;

  /// Equal tiles across the full width instead of 72 px each.
  final bool expand;

  static final _value = kSimpleText.copyWith(
    fontSize: 13,
    fontWeight: FontWeight.w600,
  );

  static final _box = BoxDecoration(
    color: AraColors.overlayScrim,
    border: Border.all(color: AraColors.border),
    borderRadius: BorderRadius.circular(10),
  );

  // On the image, the panel blur; in Settings › Rig the sheet already has it.
  Widget _frost(Widget child) => expand ? child : simpleFrost(child);

  @override
  Widget build(BuildContext context) {
    if (MediaQuery.textScalerOf(context).scale(1) > 1.3) {
      return _frost(
        Container(
        key: const Key('simple-facts-list'),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
        decoration: _box,
        child: Table(
          defaultColumnWidth: const IntrinsicColumnWidth(),
          children: [
            for (final (label, value) in facts)
              TableRow(
                children: [
                  Padding(
                    padding: const EdgeInsets.only(right: 18),
                    child: Text(label, style: AraText.caption),
                  ),
                  Text(value, style: _value, textAlign: TextAlign.right),
                ],
              ),
          ],
        ),
      ),
      );
    }
    return _tiles();
  }

  Widget _tiles() => Row(
    mainAxisSize: expand ? MainAxisSize.max : MainAxisSize.min,
    spacing: AraSpace.s8,
    children: [for (final (label, value) in facts) _tile(label, value)],
  );

  Widget _tile(String label, String value) {
    final tile = _frost(
        Container(
      width: expand ? null : kSimpleRailWidth,
      height: 56,
      padding: const EdgeInsets.symmetric(horizontal: 4),
      decoration: _box,
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        spacing: 1,
        children: [
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(label, style: AraText.caption, maxLines: 1),
          ),
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(value, style: _value, maxLines: 1),
          ),
        ],
      ),
    ),
      );
    return expand ? Expanded(child: tile) : tile;
  }
}

/// True from the tap on the square until the daemon has answered the abort:
/// the abort is a round trip to the rig (about a second), and the shutter
/// and status should answer the tap, not the reply.
final _stoppingProvider = NotifierProvider<_Stopping, bool>(_Stopping.new);

class _Stopping extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool v) => state = v;
}

/// Cancels the shot in flight with immediate feedback. The capture state
/// itself still waits for the daemon (cancelCapture keeps tracking the shot
/// if the abort didn't go through, so its frame isn't orphaned).
Future<void> _stop(BuildContext context, WidgetRef ref) async {
  final stopping = ref.read(_stoppingProvider.notifier);
  stopping.set(true);
  try {
    await cancelCapture(context, ref);
  } finally {
    stopping.set(false);
  }
}

/// The mode's settings, opened from the rail's chip: Exposure (picked from a
/// list), the Gain slider, Filter, and Loop / Single.
class SimpleSettingsPopover extends ConsumerWidget {
  const SimpleSettingsPopover({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final params = ref.watch(exposureControllerProvider);
    final ctrl = ref.read(exposureControllerProvider.notifier);
    final shot = ref.watch(previewShotProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final caps = ref.watch(cameraStatusProvider).asData?.value?.capabilities;
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s8,
        children: [
          // No title: the chip that opened it says what it is (user,
          // 2026-11-04).
          SimplePresetTile<Duration>(
            key: const Key('simple-exposure'),
            label: context.l10n.previewExposure,
            value: params.exposure,
            options: exposurePresets(
              minSec: caps?.minExposureSec ?? 0,
              maxSec: caps?.maxExposureSec ?? 0,
            ),
            format: formatExposure,
            onSelected: ctrl.setExposure,
          ),
          // The camera's gain: the same slider and value as the Camera window.
          SimpleGainSlider(
            id: 'simple-gain',
            value: params.gain,
            onSet: ctrl.setGain,
          ),
          FilterDropdown(
            value: params.filterSlot,
            onChanged: ctrl.setFilterSlot,
          ),
          // Switching while a loop runs would strand it with no stop.
          Container(
            decoration: BoxDecoration(
              color: AraColors.tileFill,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Row(
              children: [
                for (final (v, label) in [
                  (PreviewShot.loop, context.l10n.previewLoop),
                  (PreviewShot.single, context.l10n.previewSingle),
                ])
                  Expanded(
                    child: SimpleSegment(
                      label: label,
                      selected: shot == v,
                      radius: 8,
                      onTap: looping
                          ? null
                          : () => ref.read(previewShotProvider.notifier).set(v),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The shutter on the rail: takes a frame (Single) or starts and stops the
/// loop (Loop); while a shot runs its square stops it.
class SimpleShutter extends ConsumerWidget {
  const SimpleShutter({super.key, this.start = false});

  /// Play instead of the camera's filled circle (Focus on Manual: the frames
  /// are for focusing, not the picture).
  final bool start;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final shot = ref.watch(previewShotProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final capturing = ref.watch(captureProgressProvider).isCapturing;
    final stopping = ref.watch(_stoppingProvider);
    return SimpleShutterButton(
      start: start,
      running: (looping || capturing) && !stopping,
      onPressed: stopping
          ? null
          // A loop adopted from the daemon runs under either shot pick: the
          // square stops it (DESIGN.md: whatever starts something stops it).
          : looping
          ? () => toggleLiveView(context, ref, false)
          : switch (shot) {
              PreviewShot.loop => () => toggleLiveView(context, ref, true),
              // The square stops the shot in flight, like a loop.
              PreviewShot.single =>
                capturing
                    ? () => _stop(context, ref)
                    : () => takeOne(context, ref),
            },
    );
  }
}

/// "5 s", "0.5 s", "2 min" — how the exposure reads on its tile and list.
String formatExposure(Duration d) {
  final ms = d.inMilliseconds;
  if (ms >= 60000 && ms % 60000 == 0) return '${ms ~/ 60000} min';
  if (ms % 1000 == 0) return '${ms ~/ 1000} s';
  return '${(ms / 1000).toString()} s';
}

const _kExposurePresetsMs = [
  100, 200, 500, 1000, 2000, 3000, 5000, 10000, 15000, 20000, 30000, //
  45000, 60000, 90000, 120000, 180000, 240000, 300000, 600000,
];

/// The exposure list, trimmed to the camera's reported range (0 = not
/// reported, no limit on that side).
List<Duration> exposurePresets({
  required double minSec,
  required double maxSec,
}) => [
  for (final ms in _kExposurePresetsMs)
    if ((minSec <= 0 || ms >= minSec * 1000) &&
        (maxSec <= 0 || ms <= maxSec * 1000))
      Duration(milliseconds: ms),
];

/// A labelled 48 px tile that opens a list to pick from. The current value
/// stays listed even when it isn't a preset (set in the full UI, say).
/// Arrow keys for a wheel, which Flutter's pickers lack (WCAG 2.1.1): Tab
/// reaches it, Down moves to the next item and Up to the one before, and a
/// ring shows it has focus. [debugLabel] names its focus node.
class SimpleWheelKeys extends StatefulWidget {
  const SimpleWheelKeys({
    super.key,
    required this.debugLabel,
    required this.onStep,
    required this.child,
    this.autofocus = false,
  });

  final String debugLabel;
  final ValueChanged<int> onStep;
  final Widget child;
  final bool autofocus;

  /// [onStep] for a picker on [c] with [count] items: one item, kept in range.
  static ValueChanged<int> stepper(FixedExtentScrollController c, int count) =>
      (d) {
        final to = (c.selectedItem + d).clamp(0, count - 1);
        if (to != c.selectedItem) c.jumpToItem(to);
      };

  @override
  State<SimpleWheelKeys> createState() => _SimpleWheelKeysState();
}

class _SimpleWheelKeysState extends State<SimpleWheelKeys> {
  late final _focus = FocusNode(debugLabel: widget.debugLabel);
  bool _ring = false;

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => FocusableActionDetector(
    focusNode: _focus,
    autofocus: widget.autofocus,
    onShowFocusHighlight: (v) => setState(() => _ring = v),
    shortcuts: const {
      SingleActivator(LogicalKeyboardKey.arrowDown): _WheelStep(1),
      SingleActivator(LogicalKeyboardKey.arrowUp): _WheelStep(-1),
    },
    actions: {
      _WheelStep: CallbackAction<_WheelStep>(
        onInvoke: (i) => widget.onStep(i.dir),
      ),
    },
    child: DecoratedBox(
      position: DecorationPosition.foreground,
      decoration: BoxDecoration(
        border: _ring
            ? Border.all(color: AraColors.accentInfo, width: 2)
            : null,
        borderRadius: BorderRadius.circular(8),
      ),
      child: widget.child,
    ),
  );
}

/// A pill's tap that a keyboard reaches too: Tab focuses it, with the
/// [SimpleWheelKeys] ring, and Enter or Space taps it (DESIGN.md: keyboard
/// reach with visible focus).
class SimpleTapTarget extends StatefulWidget {
  const SimpleTapTarget({super.key, required this.onTap, required this.child});

  final VoidCallback? onTap;
  final Widget child;

  @override
  State<SimpleTapTarget> createState() => _SimpleTapTargetState();
}

class _SimpleTapTargetState extends State<SimpleTapTarget> {
  bool _ring = false;

  @override
  Widget build(BuildContext context) => FocusableActionDetector(
    enabled: widget.onTap != null,
    onShowFocusHighlight: (v) => setState(() => _ring = v),
    mouseCursor: widget.onTap == null
        ? MouseCursor.defer
        : SystemMouseCursors.click,
    actions: {
      ActivateIntent: CallbackAction<ActivateIntent>(
        onInvoke: (_) => widget.onTap?.call(),
      ),
    },
    child: GestureDetector(
      behavior: HitTestBehavior.opaque,
      onTap: widget.onTap,
      child: DecoratedBox(
        position: DecorationPosition.foreground,
        decoration: BoxDecoration(
          border: _ring
              ? Border.all(color: AraColors.accentInfo, width: 2)
              : null,
          borderRadius: BorderRadius.circular(10),
        ),
        child: widget.child,
      ),
    ),
  );
}

class _WheelStep extends Intent {
  const _WheelStep(this.dir);
  final int dir;
}

class SimplePresetTile<T> extends StatelessWidget {
  const SimplePresetTile({
    super.key,
    required this.label,
    required this.value,
    required this.options,
    required this.format,
    required this.onSelected,
    this.wheel = false,
  });

  final String label;
  final T value;
  final List<T> options;
  final String Function(T) format;
  final ValueChanged<T> onSelected;

  /// Pick on an iOS-style wheel (spun with a thumb, the neighbours in view)
  /// instead of a list: for a long ordered set like a frame count.
  final bool wheel;

  @override
  Widget build(BuildContext context) {
    final items = options.contains(value) ? options : [value, ...options];
    // Ink, so the focus and press highlight paints over the fill, not under.
    final tile = ConstrainedBox(
      // At least 48 px; taller when the user's text is larger.
      constraints: const BoxConstraints(minHeight: 48),
      child: Ink(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: AraColors.tileFill,
          borderRadius: BorderRadius.circular(10),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: AraText.caption,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            Text(
              format(value),
              style: kSimpleText,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ],
        ),
      ),
    );
    // Its own transparent Material: the fill and highlight paint here, over
    // the panel's scrim, not on a Material further down.
    if (wheel) {
      // An InkWell, not a bare tap: Tab reaches it and Enter opens the wheel.
      return Material(
        type: MaterialType.transparency,
        child: Semantics(
          button: true,
          label: label,
          child: InkWell(
            borderRadius: BorderRadius.circular(10),
            onTap: () => _showWheel(context, items),
            child: tile,
          ),
        ),
      );
    }
    return Material(
      type: MaterialType.transparency,
      child: PopupMenuButton<T>(
        tooltip: label,
        initialValue: value,
        onSelected: onSelected,
        position: PopupMenuPosition.under,
        shape: const RoundedRectangleBorder(
          borderRadius: BorderRadius.all(Radius.circular(10)),
        ),
        constraints: const BoxConstraints(minWidth: 96, maxHeight: 360),
        itemBuilder: (_) => [
          for (final o in items)
            PopupMenuItem<T>(
              value: o,
              height: 44,
              child: Text(format(o), style: kSimpleText),
            ),
        ],
        child: tile,
      ),
    );
  }

  Future<void> _showWheel(BuildContext context, List<T> items) async {
    var picked = value;
    final wheel = FixedExtentScrollController(
      initialItem: items.indexOf(value),
    );
    final done = await showCupertinoModalPopup<bool>(
      context: context,
      builder: (popup) => Container(
        height: 280,
        color: AraColors.bgPanel,
        child: SafeArea(
          top: false,
          child: Column(
            children: [
              Row(
                children: [
                  Padding(
                    padding: const EdgeInsets.only(left: 16),
                    child: Text(label, style: kSimpleText),
                  ),
                  const Spacer(),
                  TextButton(
                    onPressed: () => Navigator.of(popup).pop(true),
                    child: Text(context.l10n.previewDone),
                  ),
                ],
              ),
              Expanded(
                // Focused on opening, so the arrows turn it at once.
                child: SimpleWheelKeys(
                  debugLabel: 'simple-wheel-$label',
                  autofocus: true,
                  onStep: SimpleWheelKeys.stepper(wheel, items.length),
                  child: CupertinoPicker(
                    itemExtent: 44,
                    scrollController: wheel,
                    onSelectedItemChanged: (i) => picked = items[i],
                    children: [
                      for (final o in items)
                        Center(child: Text(format(o), style: kSimpleText)),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
    wheel.dispose();
    if (done ?? false) onSelected(picked);
  }
}

/// The round capture button: a white disc to start, a red square to stop
/// whatever it started (DESIGN.md: the start button always stops).
class SimpleShutterButton extends StatelessWidget {
  const SimpleShutterButton({
    super.key,
    required this.running,
    required this.onPressed,
    this.label,
    this.start = false,
    this.whyOff,
  });

  final bool running;

  /// Why the shutter is greyed out, when it is: said in a pill on a tap
  /// and to screen readers as a hint (user, 2026-11-04).
  final String? whyOff;

  /// A play triangle at rest instead of the filled circle: only Preview's
  /// shutter takes a picture; every other mode's starts its routine.
  final bool start;
  final VoidCallback? onPressed;

  /// What a tap does, for screen readers; defaults to Stop / Take a frame.
  final String? label;

  @override
  Widget build(BuildContext context) {
    final enabled = onPressed != null;
    final fg = enabled ? AraColors.textPrimary : AraColors.textDisabled;
    final why = enabled ? null : whyOff;
    return Semantics(
      button: true,
      label:
          label ??
          (running ? context.l10n.previewStop : context.l10n.previewTakeFrame),
      hint: why,
      child: InkResponse(
        key: const Key('simple-shutter'),
        onTap: onPressed ?? (why == null ? null : () => simpleSayWhy(context, why)),
        radius: 36,
        child: Container(
          width: 60,
          height: 60,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            border: Border.all(color: fg, width: 3),
          ),
          alignment: Alignment.center,
          child: start && !running
              ? Icon(Icons.play_arrow, size: 40, color: fg)
              : running
              ? SimpleStopSquare(color: enabled ? AraColors.accentError : fg)
              : Container(
                  width: 40,
                  height: 40,
                  decoration: BoxDecoration(color: fg, shape: BoxShape.circle),
                ),
        ),
      ),
    );
  }
}

/// The main camera is taking frames: a shot, Preview's loop or a running
/// plan. Routines that need it, or that move the mount under it (autofocus,
/// the Bahtinov readout, polar alignment), wait (user, 2026-11-04).
bool simpleMainCameraBusy(WidgetRef ref) =>
    ref.watch(captureProgressProvider.select((p) => p.isCapturing)) ||
    ref.watch(liveViewControllerProvider) ||
    ref.watch(
      sequenceRunStateProvider.select((s) => s.value?.state?.isActive ?? false),
    );

/// A greyed shutter's reason, as a pill: once at a time, fading like a result.
void simpleSayWhy(BuildContext context, String why) {
  final container = ProviderScope.containerOf(context, listen: false);
  final messages = container.read(simpleMessagesProvider.notifier);
  if (container.read(simpleMessagesProvider).any((m) => m.title == why)) {
    return;
  }
  final id = messages.show(SimpleMessageKind.warning, why);
  Timer(kSimpleResultLife, () => messages.dismiss(id));
}

/// What the camera is doing, as a small pill on the image (like a camera's
/// REC light): looping, taking a frame with its countdown and bar,
/// stopping, frame ready, or why it failed. Nothing while idle. It sits
/// outside the panels, so it stays in the immersive view.
/// The stop square's side: the shutter's while it works, and the Focuser
/// dial's middle while autofocus runs.
const double kSimpleStopSquare = 22;

/// The square that stops what is running; red, so stop reads as stop.
class SimpleStopSquare extends StatelessWidget {
  const SimpleStopSquare({super.key, this.color = AraColors.accentError});

  final Color color;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('simple-stop-square'),
    width: kSimpleStopSquare,
    height: kSimpleStopSquare,
    decoration: BoxDecoration(
      color: color,
      borderRadius: BorderRadius.circular(4),
    ),
  );
}

/// The status pill's height (Plan's sheet starts below it).
const double kSimpleStatusHeight = 48;

/// One status pill's content.
class _PillData {
  const _PillData(
    this.origin,
    this.label,
    this.detail,
    this.dot, {
    this.ring = false,
    this.working = false,
    this.shotProgress,
    this.footProgress,
    this.spoken,
  });

  final SimpleStatusOrigin origin;
  final String label;
  final String? detail;

  /// Changing: the dot is a ring (DESIGN.md status rule).
  final bool ring;
  final Color dot;

  /// Working normally: the dot blinks (DESIGN.md status rule).
  final bool working;

  /// A shot's bar, beside the words.
  final double? shotProgress;

  /// A step's progress (a plan step, cooling), along the pill's foot.
  final double? footProgress;

  /// What screen readers hear when it changes (label and detail otherwise).
  final String? spoken;
}

/// One pill: tapping it opens what it's about. Within one state (its label)
/// it keeps the widest it has been, so a countdown or a falling temperature
/// doesn't make it jitter (ALP-4937).
class _StatusPill extends StatefulWidget {
  const _StatusPill({super.key, required this.data, this.onOpen});

  final _PillData data;
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  @override
  State<_StatusPill> createState() => _StatusPillState();
}

class _StatusPillState extends State<_StatusPill> {
  final _panelKey = GlobalKey();
  String? _widthLabel;
  double _width = 0;

  void _keepWidth() {
    final w = _panelKey.currentContext?.size?.width ?? 0;
    if (mounted && w > _width) setState(() => _width = w);
  }

  @override
  Widget build(BuildContext context) {
    final d = widget.data;
    final l10n = context.l10n;
    if (d.label != _widthLabel) {
      _widthLabel = d.label;
      _width = 0;
    }
    WidgetsBinding.instance.addPostFrameCallback((_) => _keepWidth());
    final open = widget.onOpen == null ? null : () => widget.onOpen!(d.origin);
    return Semantics(
      container: true,
      liveRegion: true,
      label:
          d.spoken ??
          (d.detail == null
              ? d.label
              : l10n.previewStatusSpoken(d.label, d.detail!)),
      excludeSemantics: true,
      button: open != null,
      onTap: open,
      child: SimpleTapTarget(
        key: const Key('simple-status-pill'),
        onTap: open,
        child: ConstrainedBox(
          constraints: BoxConstraints(
            minWidth: math.min(_width, kSimpleStatusMaxWidth),
            maxWidth: kSimpleStatusMaxWidth,
          ),
          child: SimplePanel(
            key: _panelKey,
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
            child: Stack(
              children: [
                Container(
                  height: kSimpleStatusHeight,
                  // With the bar, the words move up so the two sit together.
                  padding: EdgeInsets.only(
                    bottom: d.footProgress == null ? 0 : 9,
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      StatusBlink(
                        on: d.working,
                        child: StatusDot(color: d.dot, ring: d.ring),
                      ),
                      const SizedBox(width: AraSpace.s8),
                      // One text, so the label keeps its room and only the
                      // detail's tail is cut (two Flexibles halved the space).
                      Flexible(
                        child: Text.rich(
                          TextSpan(
                            text: d.label,
                            children: [
                              if (d.detail != null) ...[
                                const WidgetSpan(
                                  child: SizedBox(width: AraSpace.s8),
                                ),
                                TextSpan(
                                  text: d.detail,
                                  style: const TextStyle(
                                    color: AraColors.textSecondary,
                                  ),
                                ),
                              ],
                            ],
                          ),
                          style: kSimpleText,
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                      if (d.shotProgress != null) ...[
                        const SizedBox(width: AraSpace.s12),
                        SizedBox(
                          width: 60,
                          child: ClipRRect(
                            borderRadius: BorderRadius.circular(2),
                            child: LinearProgressIndicator(
                              value: d.shotProgress,
                              minHeight: 4,
                              backgroundColor: AraColors.tileFill,
                            ),
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
                if (d.footProgress != null)
                  Positioned(
                    left: 0,
                    right: 0,
                    bottom: 10,
                    child: ClipRRect(
                      borderRadius: BorderRadius.circular(2),
                      child: LinearProgressIndicator(
                        value: d.footProgress,
                        minHeight: 3,
                        backgroundColor: AraColors.tileFill,
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


class SimpleStatusPill extends ConsumerStatefulWidget {
  const SimpleStatusPill({super.key, this.onOpen});

  /// Opens what the pill is about (its mode's panel, or a device window).
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  @override
  ConsumerState<SimpleStatusPill> createState() => _SimpleStatusPillState();
}

class _SimpleStatusPillState extends ConsumerState<SimpleStatusPill>
    with SingleTickerProviderStateMixin {
  late final Ticker _ticker = createTicker((_) => setState(() {}));

  /// Where the sensor was when the pill first saw it cooling: the bar's 0.
  double? _coolStart;

  @override
  void dispose() {
    _ticker.dispose();
    super.dispose();
  }

  /// The bar and remaining time are elapsed-based and the daemon's progress
  /// poll is slow, so redraw every frame while a shot is active — a smooth
  /// bar instead of one that freezes, or steps 4×/s.
  void _syncTick(bool active) {
    if (active && !_ticker.isActive) {
      _ticker.start();
    } else if (!active && _ticker.isActive) {
      _ticker.stop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final p = ref.watch(captureProgressProvider);
    final stopping = ref.watch(_stoppingProvider);
    final looping = ref.watch(liveViewControllerProvider);
    final run = ref.watch(sequenceRunStateProvider).value;
    // A scheduled plan waits on the rig: it says when it starts instead.
    final startAt = ref.watch(simpleRunProvider.select((r) => r.startAt));
    final l10n = context.l10n;
    final step = startAt != null && startAt.isAfter(DateTime.now())
        ? l10n.previewStartsAt(
            '${startAt.hour.toString().padLeft(2, '0')}:'
            '${startAt.minute.toString().padLeft(2, '0')}',
          )
        // #1596: a skipped run in Simple Mode's words; other reasons (the
        // meridian countdown) are the daemon's live text, like the step itself.
        : run?.reasonCode == 'target_skipped'
        ? l10n.previewPlanSkipped
        : run?.reasonText ?? run?.currentInstructionDescription;
    final paused =
        run?.state == SequenceRunState.paused ||
        run?.state == SequenceRunState.pausedAwaitingUser;
    _syncTick(p.isActive);
    // A cooler turned on by hand: the pill shows it cooling until it is
    // within half a degree of its target (a plan's own cooling step shows
    // as the plan's step instead).
    final cam = ref.watch(cameraStatusProvider).value;
    final ccd = cam?.ccdTemperature;
    final target = cam?.coolerSetpointC;
    final cooling =
        cam != null &&
        cam.isConnected &&
        cam.coolerOn &&
        ccd != null &&
        target != null &&
        ccd - target > 0.5;
    _coolStart = cooling ? (_coolStart ?? ccd) : null;
    // A park or unpark asked for in Simple Mode, until the mount gets there.
    final parking = ref.watch(simpleMountMotionProvider);
    // The filter wheel turning, or a manual wheel waiting for its hand swap.
    final wheel = ref.watch(filterWheelProvider).value;
    final swap = wheel != null && wheel.isAwaitingUser ? wheel.pending : null;
    // The rotator turning, whoever turned it (#1668).
    final rotator = ref.watch(rotatorProvider).value;
    // The flat panel's cover moving or its light warming up (#1668).
    final flat = ref.watch(flatPanelProvider).value;
    // The dome turning (a slew, park or home) or its shutter moving (#1668).
    final dome = ref.watch(domeProvider).value;
    final shutter = ref.watch(domeShutterProvider);
    // A dome move asked in its window, from the tap (as a park is).
    final domeAsk = ref.watch(simpleDomeAskProvider);
    // The safety monitor reading Unsafe, still there after the alarm is
    // silenced (#1668).
    final safety = ref.watch(safetyMonitorProvider).value;
    final unsafe = safety != null && safety.isConnected && !safety.safe;
    // Weather past its limits holds a plan too (the daemon's same checks),
    // even while the monitor reads Safe: the pill says which (#1690 audit).
    final policies = ref.watch(safetyPoliciesProvider);
    final weather = ref.watch(weatherProvider).value;
    final breach = policies.weatherTriggersEnabled && weather != null
        ? simpleWeatherBreaches(l10n, weather, policies).firstOrNull
        : null;
    // The focuser moving, whoever moved it; autofocus has its own pill.
    final focuser = ref.watch(focuserProvider).value;
    final settingPort = ref.watch(switchActingProvider)
        ? ref.watch(simpleSwitchSettingProvider)
        : null;
    // The guider's routine, or Guiding with its RMS, when nothing else is on.
    final guider = ref.watch(guiderStatusProvider).value;
    final guiding = simpleGuiding(guider);
    final guiderOn = simpleGuideActive(guider);
    final guiderAsk = ref.watch(simpleGuiderPendingProvider);
    final planActive = run?.state?.isActive ?? false;
    final polar = ref.watch(polarAlignLiveProvider);
    final af = ref.watch(autofocusLiveProvider).run;
    final bahtinov = ref.watch(bahtinovFocusProvider.select((b) => b.status.active));
    final guideFocus = ref.watch(guideFocusProvider.select((g) => g.status.active));
    // One pill per thing happening, stacked in this order (user, 2026-11-04:
    // a frame hid the guider). Each says it once when it changes, not each
    // second of a countdown, a calibration step or an RMS update.
    final pills = <_PillData>[
      if (stopping)
        _PillData(SimpleStatusOrigin.preview, l10n.previewStopping, null, AraColors.textSecondary)
      else if (p.phase != CapturePhase.idle)
        switch (p.phase) {
          CapturePhase.exposing => _PillData(
            SimpleStatusOrigin.preview,
            l10n.previewTakingFrame,
            p.exposureRemaining == null
                ? l10n.previewStarting
                // Whole seconds, rounded up: "1 s left" until it's done.
                : l10n.previewSecondsLeft(
                    (p.exposureRemaining!.inMilliseconds / 1000).ceil(),
                  ),
            AraColors.accentConnected,
            working: true,
            shotProgress: (p.displayProgressPct ?? 0) / 100,
            spoken: l10n.previewTakingFrame,
          ),
          // On its way from the camera: blue (DESIGN.md status rule).
          CapturePhase.downloading => _PillData(
            SimpleStatusOrigin.preview,
            l10n.previewDownloading,
            null,
            AraColors.accentInfo,
            ring: true,
          ),
          CapturePhase.done => _PillData(
            SimpleStatusOrigin.preview,
            l10n.previewFrameReady,
            null,
            AraColors.accentConnected,
          ),
          _ => _PillData(
            SimpleStatusOrigin.preview,
            l10n.previewCaptureFailed,
            p.error,
            AraColors.accentError,
          ),
        }
      else if (looping)
        _PillData(
          SimpleStatusOrigin.preview,
          l10n.previewLooping,
          null,
          AraColors.accentConnected,
          working: true,
        ),
      // A running plan says what it's doing (cooling, slewing, centring,
      // focusing), with how far the step has got along the pill's foot.
      // #1596: a finished run that imaged nothing keeps its reason on the pill.
      if ((planActive || run?.reasonText != null) && step != null && step.isNotEmpty)
        _PillData(
          SimpleStatusOrigin.plan,
          paused ? l10n.previewPlanPaused : l10n.previewPlan,
          step,
          // DESIGN.md status rule: running is working (green); paused by Ara
          // is on its way back (blue); paused waiting for the user needs a
          // look (amber).
          run!.state == SequenceRunState.pausedAwaitingUser || !planActive
              ? AraColors.accentBusy
              : paused
              ? AraColors.accentInfo
              : AraColors.accentConnected,
          ring: paused && run.state != SequenceRunState.pausedAwaitingUser,
          working: planActive && !paused,
          footProgress: run.currentInstructionProgress,
        ),
      // Unsafe right after the plan: it is why a plan holds. Steady amber,
      // not error red: the monitor works, the sky doesn't.
      if (unsafe)
        _PillData(
          SimpleStatusOrigin.safetyMonitor,
          l10n.shellSafetyMonitor,
          safety.lastTransitionAt == null
              ? l10n.deviceUnsafe
              : l10n.deviceUnsafeSince(
                  simpleClockTime(safety.lastTransitionAt!.toLocal()),
                ),
          AraColors.accentWarning,
        )
      else if (breach != null)
        _PillData(
          SimpleStatusOrigin.safetyMonitor,
          l10n.shellSafetyMonitor,
          breach,
          AraColors.accentWarning,
        ),
      if (parking != null)
        _PillData(
          SimpleStatusOrigin.mount,
          parking ? l10n.previewParking : l10n.previewUnparking,
          null,
          AraColors.accentInfo,
          ring: true,
        ),
      // Moving is working: blinking green, as the wheel's dot and window
      // show it (DESIGN.md status rule).
      if (wheel != null && wheel.isConnected && wheel.isMoving)
        _PillData(
          SimpleStatusOrigin.filterWheel,
          l10n.shellFilterWheel,
          l10n.deviceMoving,
          AraColors.accentConnected,
          working: true,
        )
      else if (swap != null)
        _PillData(
          SimpleStatusOrigin.filterWheel,
          l10n.shellFilterWheel,
          l10n.deviceSwapIn(
            swap.name.isEmpty ? l10n.deviceSlot(swap.position) : swap.name,
          ),
          AraColors.accentBusy,
        ),
      // Moving is working: blinking green, as the focuser's dot and window.
      if (focuser != null &&
          focuser.isConnected &&
          focuser.isMoving &&
          !af.isRunning)
        _PillData(
          SimpleStatusOrigin.focuser,
          l10n.deviceFocuser,
          l10n.deviceMoving,
          AraColors.accentConnected,
          working: true,
        ),
      if (rotator != null && rotator.isConnected && rotator.isMoving)
        _PillData(
          SimpleStatusOrigin.rotator,
          l10n.shellRotator,
          l10n.deviceMoving,
          AraColors.accentConnected,
          working: true,
        ),
      // A port change on its way is changing: a blue ring (DESIGN.md status
      // rule), naming the port the Switches window asked for (#1668).
      if (settingPort != null)
        _PillData(
          SimpleStatusOrigin.switches,
          l10n.shellSwitches,
          l10n.deviceSettingPort(settingPort),
          AraColors.accentInfo,
          ring: true,
        ),
      // The cover moving wins over the light warming up; a light-only panel
      // has no cover to move (#1668).
      if (flat != null && flat.isConnected && flat.hasCover && flat.isMoving)
        _PillData(
          SimpleStatusOrigin.flatPanel,
          l10n.shellFlatPanel,
          l10n.deviceCoverMoving,
          AraColors.accentConnected,
          working: true,
        )
      else if (flat != null && flat.isConnected && flat.lightWarming)
        _PillData(
          SimpleStatusOrigin.flatPanel,
          l10n.shellFlatPanel,
          l10n.deviceLightWarmingUp,
          AraColors.accentInfo,
          ring: true,
        ),
      // The dome turning is working (blinking green); its shutter on its way
      // is a change between two states (blue ring), Opening… / Closing…
      // when Ara saw where it started.
      if (dome != null && dome.isConnected && domeAsk != null)
        switch (simpleDomeAskLabel(l10n, domeAsk)) {
          (final text, true) => _PillData(
            SimpleStatusOrigin.dome,
            l10n.shellDome,
            text,
            AraColors.accentInfo,
            ring: true,
          ),
          (final text, false) => _PillData(
            SimpleStatusOrigin.dome,
            l10n.shellDome,
            text,
            AraColors.accentConnected,
            working: true,
          ),
        }
      else if (dome != null &&
          dome.isConnected &&
          dome.runtimeState == 'slewing')
        _PillData(
          SimpleStatusOrigin.dome,
          l10n.shellDome,
          l10n.deviceMoving,
          AraColors.accentConnected,
          working: true,
        )
      else if (dome != null &&
          dome.isConnected &&
          dome.runtimeState == 'shutter_moving')
        _PillData(
          SimpleStatusOrigin.dome,
          l10n.shellDome,
          switch (shutter?.opening) {
            true => l10n.deviceOpening,
            false => l10n.deviceClosing,
            null => l10n.deviceShutterMoving,
          },
          AraColors.accentInfo,
          ring: true,
        ),
      // A cooler turned on by hand (a plan's own cooling shows as its step).
      if (cooling && !planActive)
        _PillData(
          SimpleStatusOrigin.camera,
          l10n.previewCooling,
          '${simpleCelsius(ccd)} → ${simpleCelsius(target)}',
          AraColors.accentInfo,
          ring: true,
          footProgress: (_coolStart! - target) <= 0
              ? 0.0
              : ((_coolStart! - ccd) / (_coolStart! - target)).clamp(0.0, 1.0),
          spoken: l10n.previewCooling,
        ),
      // Every mode's routine has a pill while it runs; a failure is a message
      // on the left that fades, posted by the shell (user, 2026-11-04).
      if (af.isRunning)
        _PillData(
          SimpleStatusOrigin.focus,
          l10n.shellModeFocus,
          MainFocusCard.phaseText(l10n, af),
          AraColors.accentInfo,
          ring: true,
          spoken: l10n.shellModeFocus,
        )
      else if (bahtinov)
        _PillData(
          SimpleStatusOrigin.focus,
          l10n.shellModeFocus,
          l10n.focusManual,
          AraColors.accentConnected,
          working: true,
        )
      else if (guideFocus)
        _PillData(
          SimpleStatusOrigin.focus,
          l10n.shellModeFocus,
          l10n.focusScopeGuide,
          AraColors.accentConnected,
          working: true,
        ),
      if (simplePolarActive(polar))
        _PillData(
          SimpleStatusOrigin.polar,
          l10n.shellModePolarAlign,
          simplePolarPhase(polar, l10n),
          AraColors.accentInfo,
          ring: true,
        ),
      if (guiderAsk != null)
        _PillData(
          SimpleStatusOrigin.guide,
          l10n.deviceGuider,
          guiderAsk ? l10n.guideStarting : l10n.guideStopping,
          AraColors.accentInfo,
          ring: true,
        )
      else if (guiding)
        _PillData(
          SimpleStatusOrigin.guide,
          l10n.shellGuiding,
          switch (guider!.rmsTotalArcsec) {
            final r? => '${r.toStringAsFixed(2)}″',
            null => null,
          },
          simpleGuiderColour(guider),
          ring: guiderStatusLevel(guider) == StatusLevel.changing,
          working: guiderStatusLevel(guider) == StatusLevel.active,
          spoken: l10n.shellGuiding,
        )
      else if (guiderOn)
        _PillData(
          SimpleStatusOrigin.guide,
          l10n.deviceGuider,
          guider!.runtimeSummary(l10n),
          simpleGuiderColour(guider),
          spoken: l10n.previewStatusSpoken(
            l10n.deviceGuider,
            guiderRuntimeText(l10n, guider.runtimeState),
          ),
        ),
    ];
    if (pills.isEmpty) return const SizedBox.shrink();
    // Three at most; the rest fold into "+n", which opens the first one left.
    final shown = pills.take(3).toList();
    final more = pills.length - shown.length;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.end,
      spacing: AraSpace.s8,
      children: [
        for (final d in shown)
          _StatusPill(
            key: ValueKey(d.origin),
            data: d,
            onOpen: widget.onOpen,
          ),
        if (more > 0)
          Semantics(
            button: widget.onOpen != null,
            child: SimpleTapTarget(
              onTap: widget.onOpen == null
                  ? null
                  : () => widget.onOpen!(pills[3].origin),
              child: SimplePanel(
                padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
                child: ConstrainedBox(
                  constraints: const BoxConstraints(minHeight: 44, minWidth: 44),
                  child: Center(
                    widthFactor: 1,
                    heightFactor: 1,
                    child: Text(
                      '+$more',
                      semanticsLabel: l10n.statusMoreSemantics(more),
                      style: kSimpleText,
                    ),
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }
}
