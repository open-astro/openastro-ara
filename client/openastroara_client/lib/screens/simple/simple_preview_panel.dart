import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart' show Ticker;
import 'package:flutter/services.dart'
    show
        FilteringTextInputFormatter,
        LengthLimitingTextInputFormatter,
        LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/equipment_device_status.dart';
import '../../models/guider_status.dart';
import '../../services/equipment_driver_api.dart' show deviceDriverProvider;
import '../../services/storage_space_api.dart';
import '../../state/guider/guider_state.dart';
import '../../state/guider/guiding_assistant_state.dart';
import '../../state/guider/guider_live_state.dart';
import '../../models/guiding_assistant.dart' show GuidingAssistantStatus;
import '../../state/settings/phd2_settings_state.dart';
import '../../state/imaging/capture_progress_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_state.dart';
import '../../state/imaging/main_camera_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/guider/guider_build_activity_state.dart';
import 'simple_calibration.dart' show simpleCalibrationProgress;
import '../../state/simple_preflight_state.dart'
    show SimpleModeScreen, SimplePreflightStep, SimplePreflightStepLabel, simplePreflightProvider, simpleShowingProvider;
import '../../models/sequence/sequence_summary.dart';
import '../../state/stats/server_internal_state_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/focus/filter_offset_calibration_state.dart';
import '../../state/focus/guide_focus_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../widgets/focus/guide_focus_card.dart'
    show guideFocusBlocker, guideFocusBusy;
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
import '../../services/profile_api.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/equipment/switch_state.dart';
import '../../widgets/imaging/exposure_controls_panel.dart' show FilterDropdown;
import '../../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import '../imaging_actions.dart';
import 'simple_guide_panel.dart'
    show
        SimpleGuiderExposureTile,
        simpleGuiderColour,
        simpleGuiderExposureText,
        simpleGuiderPendingProvider;
import 'simple_plan_panel.dart' show simpleRunProvider;
import 'simple_polar_panel.dart' show simplePolarActive, simplePolarPhase;
import 'simple_device_windows.dart'
    show
        SimpleGainSlider,
        SimpleGuideGainSlider,
        simpleCelsius,
        simpleClockTime,
        simpleDomeAskLabel,
        simpleDomeAskProvider,
        simpleWeatherBreaches,
        simpleMountHomeProvider,
        simpleMountMotionProvider,
        simpleSwitchSettingProvider;
import 'simple_mount_controls.dart'
    show SimplePointingPhase, simplePointingProvider;
import 'simple_status_card.dart' show SimpleCardHairline;
import 'simple_shell.dart'
    show
        SimplePanel,
        SimpleSegment,
        SimpleSegments,
        simpleStackText,
        kSimpleRailWidth,
        kSimpleMessagesMaxWidth,
        simpleFrost;

// kSimpleText moved to the theme so the Sky Album shares it; re-exported for Simple Mode's panels.
export '../../theme/ara_metrics.dart' show kSimpleText;

/// Single takes a saved frame (the daemon registers every Take One); Loop
/// runs live view, whose frames are not saved.
enum PreviewShot { loop, single }

/// Which camera Preview shows: the main camera's frames, or the guide camera's
/// through the guider.
enum PreviewCamera { main, guide }

final previewCameraProvider = NotifierProvider<_PreviewCamera, PreviewCamera>(
  _PreviewCamera.new,
);

class _PreviewCamera extends Notifier<PreviewCamera> {
  @override
  PreviewCamera build() => PreviewCamera.main;
  void set(PreviewCamera v) => state = v;
}

/// Whether Preview is on the guide camera: picked, and the guider connected
/// and not holding it (guiding takes it back, and Preview is on Main again).
bool simplePreviewGuide(WidgetRef ref) {
  if (ref.watch(previewCameraProvider) != PreviewCamera.guide) return false;
  // A running loop keeps its Stop even while the guider status is briefly gone.
  if (ref.watch(guideFocusProvider.select((g) => g.status.active))) return true;
  final guider = ref.watch(guiderStatusProvider).value;
  // The guider's own loop is Preview's (#1875 slice 3): only a guiding
  // phase takes the camera back.
  return guider != null &&
      guider.isConnected &&
      (simpleGuideLooping(guider) || !guideFocusBusy(guider));
}

/// The guider loops plain frames, a star picked or not: Preview › Guide's
/// loop (#1875 slice 3).
bool simpleGuideLooping(GuiderStatus? guider) =>
    guider != null &&
    guider.isConnected &&
    const {
      GuiderRuntimeState.looping,
      GuiderRuntimeState.starSelected,
      GuiderRuntimeState.starLost,
    }.contains(guider.runtimeState);

/// Preview › Guide's shutter: loops the guider's plain frames, and its square
/// stops them. A guide-camera focus loop found running gets its stop here
/// too; focusing the guide camera stays in Focus › Guide (#1875 slice 3).
class SimplePreviewGuideShutter extends ConsumerWidget {
  const SimplePreviewGuideShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final focusLoop = ref.watch(guideFocusProvider.select((g) => g.status.active));
    final looping = simpleGuideLooping(ref.watch(guiderStatusProvider).value);
    final busy = ref.watch(guiderLiveProvider.select((s) => s.busy));
    final live = ref.read(guiderLiveProvider.notifier);
    return SimpleShutterButton(
      start: true,
      running: focusLoop || looping,
      label: focusLoop || looping ? l10n.previewGuideLoopStop : l10n.previewGuideLoopStart,
      onPressed: busy
          ? null
          : focusLoop
          ? ref.read(guideFocusProvider.notifier).stop
          : looping
          ? live.stop
          : live.loop,
    );
  }
}

/// Preview is the screen on show and it is on the guide camera, so what is
/// on screen, and every fact about it, comes from that camera.
bool simpleShowsGuidePreview(WidgetRef ref) =>
    ref.watch(simpleShowingProvider) == SimpleModeScreen.preview &&
    simplePreviewGuide(ref);

final previewShotProvider = NotifierProvider<_PreviewShot, PreviewShot>(
  _PreviewShot.new,
);

class _PreviewShot extends Notifier<PreviewShot> {
  @override
  PreviewShot build() => PreviewShot.single;
  void set(PreviewShot v) => state = v;
}

/// "5 s · 100": the settings chip's caption on the rail; on the guide camera
/// "Guide" over its own exposure and gain.
String simpleSettingsSummary(BuildContext context, WidgetRef ref) {
  if (simplePreviewGuide(ref)) {
    // The guider's own exposure: the one Preview › Guide's loop runs at.
    final phd2 = ref.watch(phd2SettingsProvider);
    final exposure = simpleGuiderExposureText(context.l10n, phd2.guideExposureMs);
    return '${context.l10n.focusScopeGuide}\n$exposure · ${phd2.guideCameraGain}';
  }
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

/// "04:32" under an hour, "1:04:32" from an hour up: a countdown (#1747).
/// Pure — unit-tested.
String simpleCountdown(int seconds) {
  final s = seconds < 0 ? 0 : seconds;
  final h = s ~/ 3600, m = (s % 3600) ~/ 60, sec = s % 60;
  String two(int v) => v.toString().padLeft(2, '0');
  return h > 0 ? '$h:${two(m)}:${two(sec)}' : '${two(m)}:${two(sec)}';
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

/// Info's facts over the frame are on (session only; off to start). Turned
/// on and off from Preview's and Focus's panels; the left rail's Info went
/// when Monitor took over watching the night (user, 2026-11-11).
class SimpleInfoShown extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool shown) => state = shown;
  void toggle() => state = !state;
}

final simpleInfoShownProvider = NotifierProvider<SimpleInfoShown, bool>(SimpleInfoShown.new);

/// Info On / Off, a row in Preview's and Focus's panels.
class SimpleInfoToggle extends ConsumerWidget {
  const SimpleInfoToggle({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final label = Text(l10n.shellInfo, style: kSimpleText);
    // Large text: the segments go under the label, sharing its width.
    final stack = simpleStackText(context);
    final segments = SimpleSegments<bool>(
      keyPrefix: 'simple-info',
      expand: stack,
      group: l10n.shellInfo,
      options: [(true, l10n.deviceOn, 'on'), (false, l10n.deviceOff, 'off')],
      selected: ref.watch(simpleInfoShownProvider),
      onPick: ref.read(simpleInfoShownProvider.notifier).set,
    );
    return stack
        ? Column(crossAxisAlignment: CrossAxisAlignment.stretch, spacing: AraSpace.s4, children: [label, segments])
        : Row(spacing: AraSpace.s8, children: [Expanded(child: label), segments]);
  }
}

/// The last frame's facts as tiles in the bottom-left corner, turned on
/// from [SimpleInfoToggle]:
/// HFR, stars, exposure · filter, gain · offset and clipped highlights.
class SimpleInfoPanel extends ConsumerWidget {
  const SimpleInfoPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (simpleShowsGuidePreview(ref)) return const _GuideInfoTiles();
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

/// Info on the guide camera: its loop's latest measurement, exposure and gain.
/// No filter or clipping: the guide frames don't carry them.
class _GuideInfoTiles extends ConsumerWidget {
  const _GuideInfoTiles();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final status = ref.watch(guideFocusProvider.select((g) => g.status));
    // A focus loop found running reports its own exposure; otherwise the
    // frames are the guider's loop, at the guider's exposure.
    final guiderMs = ref.watch(
      phd2SettingsProvider.select((p) => p.guideExposureMs),
    );
    final gain = ref.watch(
      phd2SettingsProvider.select((p) => p.guideCameraGain),
    );
    return SimpleFactTiles(
      facts: [
        (l10n.previewHfr, status.latest?.hfr.toStringAsFixed(2) ?? '—'),
        (l10n.previewStars, status.latest?.stars.toString() ?? '—'),
        (
          l10n.previewExposure,
          status.active
              ? formatExposure(
                  Duration(milliseconds: (status.exposureSec * 1000).round()),
                )
              : simpleGuiderExposureText(l10n, guiderMs),
        ),
        (l10n.deviceGain, '$gain %'),
      ],
    );
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
    fontSize: AraFontSize.simpleUnit,
    fontWeight: FontWeight.w600,
  );

  static final _box = BoxDecoration(
    color: AraColors.overlayScrim,
    border: Border.all(color: AraColors.border),
    borderRadius: BorderRadius.circular(AraRadius.simplePanel),
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
    final guide = simplePreviewGuide(ref);
    final wheel =
        ref.watch(filterWheelProvider).asData?.value?.connectionState ==
        EquipmentConnectionState.connected;
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s8,
        children: [
          // Leaving a running camera would strand it with no stop.
          ..._previewCameraRows(context, ref),
          const SimpleInfoToggle(),
          // No title: the chip that opened it says what it is (user,
          // 2026-11-04).
          if (guide) ...[
            // The shutter beside it loops the guider, at the guider's
            // exposure (#1884 review).
            const SimpleGuiderExposureTile(),
            const SimpleGuideGainSlider(),
          ] else ...[
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
            if (wheel)
              FilterDropdown(
                value: params.filterSlot,
                onChanged: ctrl.setFilterSlot,
                inline: (names, value, pick) => SimplePresetTile<String>(
                  key: const Key('simple-filter'),
                  label: context.l10n.filterDropdownLabel,
                  value: value,
                  options: names,
                  format: (n) => n,
                  onSelected: pick,
                ),
              ),
            // Switching while a loop runs would strand it with no stop.
            Container(
              decoration: BoxDecoration(
                color: AraColors.tileFill,
                borderRadius: BorderRadius.circular(AraRadius.container),
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
                        radius: AraRadius.container,
                        onTap: looping
                            ? null
                            : () =>
                                  ref.read(previewShotProvider.notifier).set(v),
                      ),
                    ),
                ],
              ),
            ),
          ],
        ],
      ),
    );
  }

  /// "Camera", then one row per connected camera: its role over its model.
  /// The guide camera is greyed out, saying why, while the guider holds it.
  List<Widget> _previewCameraRows(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final camera = ref.watch(cameraStatusProvider).asData?.value;
    final guider = ref.watch(guiderStatusProvider).value;
    final guide = simplePreviewGuide(ref);
    final guideActive = ref.watch(
      guideFocusProvider.select((g) => g.status.active),
    );
    // The guider's plain loop is Preview › Guide's own (#1875 slice 3).
    final running = guideActive || simpleGuideLooping(guider);
    // A guide loop already running keeps its row: its stop is reached there.
    final blocked =
        guideFocusBlocker(l10n, guider, active: running) ??
        (ref.watch(mainCameraBusyProvider) && !running
            ? l10n.shellMainCameraBusy
            : null);
    // Leaving Guide while its loop runs would strand it with no stop.
    final lockedMain = running ? l10n.previewStopGuideFrames : null;
    Widget row(
      PreviewCamera value,
      String id,
      String role,
      String detail,
      String? why,
    ) {
      final selected = (value == PreviewCamera.guide) == guide;
      final on = why == null;
      return Semantics(
        button: true,
        selected: selected,
        enabled: on,
        child: Material(
          color: selected ? AraColors.selectionTint : AraColors.tileFill,
          borderRadius: BorderRadius.circular(AraRadius.container),
          child: InkWell(
            key: Key('simple-preview-camera-$id'),
            borderRadius: BorderRadius.circular(AraRadius.container),
            onTap: on
                ? () => ref.read(previewCameraProvider.notifier).set(value)
                : () => simpleSayWhy(context, why),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minHeight: 48),
              child: Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: AraSpace.s12,
                  vertical: 4,
                ),
                child: Row(
                  spacing: AraSpace.s8,
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            role,
                            style: kSimpleText.copyWith(
                              color: !on && !selected
                                  ? AraColors.textDisabled
                                  : selected
                                  ? AraColors.linkText
                                  : null,
                            ),
                          ),
                          if (detail.isNotEmpty)
                            Text(detail, style: AraText.caption),
                        ],
                      ),
                    ),
                    if (selected)
                      const Icon(
                        Icons.check,
                        size: 18,
                        color: AraColors.linkText,
                      ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );
    }

    final rows = <Widget>[
      if (camera?.connectionState == EquipmentConnectionState.connected)
        row(
          PreviewCamera.main,
          'main',
          l10n.focusScopeMain,
          lockedMain ?? camera!.name,
          lockedMain,
        ),
      if (guider?.isConnected ?? false)
        row(
          PreviewCamera.guide,
          'guide',
          l10n.focusScopeGuide,
          blocked ??
              ref.watch(deviceDriverProvider('guidecamera')).value?.name ??
              '',
          blocked,
        ),
    ];
    if (rows.isEmpty) return const [];
    // The Guide shutter waits on this; Focus has the same way out.
    final waitsOnMainFocus =
        guide &&
        !guideActive &&
        ref.watch(
          phd2SettingsProvider.select((p) => p.guiderSetupType == 'oag'),
        ) &&
        !ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
    return [
      Text(
        l10n.previewCamera,
        style: kSimpleText.copyWith(color: AraColors.textSecondary),
      ),
      ...rows,
      if (waitsOnMainFocus) ...[
        Text(l10n.focusGuideMainFirst, style: AraText.caption),
        OutlinedButton(
          key: const Key('simple-preview-already-focused'),
          onPressed: () =>
              ref.read(autofocusLiveProvider.notifier).markFocusedManually(),
          child: Text(l10n.focusAlreadyInFocus),
        ),
      ],
    ];
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
    if (simplePreviewGuide(ref)) return const SimplePreviewGuideShutter();
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
  void initState() {
    super.initState();
    // Autofocus alone can't take the focus from the shell, which holds it.
    if (widget.autofocus) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _focus.requestFocus();
      });
    }
  }

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
        borderRadius: BorderRadius.circular(AraRadius.container),
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
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
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

/// A value picked on an iOS-style wheel that folds out under the tile (spun
/// with a thumb, the neighbours in view; Simple Mode has no popups). For an
/// ordered set: exposures, a wheel's filters (user, 2026-11-13: Frames'
/// wheel for all of them, mock `design/preview/simple-pickers-mock.html`).
class SimplePresetTile<T> extends StatefulWidget {
  const SimplePresetTile({
    super.key,
    required this.label,
    required this.value,
    required this.options,
    required this.format,
    required this.onSelected,
  });

  final String label;
  final T value;
  final List<T> options;
  final String Function(T) format;

  /// Null greys the tile out.
  final ValueChanged<T>? onSelected;

  @override
  State<SimplePresetTile<T>> createState() => _SimplePresetTileState<T>();
}

class _SimplePresetTileState<T> extends State<SimplePresetTile<T>> {
  bool _open = false;
  FixedExtentScrollController? _wheel;
  late T _spun;

  List<T> get _items => widget.options.contains(widget.value)
      ? widget.options
      : [widget.value, ...widget.options];

  void _toggle() => setState(() {
    _open = !_open;
    _wheel?.dispose();
    _wheel = null;
    if (_open) {
      _spun = widget.value;
      _wheel = FixedExtentScrollController(
        initialItem: _items.indexOf(widget.value),
      );
    }
  });

  void _pick(T v) {
    _toggle();
    widget.onSelected?.call(v);
  }

  @override
  void didUpdateWidget(SimplePresetTile<T> oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (_open && widget.onSelected == null) _toggle();
  }

  @override
  void dispose() {
    _wheel?.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final items = _items;
    final enabled = widget.onSelected != null;
    // Null keeps each text's own colour.
    final dim = enabled ? null : AraColors.textDisabled;
    // Ink, so the focus and press highlight paints over the fill, not under.
    final tile = ConstrainedBox(
      // At least 48 px; taller when the user's text is larger.
      constraints: const BoxConstraints(minHeight: 48),
      child: Ink(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: AraColors.tileFill,
          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              widget.label,
              style: AraText.caption.copyWith(color: dim),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
            Text(
              widget.format(widget.value),
              style: kSimpleText.copyWith(color: dim),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ],
        ),
      ),
    );
    final controller = _wheel;
    Widget label(T o) =>
        Center(child: Text(widget.format(o), style: kSimpleText));
    // Its own transparent Material: the fill and highlight paint here, over
    // the panel's scrim, not on a Material further down.
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: [
        Material(
          type: MaterialType.transparency,
          child: Semantics(
            button: true,
            expanded: _open,
            label: widget.label,
            value: widget.format(widget.value),
            enabled: enabled,
            // An InkWell, not a bare tap: Tab reaches it and Enter opens it.
            child: InkWell(
              borderRadius: BorderRadius.circular(AraRadius.simplePanel),
              onTap: enabled ? _toggle : null,
              // The label and value above say it once.
              child: ExcludeSemantics(child: tile),
            ),
          ),
        ),
        if (controller != null)
          SizedBox(
            height: 176,
            child: Column(
              children: [
                Align(
                  alignment: Alignment.centerRight,
                  child: TextButton(
                    style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
                    onPressed: () => _pick(_spun),
                    child: Text(context.l10n.previewDone),
                  ),
                ),
                Expanded(
                  // Focused on opening, so the arrows turn it at once.
                  child: SimpleWheelKeys(
                    debugLabel: 'simple-wheel-${widget.label}',
                    autofocus: true,
                    onStep: SimpleWheelKeys.stepper(controller, items.length),
                    // Each phone's own picker (user, 2026-11-13): the iOS
                    // wheel, and on Android its flat number picker with
                    // the pick between two accent lines.
                    child: Theme.of(context).platform == TargetPlatform.android
                        ? _androidWheel(controller, items, label)
                        : CupertinoPicker(
                            itemExtent: 44,
                            scrollController: controller,
                            onSelectedItemChanged: (i) => _spun = items[i],
                            children: [for (final o in items) label(o)],
                          ),
                  ),
                ),
              ],
            ),
          ),
      ],
    );
  }

  /// Android's flat number picker. ListWheelScrollView, unlike
  /// CupertinoPicker, gives a screen reader no value or swipe to step, so
  /// they are added here.
  Widget _androidWheel(
    FixedExtentScrollController controller,
    List<T> items,
    Widget Function(T) label,
  ) {
    final i = items.indexOf(_spun);
    final step = SimpleWheelKeys.stepper(controller, items.length);
    return Semantics(
      key: const Key('simple-wheel-android'),
      container: true,
      value: widget.format(_spun),
      increasedValue: i < items.length - 1 ? widget.format(items[i + 1]) : null,
      decreasedValue: i > 0 ? widget.format(items[i - 1]) : null,
      onIncrease: i < items.length - 1 ? () => step(1) : null,
      onDecrease: i > 0 ? () => step(-1) : null,
      child: ExcludeSemantics(
        child: Stack(
          children: [
            ListWheelScrollView(
              itemExtent: 44,
              controller: controller,
              physics: const FixedExtentScrollPhysics(),
              diameterRatio: 1000,
              overAndUnderCenterOpacity: 0.5,
              onSelectedItemChanged: (i) => setState(() => _spun = items[i]),
              children: [for (final o in items) label(o)],
            ),
            IgnorePointer(
              child: Center(
                child: Container(
                  height: 44,
                  decoration: const BoxDecoration(
                    border: Border.symmetric(
                      horizontal: BorderSide(
                        color: AraColors.linkText,
                        width: 1.5,
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// A count typed in its tile, with − and + beside it for one more or fewer
/// (held, they repeat): Plan's Frames (user, 2026-11-13, mock
/// `design/preview/simple-pickers-mock.html` B). Each whole number in
/// [min]..[max] typed is set at once; leaving the field empty or under [min]
/// puts the last count back.
class SimpleCountField extends StatefulWidget {
  const SimpleCountField({
    super.key,
    required this.id,
    required this.label,
    required this.value,
    required this.onChanged,
    this.min = 1,
    this.max = 9999,
  });

  /// Names the Keys: '[id]' the field, '[id]-down' and '[id]-up' the buttons.
  final String id;
  final String label;
  final int value;
  final int min, max;

  /// Null greys it out.
  final ValueChanged<int>? onChanged;

  @override
  State<SimpleCountField> createState() => _SimpleCountFieldState();
}

class _SimpleCountFieldState extends State<SimpleCountField> {
  late final _text = TextEditingController(text: '${widget.value}');
  final _focus = FocusNode();
  Timer? _repeat;

  @override
  void initState() {
    super.initState();
    _focus.addListener(() {
      if (!_focus.hasFocus) _text.text = '${widget.value}';
    });
  }

  @override
  void didUpdateWidget(SimpleCountField oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Typing keeps its own text ("0" on the way to "05"); a step shows.
    if (!_focus.hasFocus && _text.text != '${widget.value}') {
      _text.text = '${widget.value}';
    }
  }

  @override
  void dispose() {
    _stopRepeat();
    _text.dispose();
    _focus.dispose();
    super.dispose();
  }

  // A step always shows its count: a touch on − or + leaves a phone's focus
  // in the field. Typing keeps its own text.
  void _set(int n, {bool step = false}) {
    final v = n.clamp(widget.min, widget.max);
    if (v != widget.value) widget.onChanged?.call(v);
    if (step || !_focus.hasFocus) _text.text = '$v';
  }

  void _typed(String t) {
    final n = int.tryParse(t);
    if (n != null && n >= widget.min) _set(n);
  }

  Widget _button(String dir, IconData icon, String label, int step) {
    final enabled =
        widget.onChanged != null &&
        (step < 0 ? widget.value > widget.min : widget.value < widget.max);
    void go() => _set(widget.value + step, step: true);
    // The hold ends on the pointer, not the gesture: a long press already
    // won reports no end when the touch is cancelled (a call, a system swipe).
    return Listener(
      onPointerUp: (_) => _stopRepeat(),
      onPointerCancel: (_) => _stopRepeat(),
      child: GestureDetector(
        onLongPressStart: enabled
            ? (_) {
                _stopRepeat();
                _repeat = Timer.periodic(
                  const Duration(milliseconds: 90),
                  (_) => go(),
                );
              }
            : null,
        child: IconButton(
          key: Key('${widget.id}-$dir'),
          style: IconButton.styleFrom(
            backgroundColor: AraColors.tileFill,
            minimumSize: const Size(44, 48),
            padding: EdgeInsets.zero,
            tapTargetSize: MaterialTapTargetSize.shrinkWrap,
          ),
          onPressed: enabled ? go : null,
          icon: Icon(icon, semanticLabel: '${widget.label}, $label'),
        ),
      ),
    );
  }

  void _stopRepeat() {
    _repeat?.cancel();
    _repeat = null;
  }

  @override
  Widget build(BuildContext context) {
    final enabled = widget.onChanged != null;
    final dim = enabled ? null : AraColors.textDisabled;
    // Too narrow for both buttons and a readable count (a phone's Plan row),
    // the buttons give way: the field still takes a typed count.
    return LayoutBuilder(
      builder: (context, box) {
        final buttons = box.maxWidth >= 2 * 44 + 2 * AraSpace.s4 + 64;
        // A button removed mid-hold sends no pointer up to stop it.
        if (!buttons) _stopRepeat();
        return Row(
          spacing: AraSpace.s4,
          children: [
            if (buttons)
              _button('down', Icons.remove, context.l10n.deviceLess, -1),
            Expanded(child: _field(dim, enabled)),
            if (buttons) _button('up', Icons.add, context.l10n.deviceMore, 1),
          ],
        );
      },
    );
  }

  Widget _field(Color? dim, bool enabled) => TextField(
    key: Key(widget.id),
    controller: _text,
    focusNode: _focus,
    enabled: enabled,
    keyboardType: TextInputType.number,
    textInputAction: TextInputAction.done,
    inputFormatters: [
      FilteringTextInputFormatter.digitsOnly,
      LengthLimitingTextInputFormatter('${widget.max}'.length),
    ],
    style: kSimpleText.copyWith(
      color: dim,
      fontFeatures: const [FontFeature.tabularFigures()],
    ),
    decoration: InputDecoration(
      labelText: widget.label,
      prefixText: '× ',
      isDense: true,
      filled: true,
      fillColor: AraColors.tileFill,
    ),
    onChanged: _typed,
    onSubmitted: (_) => _text.text = '${widget.value}',
  );
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
        onTap:
            onPressed ??
            (why == null ? null : () => simpleSayWhy(context, why)),
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
      borderRadius: BorderRadius.circular(AraRadius.field),
    ),
  );
}

/// A status line's height (Plan's sheet starts below it).
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
    this.holds = true,
  });

  /// Stays its minimum after its state ends (#1679).
  final bool holds;

  /// Which state this is: a countdown or a reading changes only the detail.
  Object get state => (label, dot, ring, working);

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

/// One status line of the status card: tapping it opens what it's about.
/// Within one state (its label) it keeps the widest it has been, so a
/// countdown or a falling temperature doesn't make the card jitter (ALP-4937).
class _StatusPill extends StatefulWidget {
  const _StatusPill({required this.data, this.onHeight, this.onOpen});

  final _PillData data;

  /// Told the pill's height after each frame.
  final ValueChanged<double>? onHeight;
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  @override
  State<_StatusPill> createState() => _StatusPillState();
}

class _StatusPillState extends State<_StatusPill> {
  final _panelKey = GlobalKey();
  String? _widthLabel;
  double _width = 0;

  void _keepWidth() {
    if (!mounted) return;
    final size = _panelKey.currentContext?.size;
    if (size == null) return;
    widget.onHeight?.call(size.height);
    if (size.width > _width) setState(() => _width = size.width);
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
    final maxWidth = kSimpleMessagesMaxWidth;
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
            minWidth: math.min(_width, maxWidth),
            maxWidth: maxWidth,
          ),
          child: Padding(
            key: _panelKey,
            padding: const EdgeInsets.symmetric(horizontal: AraSpace.s16),
            child: Stack(
              children: [
                Container(
                  constraints: const BoxConstraints(
                    minHeight: kSimpleStatusHeight,
                  ),
                  // With the bar, the words move up so the two sit together.
                  padding: EdgeInsets.only(
                    top: 4,
                    bottom: d.footProgress == null ? 4 : 13,
                  ),
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      StatusBlink(
                        on: d.working,
                        child: StatusDot(color: d.dot, ring: d.ring),
                      ),
                      const SizedBox(width: AraSpace.s8),
                      // One text: the label and detail wrap as one sentence.
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
  const SimpleStatusPill({
    super.key,
    this.onFirstHeight,
    this.onOpen,
    this.hairlineBelow = false,
  });

  /// Messages follow in the card: a hairline closes the last line.
  final bool hairlineBelow;

  /// The top pill's height, 0 with none: a pill grows with large text.
  final ValueChanged<double>? onFirstHeight;

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

  /// The cooler went off while this pill watched (#1870): the sensor's highest
  /// reading since, until [_kWarmQuiet] passes with no rise.
  double? _warmPeak;
  bool _coolerWasOn = false;
  Timer? _warmQuiet;

  /// No ambient reading says where a sensor stops, so a minute without a
  /// rise of [_kWarmRise] does (noise at rest is under it).
  static const _kWarmQuiet = Duration(seconds: 60);
  static const _kWarmRise = 0.2;

  /// Follows the sensor up after the cooler goes off; true while it warms.
  // shortcut: an unreadable cooler reads as off (#1076), so one failed read
  // shows this pill until the next; read cooler_state_known if that is seen.
  bool _syncWarming(bool known, bool coolerOn, double? ccd) {
    if (!known || coolerOn || ccd == null) {
      _warmQuiet?.cancel();
      _warmPeak = null;
    } else if (_coolerWasOn || (_warmPeak != null && ccd - _warmPeak! >= _kWarmRise)) {
      _warmPeak = ccd;
      _warmQuiet?.cancel();
      _warmQuiet = Timer(_kWarmQuiet, () {
        if (mounted) setState(() => _warmPeak = null);
      });
    }
    _coolerWasOn = known && coolerOn;
    return _warmPeak != null;
  }

  /// The rig's own safety limits have loaded: Simple Mode never opens the
  /// policies page that loads them, and the defaults are not the rig's, so
  /// no weather breach is read from them before (#1690 audit).
  bool _policiesLoaded = false;
  bool _hydrating = false;

  /// The api the policies were (being) loaded from: another server loads again.
  ProfileApi? _loadedFor;

  Future<void> _hydratePolicies(ProfileApi api) async {
    try {
      await ref.read(safetyPoliciesProvider.notifier).hydrateFromServer(api);
      if (mounted && _loadedFor == api) setState(() => _policiesLoaded = true);
    } catch (_) {
      // Stays unloaded (nothing said beats a wrong promise); a rebuild retries.
      if (mounted && _loadedFor == api) _hydrating = false;
    }
  }

  /// The Guiding Assistant's run, followed from the guider's word that it
  /// measures until its Done pill has shown [_kAssistantDone] (#1875 slice 3).
  bool _assistantWatch = false;
  Timer? _assistantDone;
  static const _kAssistantDone = Duration(seconds: 10);

  /// The assistant's state while it is followed: null once it is not.
  GuidingAssistantStatus? _syncAssistant(bool guiderSaysAssistant) {
    if (guiderSaysAssistant) _assistantWatch = true;
    if (!_assistantWatch) return null;
    final status = ref.watch(guidingAssistantProvider).status;
    if (guiderSaysAssistant || status.isRunning) {
      _assistantDone?.cancel();
      _assistantDone = null;
    } else if (status.isDone) {
      _assistantDone ??= Timer(_kAssistantDone, () {
        if (mounted) setState(() => _assistantWatch = false);
      });
    } else {
      _assistantWatch = false;
      return null;
    }
    return status;
  }

  @override
  void dispose() {
    _ticker.dispose();
    _warmQuiet?.cancel();
    _assistantDone?.cancel();
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
        // #1747: the meridian wait in the person's words, counting down.
        : run?.meridianWaitLeft != null
        ? l10n.previewWaitingForFlip(simpleCountdown(run!.meridianWaitLeft!))
        : run?.reasonText ?? run?.currentInstructionDescription;
    final flipWait = run?.meridianWaitLeft != null;
    // A pause asked here, until the frame in hand lands (user, 2026-11-11).
    final pausing =
        ref.watch(simpleRunProvider.select((r) => r.pausing)) &&
        (run?.state?.isActive ?? false) &&
        !(run?.state?.isAnyPaused ?? false);
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
    // A cooler turned off by hand goes off at once, with no ramp: the pill
    // shows the sensor warming until it stops rising (#1870).
    final known = cam != null && cam.isConnected;
    final warming = _syncWarming(known, known && cam.coolerOn, ccd);
    // The main camera's own exposure (#1755): its progress fills a plan
    // frame's bar, and an exposure nothing here explains gets its own pill.
    final camExposing = cam != null && cam.isConnected && cam.runtimeState == 'exposing';
    final camPct = cam?.exposureProgressPct;
    final camUnexplained = cam != null &&
        cam.isConnected &&
        cam.isExposing &&
        !ref.watch(mainCameraExplainedProvider);
    // A park or unpark asked for in Simple Mode, until the mount gets there.
    final parking = ref.watch(simpleMountMotionProvider);
    // Home asked in the Mount window, from the tap (#1649).
    final homing = ref.watch(simpleMountHomeProvider);
    // The Mount window's Check or Center under way: the main camera takes a
    // frame for it, so it has a pill as any frame does (#1650).
    final pointing = ref.watch(simplePointingProvider.select((p) => p.phase));
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
    final profileApi = ref.watch(profileApiProvider);
    if (profileApi != _loadedFor) {
      _loadedFor = profileApi;
      _hydrating = false;
      _policiesLoaded = false;
    }
    if (!_hydrating && profileApi != null) {
      _hydrating = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _hydratePolicies(profileApi);
      });
    }
    final weather = ref.watch(weatherProvider).value;
    final breach =
        _policiesLoaded && policies.weatherTriggersEnabled && weather != null
        ? simpleWeatherBreaches(l10n, weather, policies).firstOrNull
        : null;
    // The focuser moving, whoever moved it; autofocus has its own pill.
    final focuser = ref.watch(focuserProvider).value;
    final settingPort = ref.watch(switchActingProvider)
        ? ref.watch(simpleSwitchSettingProvider)
        : null;
    // The guider's routine, or Guiding with its RMS, when nothing else is on.
    final guider = ref.watch(guiderStatusProvider).value;
    final guiding = guider?.isCorrecting ?? false;
    final guiderOn = guider?.isBusy ?? false;
    final guiderAsk = ref.watch(simpleGuiderPendingProvider);
    final assistant = _syncAssistant(
      guider?.runtimeState == GuiderRuntimeState.assistant,
    );
    final assistantRunning = assistant != null &&
        (assistant.isRunning ||
            guider?.runtimeState == GuiderRuntimeState.assistant);
    final planActive = run?.state?.isActive ?? false;
    final planCalibrating = planActive &&
        guider != null &&
        guider.runtimeState == GuiderRuntimeState.calibrating;
    final polar = ref.watch(polarAlignLiveProvider);
    final af = ref.watch(autofocusLiveProvider).run;
    final bahtinov = ref.watch(
      bahtinovFocusProvider.select((b) => b.status.active),
    );
    final guideFocus = ref.watch(
      guideFocusProvider.select((g) => g.status.active),
    );
    final offsets = ref.watch(
      filterOffsetCalibrationProvider.select((o) => o.calibration),
    );
    // #1757 — the calibration run Simple Mode started, else a Guide Camera
    // build (its frames, once the rig reports them).
    final calibrating = simpleCalibrationProgress(ref);
    final builds = ref.watch(guiderBuildActivityProvider);
    final building = [
      for (final a in CalibrationArtifact.values)
        if (builds[a] case final b? when b.phase == CalibrationBuildPhase.building)
          (a, b),
    ].firstOrNull;
    // One pill per thing happening, stacked in this order (user, 2026-11-04:
    // a frame hid the guider). Each says it once when it changes, not each
    // second of a countdown, a calibration step or an RMS update.
    final pills = <_PillData>[
      if (stopping)
        _PillData(
          SimpleStatusOrigin.preview,
          l10n.previewStopping,
          null,
          AraColors.textSecondary,
        )
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
        )
      // Another client's frame: exposing is working, downloading is changing
      // (DESIGN.md status rule); no bar when the camera reports none.
      else if (camUnexplained)
        camExposing
            ? _PillData(
                SimpleStatusOrigin.mainCameraExposure,
                l10n.deviceCamera,
                l10n.deviceExposing,
                AraColors.accentConnected,
                working: true,
                shotProgress: camPct == null ? null : (camPct / 100).clamp(0.0, 1.0),
                spoken: l10n.deviceCamera,
              )
            : _PillData(
                SimpleStatusOrigin.mainCameraExposure,
                l10n.deviceCamera,
                l10n.deviceDownloading,
                AraColors.accentInfo,
                ring: true,
                spoken: l10n.deviceCamera,
              ),
      // A running plan says what it's doing (cooling, slewing, centring,
      // focusing), with how far the step has got along the pill's foot.
      // #1596: a finished run that imaged nothing keeps its reason on the pill.
      // A running plan's autofocus has the Focus pill alone; this one returns
      // after it (#1875 slice 3). Paused or pausing, it stays: it answers the
      // person.
      if ((planActive || run?.reasonText != null) &&
          !(af.isRunning && planActive && !paused && !pausing) &&
          step != null &&
          step.isNotEmpty)
        _PillData(
          SimpleStatusOrigin.plan,
          paused ? l10n.previewPlanPaused : l10n.previewPlan,
          pausing
              ? l10n.planPausingAfterFrame
              // The Guider's calibration inside a plan: its leg on this pill,
              // and no Guider pill of its own (#1875 slice 3).
              : planCalibrating
              ? switch (guider.calibrationText(l10n)) {
                  final leg? => l10n.previewPlanGuiderCalibratingLeg(leg),
                  null => l10n.previewPlanGuiderCalibrating,
                }
              : step,
          // DESIGN.md status rule: running is working (green); paused by Ara
          // is on its way back (blue); paused waiting for the user needs a
          // look (amber).
          // Waiting out the meridian is on its way between two states: a
          // blue ring, not imaging (#1747).
          run!.state == SequenceRunState.pausedAwaitingUser || !planActive
              ? AraColors.accentBusy
              : paused || flipWait || pausing
              ? AraColors.accentInfo
              : AraColors.accentConnected,
          ring:
              (paused && run.state != SequenceRunState.pausedAwaitingUser) ||
              ((flipWait || pausing) && planActive),
          working: planActive && !paused && !flipWait && !pausing,
          // A step that reports none (an exposure) follows the camera's
          // own frame; a paused plan shows no frame (#1755).
          footProgress:
              run.currentInstructionProgress ??
              (planActive && !paused && camExposing && camPct != null
                  ? (camPct / 100).clamp(0.0, 1.0)
                  : null),
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
          // Moving is working: blinking green, as the Dome's (user, 2026-11-11).
          l10n.deviceMount,
          parking ? l10n.deviceParking : l10n.deviceUnparking,
          AraColors.accentConnected,
          working: true,
        )
      else if (homing)
        _PillData(
          SimpleStatusOrigin.mount,
          l10n.deviceMount,
          l10n.deviceFindingHome,
          AraColors.accentConnected,
          working: true,
        ),
      if (pointing == SimplePointingPhase.solving ||
          pointing == SimplePointingPhase.centring)
        _PillData(
          SimpleStatusOrigin.mount,
          l10n.deviceMount,
          pointing == SimplePointingPhase.solving
              ? l10n.pointingSolving
              : l10n.pointingCentring,
          AraColors.accentConnected,
          working: true,
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
          !af.isRunning &&
          !(offsets?.isRunning ?? false))
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
      // A cooler turned off by hand (a plan's own warm-up shows as its step).
      if (warming && !planActive)
        _PillData(
          SimpleStatusOrigin.camera,
          l10n.previewWarming,
          simpleCelsius(ccd!),
          AraColors.accentInfo,
          ring: true,
          spoken: l10n.previewWarming,
        ),
      // Every mode's routine has a pill while it runs; a failure is a message
      // on the left that fades, posted by the shell (user, 2026-11-04).
      // The offset calibration's autofocus runs are its own: one pill, the
      // filter and which run of how many (#1675).
      if (calibrating != null)
        _PillData(
          SimpleStatusOrigin.calibration,
          l10n.shellModeCalibration,
          l10n.calibrationPillProgress(calibrating.done, calibrating.total),
          AraColors.accentConnected,
          working: true,
          spoken: l10n.shellModeCalibration,
          footProgress: calibrating.total > 0
              ? (calibrating.done / calibrating.total).clamp(0.0, 1.0)
              : null,
        )
      else if (building case (final artifact, final b)?)
        _PillData(
          SimpleStatusOrigin.calibration,
          artifact == CalibrationArtifact.darkLibrary
              ? l10n.calibrationGuideDarks
              : l10n.calibrationGuideDefect,
          switch ((b.exposureIndex, b.exposureCount, b.frame, b.frameCount)) {
            (final ei?, final ec?, final f?, final fc?) when ec > 0 && fc > 0 =>
              l10n.calibrationGuideBuildProgress((ei - 1) * fc + f, ec * fc),
            _ => l10n.calibrationGuideBuilding,
          },
          AraColors.accentConnected,
          working: true,
          spoken: l10n.calibrationGuideBuilding,
          footProgress: b.fraction,
        ),
      if (offsets != null && offsets.isRunning)
        _PillData(
          SimpleStatusOrigin.focus,
          l10n.focusOffsetsTitle,
          offsets.runIndex == 0
              ? l10n.focusOffsetsStarting
              : l10n.focusOffsetsProgress(
                  offsets.currentName ??
                      l10n.deviceSlot(offsets.currentPosition ?? 0),
                  offsets.runIndex,
                  offsets.runCount,
                ),
          AraColors.accentConnected,
          working: true,
          spoken: l10n.focusOffsetsTitle,
          footProgress: offsets.runCount > 0
              ? ((offsets.runIndex - 1).clamp(0, offsets.runCount) /
                    offsets.runCount)
              : null,
        )
      else if (af.isRunning)
        _PillData(
          SimpleStatusOrigin.focus,
          l10n.shellModeFocus,
          MainFocusCard.phaseText(l10n, af),
          AraColors.accentInfo,
          ring: true,
          spoken: l10n.shellModeFocus,
          footProgress: af.totalSteps > 0
              ? af.completedSteps / af.totalSteps
              : null,
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
      // The Guiding Assistant measuring, time left on its bar; its Done pill
      // stays 10 s (#1875 slice 3).
      else if (assistant != null)
        assistantRunning
            ? _PillData(
                SimpleStatusOrigin.guide,
                l10n.guideToolAssistant,
                switch ((assistant.durationSec, assistant.elapsedSec)) {
                  (final d?, final e?) when assistant.state == 'measuring' =>
                    l10n.guideAssistantLeft(simpleCountdown((d - e).ceil())),
                  _ => l10n.guideToolMeasuring,
                },
                AraColors.accentConnected,
                working: true,
                spoken: l10n.guideToolAssistant,
                footProgress: assistant.progress,
              )
            : _PillData(
                SimpleStatusOrigin.guide,
                l10n.guideToolAssistant,
                l10n.guideToolDone(
                  assistant.recommendations?.suggestions.length ?? 0,
                ),
                AraColors.accentConnected,
                spoken: l10n.guideToolAssistant,
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
      else if (guiderOn && !planCalibrating)
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
    // A Preflight step with nothing of its own running says it waits for the
    // person; once its routine starts, that routine's pill speaks for it.
    final preflight = ref.watch(simplePreflightProvider);
    if (preflight != null) {
      final origin = switch (preflight) {
        SimplePreflightStep.plan => SimpleStatusOrigin.plan,
        SimplePreflightStep.focus => SimpleStatusOrigin.focus,
        SimplePreflightStep.polarAlign => SimpleStatusOrigin.polar,
        SimplePreflightStep.guide => SimpleStatusOrigin.guide,
      };
      if (!pills.any((d) => d.origin == origin)) {
        pills.add(
          _PillData(
            origin,
            l10n.shellModePreflight,
            l10n.previewPreflightWaiting(preflight.label(l10n)),
            AraColors.accentBusy,
            // The next step's pill takes over; two steps never show at once.
            holds: false,
          ),
        );
      }
    }
    return _StatusPillStack(
      pills: pills,
      onFirstHeight: widget.onFirstHeight,
      onOpen: widget.onOpen,
      hairlineBelow: widget.hairlineBelow,
    );
  }
}

/// How long a status pill stays at the least (#1679), and its way out.
const _kPillMinimum = Duration(seconds: 1);
const _kPillFade = Duration(milliseconds: 200);

/// One kind of pill the stack shows: its origin's latest words, kept up after
/// its state ends until the minimum is over.
class _KeptPill {
  _KeptPill(this.data, this.minimum);

  _PillData data;
  Timer minimum;
  bool live = true;
  bool fading = false;
}

/// The status lines, one per kind. A line stays at least [_kPillMinimum], in
/// its slot, then fades; a new line of its kind takes over at once.
class _StatusPillStack extends StatefulWidget {
  const _StatusPillStack({
    required this.pills,
    this.onFirstHeight,
    this.onOpen,
    this.hairlineBelow = false,
  });

  /// Messages follow in the card: a hairline closes the last line.
  final bool hairlineBelow;

  final ValueChanged<double>? onFirstHeight;

  final List<_PillData> pills;
  final ValueChanged<SimpleStatusOrigin>? onOpen;

  @override
  State<_StatusPillStack> createState() => _StatusPillStackState();
}

class _StatusPillStackState extends State<_StatusPillStack> {
  final _kept = <SimpleStatusOrigin, _KeptPill>{};
  var _order = <SimpleStatusOrigin>[];

  Timer _minimum(SimpleStatusOrigin origin) => Timer(_kPillMinimum, () {
    final kept = _kept[origin];
    if (!mounted || kept == null || kept.live) return;
    setState(() => _leave(origin, kept));
  });

  void _leave(SimpleStatusOrigin origin, _KeptPill kept) {
    if (MediaQuery.disableAnimationsOf(context) || !kept.data.holds) {
      kept.minimum.cancel();
      _kept.remove(origin);
    } else {
      kept.fading = true;
    }
  }

  void _sync() {
    for (final d in widget.pills) {
      final kept = _kept[d.origin];
      if (kept == null) {
        _kept[d.origin] = _KeptPill(d, _minimum(d.origin));
        continue;
      }
      final newState = kept.data.state != d.state;
      kept
        ..data = d
        ..live = true;
      // Each state gets its own minimum, a takeover included.
      if (kept.fading || newState) {
        kept.minimum.cancel();
        kept
          ..fading = false
          ..minimum = _minimum(d.origin);
      }
    }
    final live = {for (final d in widget.pills) d.origin};
    for (final origin in _kept.keys.toList()) {
      final kept = _kept[origin]!;
      if (live.contains(origin) || !kept.live) continue;
      kept.live = false;
      if (!kept.data.holds || !kept.minimum.isActive) _leave(origin, kept);
    }
  }

  @override
  void initState() {
    super.initState();
    _sync();
  }

  @override
  void didUpdateWidget(_StatusPillStack oldWidget) {
    super.didUpdateWidget(oldWidget);
    _sync();
  }

  @override
  void dispose() {
    for (final kept in _kept.values) {
      kept.minimum.cancel();
    }
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final pills = [for (final d in widget.pills) _kept[d.origin]!];
    for (final (i, origin) in _order.indexed) {
      final kept = _kept[origin];
      if (kept != null && !kept.live) {
        pills.insert(math.min(i, pills.length), kept);
      }
    }
    _order = [for (final k in pills) k.data.origin];
    if (pills.isEmpty) {
      WidgetsBinding.instance.addPostFrameCallback(
        (_) => widget.onFirstHeight?.call(0),
      );
      return const SizedBox.shrink();
    }
    // Every line shows, in the stack's order; a hairline under each, the last
    // one only when messages follow in the card.
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final (i, k) in pills.indexed) ...[
          AnimatedOpacity(
            key: ValueKey(k.data.origin),
            opacity: k.fading ? 0 : 1,
            duration: _kPillFade,
            curve: Curves.easeOut,
            onEnd: () {
              if (k.fading) setState(() => _kept.remove(k.data.origin));
            },
            child: _StatusPill(
              data: k.data,
              onHeight: i == 0 ? widget.onFirstHeight : null,
              onOpen: widget.onOpen,
            ),
          ),
          if (i < pills.length - 1 || widget.hairlineBelow)
            const SimpleCardHairline(),
        ],
      ],
    );
  }
}
