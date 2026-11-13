import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/guider/guiding_assistant_state.dart';
import '../../state/guider/guider_live_state.dart';
import '../../models/guiding_assistant.dart';
import '../../models/guider_status.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/main_camera_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import '../../widgets/status_indicator.dart'
    show StatusBlink, StatusDot, StatusLevel;
import '../../util/friendly_error.dart';
import 'simple_preflight.dart'
    show SimplePreflightStep, simplePreflightProvider, simplePreflightNext;
import 'simple_device_windows.dart'
    show
        SimpleGuideGainSlider,
        simpleSaveGuider,
        simpleSliderRow,
        simpleWindowRow,
        simpleWindowStepper;
import 'simple_preview_panel.dart'
    show
        SimplePresetTile,
        SimpleShutterButton,
        formatExposure,
        kSimpleText;
import 'simple_shell.dart' show SimplePanel, SimpleSegments, kSimpleGoStyle;
import '../../widgets/adaptive_controls.dart';

/// Guide mode: the guider's routine (loop, pick a star, calibrate when
/// needed, settle, guide) runs from the shutter; its red square stops it.

String _arcsec(double v) => '${v.toStringAsFixed(2)}″';

/// A Start (true) or Stop (false) asked for and not shown by the guider yet:
/// said at once, since the guider takes seconds (a stop waits out its frame).
class SimpleGuiderPending extends Notifier<bool?> {
  @override
  bool? build() => null;
  void set(bool? v) => state = v;
}

final simpleGuiderPendingProvider =
    NotifierProvider<SimpleGuiderPending, bool?>(SimpleGuiderPending.new);

/// The Guider panel's open tab: 0 Guide, 1 Tuning, 2 Tools (#1722).
class SimpleGuiderTab extends Notifier<int> {
  @override
  int build() => 0;
  void set(int v) => state = v;
}

final simpleGuiderTabProvider =
    NotifierProvider<SimpleGuiderTab, int>(SimpleGuiderTab.new);

/// The guider's dot: the top bar's colour (a lost star amber, settling blue),
/// grey without a guider.
Color simpleGuiderColour(GuiderStatus? g) => g == null || !g.isConnected
    ? AraColors.textDisabled
    : guiderStatusLevel(g).color;


/// The rail chip in Guide mode: the RMS while it guides, else Off.
String simpleGuideSummary(WidgetRef ref) {
  final g = ref.watch(guiderStatusProvider).value;
  final rms = g?.rmsTotalArcsec;
  return (g?.isCorrecting ?? false) && rms != null
      ? _arcsec(rms)
      : ref.context.l10n.deviceOff;
}

class SimpleGuideShutter extends ConsumerWidget {
  const SimpleGuideShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final g = ref.watch(guiderStatusProvider).value;
    final pending = ref.watch(simpleGuiderPendingProvider);
    // An ask in flight shows as done already, and takes no second tap.
    final active = pending ?? (g?.isBusy ?? false);
    final cameraBusy = ref.watch(mainCameraTakingFramesProvider);
    final l10n = context.l10n;
    return SimpleShutterButton(
      start: true,
      running: active,
      label: pending != null
          ? (pending ? l10n.guideStarting : l10n.guideStopping)
          : active
          ? l10n.guideStop
          : l10n.guideStart,
      whyOff: pending != null
          ? null
          : g == null || !g.isConnected
          ? l10n.guideConnectFirst
          : l10n.shellMainCameraBusy,
      // Calibration turns the mount: no start under a main-camera frame.
      onPressed:
          g == null ||
              !g.isConnected ||
              pending != null ||
              (!active && cameraBusy)
          ? null
          : () => simpleGuideRun(context, start: !active),
    );
  }
}

/// Starts (the whole routine) or stops guiding; a refusal says why in a pill.
Future<void> simpleGuideRun(
  BuildContext context, {
  required bool start,
}) async {
  final notify = userNotifier(context, origin: SimpleStatusOrigin.guide);
  final l10n = context.l10n;
  // The container, not ref or context: a mode change unmounts the shutter
  // mid-ask, and the pending state must still clear.
  final container = ProviderScope.containerOf(context, listen: false);
  final guider = container.read(guiderStatusProvider.notifier);
  final pending = container.read(simpleGuiderPendingProvider.notifier);
  pending.set(start);
  try {
    await (start ? guider.startGuiding() : guider.stopGuiding());
    // The guider answers the ask at once and acts a moment later: read again
    // every half second until it shows, 10 s at most.
    for (var i = 0; i < 20; i++) {
      if ((container.read(guiderStatusProvider).value?.isBusy ?? false) ==
          start) {
        break;
      }
      await Future<void>.delayed(const Duration(milliseconds: 500));
      await guider.refresh();
    }
    // The rig accepts a Stop and may then refuse it (ALP-5018): say so, or the
    // shutter just flips back to running.
    final g = container.read(guiderStatusProvider).value;
    if (!start && (g?.isBusy ?? false)) {
      notify(
        // Lower case inside the sentence, as the guiding strip writes the phase.
        l10n.guideStopRefused(guiderRuntimeText(l10n, g!.runtimeState).toLowerCase()),
        error: true,
        snackBarRed: false,
      );
    }
  } catch (e) {
    notify(
      friendlyError(
        l10n,
        e,
        action: start ? l10n.guideActionStart : l10n.guideActionStop,
      ),
      error: true,
      snackBarRed: false,
    );
  } finally {
    pending.set(null);
  }
}

/// Guide mode's panel: what the guider is doing, the RMS, and how often
/// the plans dither.
class SimpleGuidePanel extends ConsumerWidget {
  const SimpleGuidePanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final g = ref.watch(guiderStatusProvider).value;
    final connected = g != null && g.isConnected;
    final active = g?.isBusy ?? false;
    final guiding = g?.isCorrecting ?? false;
    final pending = ref.watch(simpleGuiderPendingProvider);
    final colour = pending != null
        ? AraColors.accentInfo
        : simpleGuiderColour(g);
    final phd2 = ref.watch(phd2SettingsProvider);
    final tab = ref.watch(simpleGuiderTabProvider);
    final every = phd2.ditherEnabled ? math.max(1, phd2.ditherEveryNFrames) : 0;
    final total = guiding ? g!.rmsTotalArcsec : null;
    final ra = guiding ? g!.rmsRaArcsec : null;
    final dec = guiding ? g!.rmsDecArcsec : null;
    // Preflight's Guide step, the last before imaging: its Done starts it.
    final preflightDone =
        guiding &&
        ref.watch(simplePreflightProvider) == SimplePreflightStep.guide &&
        simplePreflightNext(ref, after: SimplePreflightStep.guide) == null;
    return SimplePanel(
      key: const Key('simple-guide-panel'),
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s16,
        vertical: AraSpace.s12,
      ),
      child: SizedBox(
        width: 340,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 4,
          children: [
            Row(
              spacing: AraSpace.s8,
              children: [
                StatusBlink(
                  on: pending == null && guiding,
                  child: StatusDot(
                    color: colour,
                    ring:
                        pending != null ||
                        (connected &&
                            guiderStatusLevel(g) == StatusLevel.changing),
                  ),
                ),
                Expanded(
                  child: Text(
                    !connected
                        ? l10n.guideNoGuider
                        : pending != null
                        ? (pending ? l10n.guideStarting : l10n.guideStopping)
                        : active
                        ? g.runtimeSummary(l10n)
                        : l10n.guideIdle,
                    style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                  ),
                ),
              ],
            ),
            if (connected && !active && pending == null)
              Text(g.outOfStepReason ?? l10n.guideTapShutter, style: AraText.caption),
            if (!connected)
              Text(l10n.guideConnectFirst, style: AraText.caption),
            if (preflightDone)
              FilledButton(
                key: const Key('simple-guide-preflight-done'),
                style: kSimpleGoStyle,
                onPressed: ref.read(simplePreflightProvider.notifier).next,
                child: Text(l10n.preflightDoneStartImaging),
              ),
            _SimpleGuiderTabs(selected: tab),
            // Guide stays laid out, so the panel keeps one height across tabs;
            // Tools lies over it and scrolls inside when its suggestion list runs longer.
            Stack(
              fit: StackFit.passthrough,
              children: [
            IndexedStack(
              index: tab == 2 ? null : tab,
              sizing: StackFit.loose,
              children: [
                Column(
                  mainAxisSize: MainAxisSize.min,
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: 4,
                  children: [
            simpleWindowRow(
              l10n.deviceRms,
              id: 'RMS',
              Column(
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  Text(
                    total == null ? '—' : _arcsec(total),
                    style: kSimpleText.copyWith(
                      fontSize: AraFontSize.simpleReadingLarge,
                      fontWeight: FontWeight.w300,
                    ),
                  ),
                  if (ra != null && dec != null)
                    Text(
                      l10n.deviceRmsRaDec(_arcsec(ra), _arcsec(dec)),
                      style: AraText.caption,
                    ),
                ],
              ),
            ),
            // The guide camera's exposure, picked as Preview's; set live.
            SimplePresetTile<int>(
              key: const Key('simple-guide-exposure'),
              label: l10n.previewExposure,
              value: phd2.guideExposureMs,
              options: [
                for (final ms in guideExposureChoicesMs)
                  if (ms >= 200 && ms <= 15000) ms,
              ],
              format: (ms) => ms <= 0
                  ? l10n.guideExposureOwn
                  : formatExposure(Duration(milliseconds: ms)),
              onSelected: (ms) => simpleSaveGuider(
                context,
                ref,
                (p) => p.setGuideExposureMs(ms),
                live: true,
              ),
            ),
            const SimpleGuideGainSlider(oneLine: true),
            Text(l10n.guideAggressiveness, style: AraText.caption),
            _SimpleAggressiveness(
              id: 'ra',
              axis: l10n.guideAxisRa,
              label: l10n.guideRaAggressiveness,
              value: phd2.raAggressiveness,
              set: (p, v) => p.setRaAggressiveness(v),
            ),
            _SimpleAggressiveness(
              id: 'dec',
              axis: l10n.guideAxisDec,
              label: l10n.guideDecAggressiveness,
              value: phd2.decAggressiveness,
              set: (p, v) => p.setDecAggressiveness(v),
            ),
            // Four named choices: one row of segments, no list to open.
            Text(l10n.guideDecMode, style: AraText.caption),
            SimpleSegments<String>(
              key: const Key('simple-guide-dec-mode'),
              keyPrefix: 'simple-guide-dec-mode',
              group: l10n.guideDecMode,
              shrinkToFit: true,
              selected: phd2.decGuideMode,
              options: [
                for (final m in const ['auto', 'north', 'south', 'off'])
                  (m, _decModeName(l10n, m) ?? l10n.guideDecAuto, m),
              ],
              onPick: (m) => simpleSaveGuider(
                context,
                ref,
                (p) => p.setDecGuideMode(m),
                live: true,
              ),
            ),
            simpleWindowRow(
              l10n.deviceDither,
              id: 'Dither',
              simpleWindowStepper(
                id: 'simple-guider-dither',
                text: switch (every) {
                  0 => l10n.deviceOff,
                  _ => l10n.deviceDitherEveryFrames(every),
                },
                onLess: () => _setDither(context, ref, every - 1),
                onMore: () => _setDither(context, ref, every + 1),
              ),
            ),
                  ],
                ),
                tab == 1 ? const _SimpleGuiderTuning() : const SizedBox.shrink(),
              ],
            ),
                if (tab == 2)
                  const Positioned.fill(
                    child: SingleChildScrollView(child: _SimpleGuiderTools()),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  /// 0 turns dithering off; 1 and up dithers every that many frames.
  static void _setDither(BuildContext context, WidgetRef ref, int n) {
    if (n < 0 || n > 20) return;
    _save(context, ref, (p) {
      p.setDitherEnabled(n > 0);
      if (n > 0) p.setDitherEveryNFrames(n);
    });
  }

  static Future<void> _save(
    BuildContext context,
    WidgetRef ref,
    void Function(Phd2SettingsNotifier) change,
  ) => simpleSaveGuider(context, ref, change);
}

class _SimpleGuiderTabs extends ConsumerWidget {
  const _SimpleGuiderTabs({required this.selected});

  final int selected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    return SimpleSegments<int>(
      keyPrefix: 'simple-guider-tab',
      shrinkToFit: true,
      group: l10n.guideTabsGroup,
      options: [
        (0, l10n.guideTabGuide, 'guide'),
        (1, l10n.guideTabTuning, 'tuning'),
        (2, l10n.guideTabTools, 'tools'),
      ],
      selected: selected,
      onPick: ref.read(simpleGuiderTabProvider.notifier).set,
    );
  }
}

/// The Guider's numbers that are worth changing only when guiding is poor.
/// Minimum move reaches the Guider live; settle applies from the next dither.
class _SimpleGuiderTuning extends ConsumerWidget {
  const _SimpleGuiderTuning();

  // In hundredths, so repeated taps don't drift by floating-point error.
  static double _stepped(double v, double by) => ((v + by) * 100).round() / 100;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final phd2 = ref.watch(phd2SettingsProvider);
    final px = phd2.ditherPixels;
    // Label beside a narrower stepper: this tab has six of them to fit on an iPad.
    // Read whole, because "For" or "RA" means nothing without its group.
    Widget row(String label, Widget stepper, {required String id, String? group}) => Semantics(
      container: true,
      explicitChildNodes: true,
      label: group == null ? label : '$group, $label',
      child: Row(
        key: Key('simple-window-row-$id'),
        spacing: AraSpace.s8,
        children: [
          Expanded(child: ExcludeSemantics(child: Text(label, style: kSimpleText))),
          SizedBox(width: 176, child: stepper),
        ],
      ),
    );
    void live(void Function(Phd2SettingsNotifier) change) =>
        simpleSaveGuider(context, ref, change, live: true);
    void save(void Function(Phd2SettingsNotifier) change) =>
        simpleSaveGuider(context, ref, change);
    Widget minMove(String axis, String label, String id, double v, void Function(Phd2SettingsNotifier, double) set) =>
        row(
          axis,
          id: label,
          group: l10n.guideMinimumMoveName,
          simpleWindowStepper(
            name: '${l10n.guideMinimumMoveName}, $axis',
            id: 'simple-guider-min-move-$id',
            text: '${v.toStringAsFixed(2)} px',
            onLess: () {
              if (v > 0) live((p) => set(p, math.max(0, _stepped(v, -0.05))));
            },
            onMore: () => live((p) => set(p, _stepped(v, 0.05))),
          ),
        );
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: 2,
      children: [
        Text(l10n.guideMinimumMove, style: AraText.caption),
        minMove(l10n.guideAxisRa, 'RA minimum move', 'ra', phd2.minimumMove, (p, v) => p.setMinimumMove(v)),
        minMove(l10n.guideAxisDec, 'Dec minimum move', 'dec', phd2.decMinimumMove, (p, v) => p.setDecMinimumMove(v)),
        if (phd2.ditherEnabled)
          row(
            l10n.deviceDitherBy,
            id: 'Dither by',
            simpleWindowStepper(
              id: 'simple-guider-dither-px',
              name: l10n.deviceDitherBy,
              text: '${px == px.roundToDouble() ? px.round() : px} px',
              onLess: () {
                if (px > 1) save((p) => p.setDitherPixels(px - 1));
              },
              onMore: () {
                if (px < 30) save((p) => p.setDitherPixels(px + 1));
              },
            ),
          ),
        Text(l10n.guideSettle, style: AraText.caption),
        row(
          l10n.guideSettleWithin,
          id: 'Settle within',
          group: l10n.guideSettle,
          simpleWindowStepper(
            id: 'simple-guider-settle-px',
            name: '${l10n.guideSettle}, ${l10n.guideSettleWithin}',
            text: '${phd2.settlePixels.toStringAsFixed(1)} px',
            onLess: () {
              if (phd2.settlePixels > 0.1) save((p) => p.setSettlePixels(_stepped(phd2.settlePixels, -0.1)));
            },
            onMore: () => save((p) => p.setSettlePixels(_stepped(phd2.settlePixels, 0.1))),
          ),
        ),
        row(
          l10n.guideSettleFor,
          id: 'Settle for',
          group: l10n.guideSettle,
          simpleWindowStepper(
            id: 'simple-guider-settle-time',
            name: '${l10n.guideSettle}, ${l10n.guideSettleFor}',
            text: '${phd2.settleTimeSec} s',
            onLess: () {
              if (phd2.settleTimeSec > 1) save((p) => p.setSettleTimeSec(phd2.settleTimeSec - 1));
            },
            onMore: () => save((p) => p.setSettleTimeSec(phd2.settleTimeSec + 1)),
          ),
        ),
        row(
          l10n.guideSettleGiveUp,
          id: 'Settle give up',
          group: l10n.guideSettle,
          simpleWindowStepper(
            id: 'simple-guider-settle-timeout',
            name: '${l10n.guideSettle}, ${l10n.guideSettleGiveUp}',
            text: '${phd2.settleTimeoutSec} s',
            onLess: () {
              if (phd2.settleTimeoutSec > 5) save((p) => p.setSettleTimeoutSec(phd2.settleTimeoutSec - 5));
            },
            onMore: () => save((p) => p.setSettleTimeoutSec(phd2.settleTimeoutSec + 5)),
          ),
        ),
      ],
    );
  }
}

/// Recalibrate and the Guiding Assistant: one row each (name, what it does,
/// where it stands, a quiet button).
class _SimpleGuiderTools extends ConsumerStatefulWidget {
  const _SimpleGuiderTools();

  @override
  ConsumerState<_SimpleGuiderTools> createState() => _SimpleGuiderToolsState();
}

class _SimpleGuiderToolsState extends ConsumerState<_SimpleGuiderTools> {
  // Forgetting a calibration or picking a star takes seconds, before the
  // guider's own state shows anything: a second tap must wait.
  bool _preparing = false;
  // The waiting suggestions the user unticked; a new run ticks them all again.
  final Set<String> _unticked = {};

  Future<void> _recalibrate() async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.guide);
    final container = ProviderScope.containerOf(context, listen: false);
    // Held across the await: nothing else watches it here, so autoDispose would lose the error.
    final keep = container.listen(guiderLiveProvider, (_, _) {});
    setState(() => _preparing = true);
    final l10n = context.l10n;
    try {
      await container.read(guiderLiveProvider.notifier).clearCalibration();
      if (container.read(guiderLiveProvider).error case final error?) {
        notify(failureReason(l10n, error), error: true, snackBarRed: false);
        return;
      }
    } finally {
      keep.close();
      if (mounted) setState(() => _preparing = false);
    }
    if (mounted) await simpleGuideRun(context, start: true);
  }

  /// The assistant needs a calibration and a star held in a loop; the shutter
  /// never stops at that state, so this tap gets there itself.
  Future<void> _runAssistant() async {
    final notify = userNotifier(context, origin: SimpleStatusOrigin.guide);
    final l10n = context.l10n;
    final container = ProviderScope.containerOf(context, listen: false);
    final keep = container.listen(guiderLiveProvider, (_, _) {});
    setState(() => _preparing = true);
    GuiderLiveState live() => container.read(guiderLiveProvider);
    bool starHeld() => live().live.isLooping && live().live.hasLock;
    try {
      final notifier = container.read(guiderLiveProvider.notifier);
      await notifier.refresh();
      if (live().calibration.data?.calibrated != true) {
        notify(l10n.guideAssistantCalibrateFirst, error: true, snackBarRed: false);
        return;
      }
      if (!starHeld()) {
        await notifier.findStar();
        await notifier.refresh();
      }
      if (!starHeld()) {
        notify(guiderLiveFailure(l10n, live()) ?? l10n.guideAssistantNoStar,
            error: true, snackBarRed: false);
        return;
      }
      await container.read(guidingAssistantProvider.notifier).start(measureBacklash: true);
    } finally {
      keep.close();
      if (mounted) setState(() => _preparing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    ref.listen(guidingAssistantProvider, (previous, next) {
      if (next.status.isRunning || (next.status.isDone && previous?.status.isDone != true)) {
        _unticked.clear();
      }
      // A run failing now, not the last run's result found on opening this tab.
      final runFailed = previous != null && previous.status.isRunning && next.status.isFailed;
      final problem = next.error != null && next.error != previous?.error
          ? failureReason(l10n, next.error!)
          : runFailed
          ? next.status.error
          : null;
      if (problem != null) {
        userNotifier(context, origin: SimpleStatusOrigin.guide)(problem, error: true, snackBarRed: false);
      }
    });
    final g = ref.watch(guiderStatusProvider).value;
    final connected = g != null && g.isConnected;
    final active = g?.isBusy ?? false;
    final guiding = g?.stopEndsPhase ?? false;
    final pending = ref.watch(simpleGuiderPendingProvider);
    final cameraBusy = ref.watch(mainCameraTakingFramesProvider);
    final planRunning =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final assistant = ref.watch(guidingAssistantProvider);
    final notifier = ref.read(guidingAssistantProvider.notifier);
    final status = assistant.status;
    final done = ref.watch(guidingAssistantAppliedProvider);
    final suggestions = [
      if (status.isDone)
        for (final s in status.recommendations?.suggestions ?? const <GuidingAssistantSuggestion>[])
          if (!done.contains(s.key)) s,
    ];
    final ticked = [
      for (final s in suggestions)
        if (!_unticked.contains(s.key)) s.key,
    ];
    final calibration = ref.watch(
      guiderLiveProvider.select((s) => s.calibration.data),
    );
    final calibrated = calibration?.calibrated == true;
    // One reason that blocks both tools shows once, above the rows.
    final shared = planRunning
        ? l10n.guideStopPlanFirst
        : cameraBusy
        ? l10n.shellMainCameraBusy
        : guiding
        ? l10n.guideStopGuidingFirst
        : null;
    final measuring = status.isRunning;
    // A loop stops Recalibrate only: the Assistant needs one, so it goes on that row.
    // While the Assistant measures the runtime is busy too, but stopping it would end the run.
    final loop = shared == null && active && !measuring ? l10n.guideStopLoopFirst : null;
    final canRun = connected && shared == null && pending == null;
    final dec = calibration?.declinationDeg;
    final calibrationLine = !calibrated
        ? l10n.guideToolNotCalibrated
        : dec == null
        ? l10n.guideToolCalibrated
        // Sign from the rounded value, so -0.4 reads 0°, not -0°.
        : l10n.guideToolCalibratedAt(
            '${dec.round() > 0 ? '+' : ''}${dec.round()}°',
          );
    final waiting = suggestions.length;
    final failed = status.isFailed;
    final String assistantLine = measuring
        ? l10n.guideToolMeasuring
        : failed
        ? l10n.guideToolFailed(
            status.error ?? l10n.guideAssistantFailedNoReason,
          )
        : status.isDone
        ? l10n.guideToolDone(waiting)
        : !calibrated
        ? l10n.guideToolCalibrateFirst
        : l10n.guideToolNotRun;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (shared != null)
          Padding(
            padding: const EdgeInsets.only(bottom: AraSpace.s8),
            child: _ToolStatus(
              key: const Key('simple-guider-tools-refusal'),
              color: AraColors.accentWarning,
              text: shared,
              warning: true,
            ),
          ),
        _ToolRow(
          title: l10n.guideToolCalibration,
          what: l10n.guideToolCalibrationWhat,
          statusText: calibrationLine,
          button: _toolButton(
            key: const Key('simple-guider-recalibrate'),
            label: l10n.guideToolRecalibrate,
            semantics: l10n.guideRecalibrateNow,
            onPressed: canRun && !active && !_preparing && !measuring
                ? _recalibrate
                : null,
          ),
          status: _ToolStatus(
            key: const Key('simple-guider-calibration-status'),
            color: calibrated
                ? AraColors.accentConnected
                : AraColors.accentInfo,
            ring: !calibrated,
            text: calibrationLine,
          ),
          extra: loop == null
              ? null
              : _ToolStatus(
                  color: AraColors.accentWarning,
                  text: loop,
                  warning: true,
                ),
        ),
        const Divider(height: 1, thickness: 1, color: AraColors.border),
        _ToolRow(
          title: l10n.guideToolAssistant,
          what: l10n.guideToolAssistantWhat,
          statusText: assistantLine,
          button: _toolButton(
            key: const Key('simple-guider-assistant'),
            label: measuring
                ? l10n.guideToolStop
                : status.isDone || failed
                ? l10n.guideToolRunAgain
                : l10n.guideToolRun,
            semantics: measuring
                ? l10n.guideStopAssistant
                : l10n.guideRunAssistant,
            onPressed: assistant.busy
                ? null
                : measuring
                ? notifier.stop
                : canRun && !guiding && !_preparing && calibrated
                ? _runAssistant
                : null,
          ),
          status: _ToolStatus(
            key: const Key('simple-guider-assistant-status'),
            color: measuring || status.isDone
                ? AraColors.accentConnected
                : failed || !calibrated
                ? AraColors.accentWarning
                : AraColors.accentInfo,
            ring: !measuring && !status.isDone && !failed && calibrated,
            blink: measuring,
            warning: failed || (!calibrated && !measuring && !status.isDone),
            // Stays while the last run's failure stands, so leaving the tab mid-run loses nothing.
            expandable: failed,
            text: assistantLine,
          ),
          extra: waiting == 0
              ? null
              : Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    for (final s in suggestions)
                      // Its own ink layer: the panel paints a background over the nearest Material.
                      Material(
                        type: MaterialType.transparency,
                        child: CheckboxListTile(
                          key: Key('ga-suggest-${s.key}'),
                          controlAffinity: ListTileControlAffinity.leading,
                          contentPadding: EdgeInsets.zero,
                          visualDensity: VisualDensity.compact,
                          activeColor: AraColors.accentConnected,
                          checkColor: AraColors.bgPrimary,
                          value: !_unticked.contains(s.key),
                          onChanged: assistant.busy
                              ? null
                              : (v) => setState(() {
                                  if (v == true) {
                                    _unticked.remove(s.key);
                                  } else {
                                    _unticked.add(s.key);
                                  }
                                }),
                          title: Text(_suggestionName(l10n, s), style: kSimpleText),
                          secondary: Text(_suggestionValue(l10n, s), style: kSimpleText),
                        ),
                      ),
                    FilledButton(
                      style: kSimpleGoStyle,
                      onPressed: assistant.busy || ticked.isEmpty
                          ? null
                          : () => notifier.apply(ticked),
                      child: Text(l10n.guideAssistantApply(ticked.length)),
                    ),
                  ],
                ),
        ),
      ],
    );
  }
}

/// A suggestion's setting name in the user's language; the model's names are English.
String _suggestionName(AppLocalizations l10n, GuidingAssistantSuggestion s) => switch (s.key) {
  'exposure' => l10n.guideSuggestExposure,
  'min_move_ra' => l10n.guideSuggestMinMoveRa,
  'min_move_dec' => l10n.guideSuggestMinMoveDec,
  'dec_backlash' => l10n.guideSuggestDecBacklash,
  'dec_guide_mode' => l10n.guideDecMode,
  'dec_algorithm' => l10n.guideSuggestDecAlgorithm,
  _ => s.label,
};

/// A suggestion's value; the guider names its Dec guide mode in English ("North").
String _suggestionValue(AppLocalizations l10n, GuidingAssistantSuggestion s) => switch (s.key) {
  'dec_guide_mode' => _decModeName(l10n, s.value.toLowerCase()) ?? s.value,
  _ => s.value,
};

/// A Dec guide mode (auto, north, south, off) in the user's language; null for any other word.
String? _decModeName(AppLocalizations l10n, String mode) => switch (mode) {
  'north' => l10n.guideDecNorth,
  'south' => l10n.guideDecSouth,
  'off' => l10n.deviceOff,
  'auto' => l10n.guideDecAuto,
  _ => null,
};

/// One tool: its name and quiet button, what it does, where it stands (and
/// anything extra under it, such as a refusal, or the suggestions and Apply).
class _ToolRow extends StatelessWidget {
  const _ToolRow({
    required this.title,
    required this.what,
    required this.button,
    required this.status,
    required this.statusText,
    this.extra,
  });

  final String title;
  final String what;
  final String statusText;
  final Widget button;
  final Widget status;
  final Widget? extra;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: AraSpace.s8),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: [
        // One spoken node per row (name, what it does, where it stands) on the
        // title; the lines below are drawn but left out of the tree.
        Row(
          spacing: AraSpace.s12,
          children: [
            Expanded(
              child: Semantics(
                container: true,
                header: true,
                label: '$title. $what $statusText',
                excludeSemantics: true,
                child: Text(
                  title,
                  style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                ),
              ),
            ),
            button,
          ],
        ),
        ExcludeSemantics(child: Text(what, style: AraText.caption)),
        ExcludeSemantics(child: status),
        if (extra != null)
          Padding(
            padding: const EdgeInsets.only(top: AraSpace.s4),
            child: extra,
          ),
      ],
    ),
  );
}

/// A tool's quiet button: outlined with a Hairline border, 44 pt tall. The
/// spoken name is the whole action, since "Run" alone says nothing.
Widget _toolButton({
  required Key key,
  required String label,
  required String semantics,
  required VoidCallback? onPressed,
}) => OutlinedButton(
  key: key,
  style: OutlinedButton.styleFrom(
    minimumSize: const Size(104, 44),
    foregroundColor: AraColors.textPrimary,
    disabledForegroundColor: AraColors.textDisabled,
    side: const BorderSide(color: AraColors.border),
  ),
  onPressed: onPressed,
  child: Semantics(label: semantics, excludeSemantics: true, child: Text(label)),
);

/// A status line: a dot (a ring for blue, blinking while a tool works) and the
/// words, two lines at most so Tools keeps Guide's height. [expandable] shows
/// the rest in place on a tap; [warning] writes the words in amber.
class _ToolStatus extends StatefulWidget {
  const _ToolStatus({
    super.key,
    required this.color,
    required this.text,
    this.ring = false,
    this.blink = false,
    this.warning = false,
    this.expandable = false,
  });

  final Color color;
  final String text;
  final bool ring;
  final bool blink;
  final bool warning;
  final bool expandable;

  @override
  State<_ToolStatus> createState() => _ToolStatusState();
}

class _ToolStatusState extends State<_ToolStatus> {
  bool _whole = false;

  @override
  Widget build(BuildContext context) {
    final line = Row(
      spacing: AraSpace.s8,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 3),
          child: StatusBlink(
            on: widget.blink,
            child: StatusDot(color: widget.color, ring: widget.ring),
          ),
        ),
        Expanded(
          child: Text(
            widget.text,
            maxLines: _whole ? null : 2,
            overflow: _whole ? null : TextOverflow.ellipsis,
            style: AraText.caption.copyWith(
              color: widget.warning ? widget.color : AraColors.textPrimary,
            ),
          ),
        ),
      ],
    );
    if (!widget.expandable) return line;
    // Sighted tap only: the row's one semantics label already reads the whole reason.
    return InkWell(
      key: const Key('simple-guider-assistant-failed'),
      borderRadius: BorderRadius.circular(AraRadius.container),
      onTap: () => setState(() => _whole = !_whole),
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 44),
        child: line,
      ),
    );
  }
}

/// How strongly the guider corrects one axis: 0-100 % in 5s, set when the
/// thumb is let go and sent to the guider live, so guiding goes on.
class _SimpleAggressiveness extends ConsumerStatefulWidget {
  const _SimpleAggressiveness({
    required this.id,
    required this.axis,
    required this.label,
    required this.value,
    required this.set,
  });

  final String id;
  final String axis;
  final String label;
  final double value;
  final void Function(Phd2SettingsNotifier, double) set;

  @override
  ConsumerState<_SimpleAggressiveness> createState() =>
      _SimpleAggressivenessState();
}

class _SimpleAggressivenessState extends ConsumerState<_SimpleAggressiveness> {
  double? _drag;

  @override
  Widget build(BuildContext context) {
    final value = _drag ?? widget.value;
    final percent = '${(value * 100).round()} %';
    return simpleSliderRow(
      context,
      widget.axis,
      // Merged: Slider's own node carries the value as its label.
      MergeSemantics(
        child: Semantics(
          label: widget.label,
          child: AdaptiveSlider(
            key: Key('simple-guide-aggressiveness-${widget.id}'),
            min: 0,
            max: 1,
            divisions: 20,
            value: value.clamp(0, 1),
            label: percent,
            onChanged: (v) => setState(() => _drag = v),
            onChangeEnd: (v) {
              setState(() => _drag = null);
              simpleSaveGuider(
                context,
                ref,
                (p) => widget.set(p, v),
                live: true,
              );
            },
          ),
        ),
      ),
      percent,
    );
  }
}
