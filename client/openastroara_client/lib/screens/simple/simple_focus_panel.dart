import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/autofocus_run.dart';
import '../../models/equipment_device_status.dart';
import '../../models/filter_offset_calibration.dart';
import '../../models/filter_wheel_status.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/focus/filter_offset_calibration_state.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../models/focuser_status.dart';
import '../../widgets/focus/bahtinov_focus_card.dart'
    show BahtinovFocusCard, bahtinovHint, formatOffset;
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../state/focus/guide_focus_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/imaging/main_camera_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../widgets/focus/guide_focus_card.dart'
    show GuideFocusCard, TurnAdvice, guideFocusBlocker, guideFocusHint;
import '../../widgets/focus/v_curve_chart.dart';
import '../../state/setup/setup_readiness.dart';
import 'simple_preflight.dart'
    show SimplePreflightStep, simplePreflightProvider, simplePreflightNext;
import 'simple_preview_panel.dart'
    show SimpleInfoToggle, SimpleShutterButton, kSimpleText;
import 'simple_device_windows.dart' show simpleCelsius, simpleSigned;
import 'simple_shell.dart'
    show SimplePanel, SimpleSegments, kSimpleGoStyle, simpleStackText;
import '../../util/friendly_error.dart';

/// The HFR to show big: the confirmed one after a run, the latest kept probe
/// while one runs, else nothing. Pure — unit-tested.
double? simpleFocusHfr(AutofocusRun run) {
  if (run.isComplete && (run.finalHfr ?? 0) > 0) return run.finalHfr;
  final kept = run.sweepProbes.where((p) => p.kept && p.hfr > 0);
  return kept.isEmpty ? null : kept.last.hfr;
}

/// "Auto", "Manual", "Offsets" or "Guide": the rail chip's caption in Focus
/// mode. For the main telescope the method is the one Smart Focus would use:
/// the user's pick, else autofocus with a focuser connected, by hand without
/// (as `MainFocusCard` decides).
String simpleFocusSummary(WidgetRef ref) =>
    ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide
    ? ref.context.l10n.focusGuide
    : ref.watch(simpleFocusOffsetsProvider)
    ? ref.context.l10n.focusOffsets
    : ref.watch(effectiveFocusMethodProvider) == MainFocusMethod.autofocus
    ? ref.context.l10n.focusAuto
    : ref.context.l10n.focusManual;

/// Which camera Focus focuses: the main telescope's, or the guide camera's
/// (always by hand, watching its HFR). Session only; the main one to start.
enum SimpleFocusScope { main, guide }

class SimpleFocusScopeNotifier extends Notifier<SimpleFocusScope> {
  @override
  SimpleFocusScope build() {
    // A guide loop outlives the app: found running, Focus goes where its stop
    // is (the choice is locked while it reads, so Main would strand it).
    ref.listen(guideFocusProvider.select((g) => g.status.active), (_, active) {
      if (active) state = SimpleFocusScope.guide;
    });
    return ref.read(guideFocusProvider).status.active
        ? SimpleFocusScope.guide
        : SimpleFocusScope.main;
  }

  void set(SimpleFocusScope scope) => state = scope;
}

final simpleFocusScopeProvider =
    NotifierProvider<SimpleFocusScopeNotifier, SimpleFocusScope>(
      SimpleFocusScopeNotifier.new,
    );

/// #1675 — the main telescope's routine row: Auto and Manual are the focus
/// method; Offsets measures each filter's focus offset.
enum SimpleFocusRoutine { auto, manual, offsets }

/// Whether the Offsets routine is picked (session only; off to start). A
/// calibration found on the rig, running or with results waiting, picks it.
class SimpleFocusOffsetsNotifier extends Notifier<bool> {
  @override
  bool build() {
    ref.listen(
      filterOffsetCalibrationProvider.select((l) => l.calibration != null),
      (_, found) {
        if (found) state = true;
      },
    );
    return ref.read(filterOffsetCalibrationProvider).calibration != null;
  }

  void set(bool picked) => state = picked;
}

final simpleFocusOffsetsProvider =
    NotifierProvider<SimpleFocusOffsetsNotifier, bool>(
      SimpleFocusOffsetsNotifier.new,
    );

/// Search's Focus offsets hit: the main telescope's Offsets routine, unless
/// a focus routine runs (the row is locked then).
void simpleOpenFocusOffsets(WidgetRef ref) {
  final busy =
      ref.read(autofocusLiveProvider).run.isRunning ||
      ref.read(bahtinovFocusProvider).status.active ||
      ref.read(guideFocusProvider).status.active;
  if (busy) return;
  ref.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.main);
  ref.read(simpleFocusOffsetsProvider.notifier).set(true);
}

/// The filters the calibration will measure: the user's picks, else the
/// daemon's defaults, always with the reference (which can't be unticked).
/// Returns the picks and the reference slot (null with no named slot).
(Set<int>, int?) simpleOffsetPicks(WidgetRef ref) {
  final slots = ref.watch(filterWheelProvider).value?.slots ?? const [];
  final picks =
      ref.watch(filterOffsetCalibrationProvider.select((l) => l.picks)) ??
      defaultOffsetPicks(slots);
  final reference = offsetReference(
    slots,
    picks,
    ref.watch(autofocusSettingsProvider.select((s) => s.afFilter)),
  );
  return ({...picks, ?reference}, reference);
}

/// Why the Offsets routine can't start now, in words; null when it can. A
/// plan or another focus routine first, then a missing or unsuitable device,
/// then the picks (the daemon checks the same and says why on a 409).
String? simpleOffsetsBlocker(WidgetRef ref, AppLocalizations l10n) {
  final plan =
      ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
  if (plan) return l10n.focusOffsetsPlanRunning;
  final focusing =
      ref.watch(autofocusLiveProvider.select((l) => l.run.isRunning)) ||
      ref.watch(bahtinovFocusProvider.select((b) => b.status.active)) ||
      ref.watch(guideFocusProvider.select((g) => g.status.active));
  if (focusing) return l10n.focusOffsetsFocusBusy;
  final camera = ref.watch(cameraStatusProvider).value;
  if (!(camera?.isConnected ?? false)) return l10n.focusOffsetsNeedsCamera;
  final focuser = ref.watch(focuserProvider).value;
  if (!(focuser?.isConnected ?? false)) return l10n.focusOffsetsNeedsFocuser;
  final wheel = ref.watch(filterWheelProvider).value;
  if (!(wheel?.isConnected ?? false)) return l10n.focusOffsetsNeedsWheel;
  if (wheel!.manual) return l10n.focusOffsetsNeedsMotorisedWheel;
  if (!(focuser!.capabilities?.absoluteFocuser ?? false)) {
    return l10n.focusOffsetsNeedsAbsoluteFocuser;
  }
  final (picks, reference) = simpleOffsetPicks(ref);
  if (reference == null || picks.length < 2) return l10n.focusOffsetsPickOne;
  return null;
}

/// The guide camera's live-focus exposures, and the one picked (2 s to
/// start), as the full UI's card offers them.
const kSimpleGuideExposures = [1.0, 2.0, 3.0, 5.0];

class SimpleGuideExposure extends Notifier<double> {
  @override
  double build() => 2;

  void set(double seconds) => state = seconds;
}

final simpleGuideExposureProvider =
    NotifierProvider<SimpleGuideExposure, double>(SimpleGuideExposure.new);

/// Whether the focus run's frame, its measured stars circled, fills the screen
/// behind Focus's panels (session only; off to start).
class SimpleFocusStarsShown extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool shown) => state = shown;
}

final simpleFocusStarsShownProvider =
    NotifierProvider<SimpleFocusStarsShown, bool>(SimpleFocusStarsShown.new);

/// The Bahtinov readout's exposures in Simple Mode, and the one picked
/// (shared by the panel and the shutter; 2 s to start).
const kSimpleBahtinovExposures = [1.0, 2.0, 4.0, 8.0];

class SimpleBahtinovExposure extends Notifier<double> {
  @override
  double build() => 2;

  void set(double seconds) => state = seconds;
}

final simpleBahtinovExposureProvider =
    NotifierProvider<SimpleBahtinovExposure, double>(
      SimpleBahtinovExposure.new,
    );

/// The rail's shutter in Focus follows the chip: on Auto it starts autofocus
/// and its red square cancels it; on Manual it starts the Bahtinov readout
/// and its square stops it.
class SimpleFocusShutter extends ConsumerWidget {
  const SimpleFocusShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final cameraBusy = ref.watch(mainCameraBusyProvider);
    if (ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide) {
      return const SimpleGuideLiveShutter();
    }
    if (ref.watch(simpleFocusOffsetsProvider)) {
      // Offsets: starts the calibration; its square cancels it.
      final offsets = ref.watch(filterOffsetCalibrationProvider);
      final running = offsets.isRunning;
      final notifier = ref.read(filterOffsetCalibrationProvider.notifier);
      final blocker = simpleOffsetsBlocker(ref, context.l10n);
      final (picks, _) = simpleOffsetPicks(ref);
      return SimpleShutterButton(
        start: true,
        running: running,
        label: running
            ? context.l10n.focusOffsetsCancel
            : context.l10n.focusOffsetsStart,
        whyOff: offsets.busy
            ? null
            : blocker ?? context.l10n.shellMainCameraBusy,
        onPressed: offsets.busy || (!running && (blocker != null || cameraBusy))
            ? null
            : running
            ? notifier.cancel
            : () => notifier.start(picks.toList()..sort()),
      );
    }
    if (ref.watch(effectiveFocusMethodProvider) != MainFocusMethod.autofocus) {
      // Manual: starts and stops the Bahtinov readout.
      final bahtinov = ref.watch(bahtinovFocusProvider);
      final active = bahtinov.status.active;
      final notifier = ref.read(bahtinovFocusProvider.notifier);
      return SimpleShutterButton(
        start: true,
        running: active,
        label: active
            ? context.l10n.focusStopBahtinov
            : context.l10n.focusStartBahtinov,
        whyOff: bahtinov.busy ? null : context.l10n.shellMainCameraBusy,
        onPressed: bahtinov.busy || (!active && cameraBusy)
            ? null
            : active
            ? notifier.stop
            : () => notifier.start(
                exposureSec: ref.read(simpleBahtinovExposureProvider),
              ),
      );
    }
    final live = ref.watch(autofocusLiveProvider);
    final connected =
        ref.watch(focuserProvider).asData?.value?.connectionState ==
        EquipmentConnectionState.connected;
    final running = live.run.isRunning;
    final notifier = ref.read(autofocusLiveProvider.notifier);
    return SimpleShutterButton(
      start: true,
      running: running,
      label: running
          ? context.l10n.focusCancelAutofocus
          : context.l10n.focusAutofocus,
      onPressed: live.busy || !(running || connected) || (!running && cameraBusy)
          ? null
          : running
          ? notifier.cancel
          : notifier.start,
      whyOff: live.busy
          ? null
          : !connected
          ? context.l10n.focusNeedsFocuser
          : context.l10n.shellMainCameraBusy,
    );
  }
}

/// The guide camera's shutter, in Focus and Preview: starts and stops its live
/// frames through the guider, once it can.
class SimpleGuideLiveShutter extends ConsumerWidget {
  const SimpleGuideLiveShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final guide = ref.watch(guideFocusProvider);
    final active = guide.status.active;
    final notifier = ref.read(guideFocusProvider.notifier);
    final blocked =
        guideFocusBlocker(
          context.l10n,
          ref.watch(guiderStatusProvider).asData?.value,
          active: active,
        ) !=
        null;
    final gated =
        ref.watch(
          phd2SettingsProvider.select((p) => p.guiderSetupType == 'oag'),
        ) &&
        !ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
    return SimpleShutterButton(
      start: true,
      running: active,
      label: active
          ? context.l10n.focusStopGuide
          : context.l10n.focusStartGuide,
      whyOff: guide.busy
          ? null
          : gated
          ? context.l10n.guideFocusWaitingMain
          : guideFocusBlocker(
              context.l10n,
              ref.watch(guiderStatusProvider).asData?.value,
              active: active,
            ),
      onPressed: guide.busy || (!active && (blocked || gated))
          ? null
          : active
          ? notifier.stop
          : () => notifier.start(
              exposureSec: ref.read(simpleGuideExposureProvider),
            ),
    );
  }
}

/// Focus mode's panel beside the right rail: where the run stands, the HFR,
/// the V-curve once there are probes and the focuser position (the Focuser
/// window's dial moves it). The shutter starts autofocus.
class SimpleFocusPanel extends ConsumerStatefulWidget {
  const SimpleFocusPanel({super.key});

  @override
  ConsumerState<SimpleFocusPanel> createState() => _SimpleFocusPanelState();
}

class _SimpleFocusPanelState extends ConsumerState<SimpleFocusPanel> {
  // The mask-off question, asked in the panel (Simple Mode has no popups):
  // by Finish, or by leaving Manual before finishing ([_then] switches once
  // the mask is off).
  bool _askMask = false;
  VoidCallback? _then;

  /// Leaving Manual with a measurement not finished asks first.
  void _leaveManual(BahtinovFocusLive bahtinov, VoidCallback leave) {
    if (bahtinov.status.hasMeasurement && !bahtinov.finished) {
      setState(() {
        _askMask = true;
        _then = leave;
      });
    } else {
      leave();
    }
  }

  @override
  Widget build(BuildContext context) {
    final live = ref.watch(autofocusLiveProvider);
    final bahtinov = ref.watch(bahtinovFocusProvider);
    final method = ref.watch(effectiveFocusMethodProvider);
    final guide = ref.watch(guideFocusProvider);
    final scope = ref.watch(simpleFocusScopeProvider);
    final offsets = ref.watch(filterOffsetCalibrationProvider);
    final offsetsPicked = ref.watch(simpleFocusOffsetsProvider);
    final locked =
        live.run.isRunning ||
        bahtinov.status.active ||
        bahtinov.busy ||
        offsets.isRunning ||
        offsets.busy;
    final anyRunning = locked || guide.status.active || guide.busy;
    final run = live.run;
    final focuser = ref.watch(focuserProvider).asData?.value;
    final connected =
        focuser?.connectionState == EquipmentConnectionState.connected;
    final (headline, headlineColor) = MainFocusCard.headlineFor(
      context.l10n,
      run,
    );
    final hfr = simpleFocusHfr(run);
    final l10n = context.l10n;
    final fit = run.fit;
    final best = run.finalPosition ?? fit?.bestPosition.round();
    // The full UI's verdict, cut to what to do (the panel has no room for
    // the why).
    final weak = MainFocusCard.weakFitText(l10n, run) == null
        ? null
        : l10n.focusWeakFitShort(fit!.rSquared.toStringAsFixed(2));
    final starsShown = ref.watch(simpleFocusStarsShownProvider);
    final error = live.error != null
        ? failureReason(l10n, live.error!)
        : (run.isFailed ? MainFocusCard.failureText(context.l10n, run) : null);
    // Measured stars On / Off: the run's frame, stars circled, full screen.
    final stars = SimpleSegments<bool>(
      keyPrefix: 'simple-focus-stars',
      expand: false,
      options: [(true, l10n.deviceOn, 'on'), (false, l10n.deviceOff, 'off')],
      selected: starsShown,
      onPick: (on) => ref.read(simpleFocusStarsShownProvider.notifier).set(on),
    );
    // Preflight with focus the last step (a permanent pier, or aligned
    // already): its Done starts imaging, never the run's end on its own.
    // Auto or Manual, whichever focused.
    // Its room is kept until then, so the panel holds one height through the run.
    final preflightDone =
        ref.watch(simplePreflightProvider) == SimplePreflightStep.focus &&
            simplePreflightNext(ref, after: SimplePreflightStep.focus) == null
        ? _keepRoom(
            visible: ref.watch(setupFocusStateProvider) == SetupStepState.done,
            child: FilledButton(
              key: const Key('simple-focus-preflight-done'),
              style: kSimpleGoStyle,
              onPressed: ref.read(simplePreflightProvider.notifier).next,
              child: Text(l10n.preflightDoneStartImaging),
            ),
          )
        : null;
    return SimplePanel(
      // 12 above and below, and 4 between rows (6 before 2026-11-04): with
      // the Auto / Manual row under Main / Guide it still fits an iPad.
      padding: const EdgeInsets.symmetric(
        horizontal: AraSpace.s16,
        vertical: AraSpace.s12,
      ),
      child: SizedBox(
        width: 400,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 4,
          children: [
            // Which camera, then under it how to focus the main one (the
            // guide camera has no focuser: no Auto / Manual row).
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 4,
              children: [
                SimpleSegments<SimpleFocusScope>(
                  keyPrefix: 'simple-focus-scope',
                  options: [
                    (SimpleFocusScope.main, l10n.focusScopeMain, 'main'),
                    (SimpleFocusScope.guide, l10n.focusScopeGuide, 'guide'),
                  ],
                  selected: scope,
                  onPick: anyRunning
                      ? null
                      : (sc) {
                          void pick() => ref
                              .read(simpleFocusScopeProvider.notifier)
                              .set(sc);
                          sc == SimpleFocusScope.guide &&
                                  method == MainFocusMethod.bahtinov
                              ? _leaveManual(bahtinov, pick)
                              : pick();
                        },
                ),
                // The method: Smart Focus with the focuser, or by hand with a
                // Bahtinov mask; or Offsets, which measures each filter's
                // focus offset (#1675). Locked while a run goes.
                if (scope == SimpleFocusScope.main)
                  SimpleSegments<SimpleFocusRoutine>(
                    keyPrefix: 'simple-focus-method',
                    options: [
                      (SimpleFocusRoutine.auto, l10n.focusAuto, 'auto'),
                      (SimpleFocusRoutine.manual, l10n.focusManual, 'manual'),
                      (SimpleFocusRoutine.offsets, l10n.focusOffsets, 'offsets'),
                    ],
                    selected: offsetsPicked
                        ? SimpleFocusRoutine.offsets
                        : method == MainFocusMethod.autofocus
                        ? SimpleFocusRoutine.auto
                        : SimpleFocusRoutine.manual,
                    onPick: locked
                        ? null
                        : (r) {
                            void pick() {
                              ref
                                  .read(simpleFocusOffsetsProvider.notifier)
                                  .set(r == SimpleFocusRoutine.offsets);
                              if (r != SimpleFocusRoutine.offsets) {
                                ref
                                    .read(mainFocusMethodProvider.notifier)
                                    .pick(
                                      r == SimpleFocusRoutine.auto
                                          ? MainFocusMethod.autofocus
                                          : MainFocusMethod.bahtinov,
                                    );
                              }
                            }

                            r != SimpleFocusRoutine.manual &&
                                    method == MainFocusMethod.bahtinov &&
                                    !offsetsPicked
                                ? _leaveManual(bahtinov, pick)
                                : pick();
                          },
                  ),
              ],
            ),
            const SimpleInfoToggle(),
            if (scope == SimpleFocusScope.guide)
              ..._guide(l10n, guide)
            else if (_askMask)
              ..._maskQuestion(l10n)
            else if (offsetsPicked)
              ..._offsets(l10n, offsets, live.run)
            else if (method == MainFocusMethod.bahtinov) ...[
              ..._bahtinov(l10n, bahtinov, connected ? focuser : null),
              ?preflightDone,
            ] else ...[
              // The headline shares its row with Measured stars when there is
              // room, so the panel still fits an iPad under its two choice rows.
              if (live.frame != null && !simpleStackText(context))
                Row(
                  spacing: AraSpace.s8,
                  children: [
                    Expanded(
                      child: _twoLines(
                        headline,
                        kSimpleText.copyWith(color: headlineColor),
                      ),
                    ),
                    Text(l10n.focusMeasuredStars, style: AraText.caption),
                    stars,
                  ],
                )
              else
                _twoLines(
                  headline,
                  kSimpleText.copyWith(color: headlineColor),
                ),
              // One slot, so the run's end swaps the HFR for its figures in place.
              _slot(context, _kFocusReadoutHeight, [
                // A finished run's Best position tile says where it is.
                if (connected && focuser != null && !run.isComplete)
                  Text(
                    [
                      if (focuser.position != null)
                        l10n.focusFocuserAt('${focuser.position}'),
                      if (focuser.temperature != null)
                        simpleCelsius(focuser.temperature!),
                      if (focuser.name.isNotEmpty) focuser.name,
                    ].join(' · '),
                    style: AraText.caption,
                  ),
                // A finished run's headline already says its HFR.
                if (!run.isComplete)
                  Text(
                    context.l10n.focusHfr(hfr?.toStringAsFixed(2) ?? '—'),
                    style: kSimpleText.copyWith(
                      fontSize: AraFontSize.simpleReadingLarge,
                      fontWeight: FontWeight.w300,
                    ),
                  ),
                // A finished run's figures, all on view (Simple Mode folds
                // nothing away).
                if (run.isComplete)
                  Row(
                    spacing: AraSpace.s4,
                    children: [
                      _figure(
                        l10n.focusBestPosition,
                        best == null ? '—' : '$best',
                      ),
                      _figure(
                        l10n.focusStars,
                        run.finalStars == null ? '—' : '${run.finalStars}',
                      ),
                      _figure(
                        l10n.focusFitR2,
                        fit == null ? '—' : fit.rSquared.toStringAsFixed(2),
                      ),
                    ],
                  ),
              ]),
              ?preflightDone,
              // There from the start, with its waiting text; a warning or an
              // error takes its room from the chart.
              SizedBox(
                height: _kFocusChartHeight,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: 4,
                  children: [
                    Expanded(child: VCurveChart(run: run)),
                    // Icon + colour + text (DESIGN.md): the tint alone all but
                    // vanishes in night mode.
                    if (weak != null && error == null)
                      Container(
                        key: const Key('simple-focus-weak-fit'),
                        padding: const EdgeInsets.all(AraSpace.s8),
                        decoration: BoxDecoration(
                          color: AraColors.accentWarning.withValues(alpha: 0.12),
                          borderRadius: BorderRadius.circular(AraRadius.simplePanel),
                        ),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          spacing: AraSpace.s8,
                          children: [
                            const Icon(
                              Icons.warning_amber_rounded,
                              size: 18,
                              color: AraColors.accentWarning,
                            ),
                            Expanded(
                              child: Text(
                                weak,
                                style: AraText.caption.copyWith(
                                  color: AraColors.accentWarning,
                                ),
                                maxLines: 2,
                                overflow: TextOverflow.ellipsis,
                              ),
                            ),
                          ],
                        ),
                      ),
                    // Whole, however long: up to half the slot, then it scrolls.
                    if (error != null)
                      ConstrainedBox(
                        constraints: const BoxConstraints(
                          maxHeight: _kFocusChartHeight / 2,
                        ),
                        child: SingleChildScrollView(
                          child: Text(
                            error,
                            style: kSimpleText.copyWith(
                              color: AraColors.errorText,
                            ),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
              // The run's frame with its measured stars circled, full screen
              // behind the panels; off to start.
              if (simpleStackText(context))
                _keepRoom(
                  visible: live.frame != null,
                  child: Row(
                  spacing: AraSpace.s12,
                  children: [
                    Expanded(
                      child: Text(l10n.focusMeasuredStars, style: kSimpleText),
                    ),
                    stars,
                  ],
                  ),
                ),
              // The Focuser window's dial moves it; nothing to nudge here.
              if (!connected)
                Text(
                  context.l10n.focusNoFocuser,
                  style: kSimpleText.copyWith(color: AraColors.textSecondary),
                ),
            ],
          ],
        ),
      ),
    );
  }

  /// Offsets (#1675): before a run, the reference and the filter picker; while
  /// it runs, the filter being measured and its live curve; once it ends, the
  /// results with Save and Discard. The shutter starts and stops it.
  List<Widget> _offsets(
    AppLocalizations l10n,
    FilterOffsetLive live,
    AutofocusRun run,
  ) {
    final cal = live.calibration;
    final error = live.error == null ? null : failureReason(l10n, live.error!);
    final errorText = error == null
        ? null
        : Text(error, style: kSimpleText.copyWith(color: AraColors.errorText));
    if (cal != null && cal.isRunning) {
      final name = cal.currentName ??
          (cal.currentPosition == null ? null : l10n.deviceSlot(cal.currentPosition!));
      return [
        _twoLines(
          name == null || cal.runIndex == 0
              ? l10n.focusOffsetsStarting
              : l10n.focusOffsetsMeasuring(name, cal.runIndex, cal.runCount),
          kSimpleText.copyWith(color: AraColors.accentConnected),
        ),
        LinearProgressIndicator(
          value: cal.runCount == 0
              ? null
              : ((cal.runIndex - 1).clamp(0, cal.runCount) / cal.runCount),
        ),
        SizedBox(
          height: _kFocusChartHeight,
          child: VCurveChart(run: run),
        ),
        ?errorText,
      ];
    }
    if (cal != null) {
      final measured = cal.filters.any(
        (f) => f.isMeasured && f.position != cal.referencePosition,
      );
      final drift = cal.referenceFirstPosition != null &&
              cal.referenceSecondPosition != null
          ? (cal.referenceFirstPosition! - cal.referenceSecondPosition!).abs()
          : null;
      return [
        Text(
          switch (cal.state) {
            'complete' => l10n.focusOffsetsResults,
            'cancelled' => l10n.focusOffsetsStopped,
            _ => l10n.focusOffsetsFailed,
          },
          style: kSimpleText.copyWith(
            color: cal.state == 'failed' ? AraColors.errorText : null,
          ),
        ),
        if (cal.reason case final reason? when reason.isNotEmpty)
          Text(reason, style: kSimpleText.copyWith(color: AraColors.errorText)),
        _offsetTable(l10n, cal),
        if (cal.isComplete && cal.driftWarning && drift != null && cal.cfzSteps != null)
          _warning(
            const Key('simple-offsets-drift'),
            l10n.focusOffsetsDrift(drift, cal.cfzSteps!),
          )
        else if (cal.isComplete && !cal.driftChecked)
          Text(l10n.focusOffsetsDriftNotChecked, style: AraText.caption),
        ?errorText,
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: OutlinedButton(
                key: const Key('simple-offsets-discard'),
                onPressed: live.busy
                    ? null
                    : ref.read(filterOffsetCalibrationProvider.notifier).discard,
                child: Text(l10n.focusOffsetsDiscard),
              ),
            ),
            // A stopped or failed run leaves the saved offsets as they were.
            if (cal.isComplete && measured)
              Expanded(
                child: FilledButton(
                  key: const Key('simple-offsets-save'),
                  style: kSimpleGoStyle,
                  onPressed: live.busy
                      ? null
                      : ref.read(filterOffsetCalibrationProvider.notifier).save,
                  child: Text(l10n.focusOffsetsSave),
                ),
              ),
          ],
        ),
      ];
    }
    // Before a run: what it does, the reference, the picks, and why not yet.
    final wheel = ref.watch(filterWheelProvider).value;
    final slots = wheel?.slots ?? const <FilterSlot>[];
    final (picks, reference) = simpleOffsetPicks(ref);
    final blocker = simpleOffsetsBlocker(ref, l10n);
    String named(FilterSlot s) =>
        s.name.isEmpty ? l10n.deviceSlot(s.position) : s.name;
    final referenceSlot =
        slots.where((s) => s.position == reference).firstOrNull;
    return [
      if (live.saved)
        Text(
          l10n.focusOffsetsSaved,
          key: const Key('simple-offsets-saved'),
          style: kSimpleText.copyWith(color: AraColors.accentConnected),
        ),
      Text(l10n.focusOffsetsIntro, style: AraText.caption),
      if (referenceSlot != null)
        Text(l10n.focusOffsetsReference(named(referenceSlot)), style: kSimpleText),
      if (slots.isNotEmpty) ...[
        Text(l10n.focusOffsetsPicker, style: AraText.caption),
        Wrap(
          spacing: AraSpace.s8,
          runSpacing: AraSpace.s8,
          children: [
            for (final s in slots)
              FilterChip(
                key: Key('simple-offsets-pick-${s.position}'),
                // Picked: Plan's solid blue with white words and tick.
                label: Text(
                  s.position == reference
                      ? l10n.focusOffsetsReferenceChip(named(s))
                      : named(s),
                  style: kSimpleText.copyWith(
                    color: picks.contains(s.position)
                        ? AraColors.selectionFg
                        : AraColors.textPrimary,
                  ),
                ),
                selected: picks.contains(s.position),
                selectedColor: AraColors.selectionBg,
                checkmarkColor: AraColors.selectionFg,
                // The reference is always measured.
                onSelected: s.position == reference || live.busy
                    ? null
                    : (on) => ref
                          .read(filterOffsetCalibrationProvider.notifier)
                          .setPicks(
                            on
                                ? {...picks, s.position}
                                : ({...picks}..remove(s.position)),
                          ),
              ),
          ],
        ),
      ],
      if (blocker != null)
        Text(
          blocker,
          key: const Key('simple-offsets-blocker'),
          style: kSimpleText.copyWith(color: AraColors.accentBusy),
        ),
      ?errorText,
    ];
  }

  /// The results: filter, offset, fit R² and the offset saved before, one row
  /// per filter, each read out whole; a weak fit and a failure say so under it.
  Widget _offsetTable(AppLocalizations l10n, FilterOffsetCalibration cal) {
    Widget cells(List<String> texts, TextStyle style) => Row(
      spacing: AraSpace.s8,
      children: [
        for (final (i, t) in texts.indexed)
          Expanded(
            flex: i == 0 ? 3 : 2,
            child: Text(t, style: style, textAlign: i == 0 ? null : TextAlign.end),
          ),
      ],
    );
    return Column(
      key: const Key('simple-offsets-table'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: [
        ExcludeSemantics(
          child: cells([
            l10n.focusOffsetsColFilter,
            l10n.focusOffsetsColOffset,
            l10n.focusFitR2,
            l10n.focusOffsetsColPrevious,
          ], AraText.caption),
        ),
        for (final f in cal.filters)
          () {
            final filter = f.name.isEmpty ? l10n.deviceSlot(f.position) : f.name;
            final name = f.position == cal.referencePosition
                ? l10n.focusOffsetsReferenceChip(filter)
                : filter;
            final offset = switch (f.status) {
              'measured' => f.offset == null ? '—' : simpleSigned(f.offset!),
              'failed' => l10n.focusOffsetsRowFailed,
              'skipped' => l10n.focusOffsetsRowSkipped,
              _ => l10n.focusOffsetsRowPending,
            };
            final r2 = f.rSquared?.toStringAsFixed(2) ?? '—';
            final previous = simpleSigned(f.previousOffset);
            return Semantics(
              key: Key('simple-offsets-row-${f.position}'),
              container: true,
              label: '$name, ${l10n.focusOffsetsColOffset} $offset, '
                  '${l10n.focusFitR2} $r2, ${l10n.focusOffsetsColPrevious} $previous'
                  '${f.isWeak ? '. ${l10n.focusOffsetsWeakFit(r2)}' : ''}'
                  '${f.isFailed && f.failureReason != null ? '. ${f.failureReason}' : ''}',
              excludeSemantics: true,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: 2,
                children: [
                  cells(
                    [name, offset, r2, previous],
                    kSimpleText.copyWith(
                      color: f.isFailed ? AraColors.errorText : null,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                  ),
                  if (f.isWeak) _warning(null, l10n.focusOffsetsWeakFit(r2)),
                  if (f.isFailed && f.failureReason != null)
                    Text(
                      f.failureReason!,
                      style: AraText.caption.copyWith(color: AraColors.errorText),
                    ),
                ],
              ),
            );
          }(),
      ],
    );
  }

  /// Icon + colour + text (DESIGN.md): the tint alone all but vanishes in
  /// night mode.
  Widget _warning(Key? key, String text) => Row(
    key: key,
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: AraSpace.s8,
    children: [
      const Icon(Icons.warning_amber_rounded, size: 18, color: AraColors.accentWarning),
      Expanded(
        child: Text(
          text,
          style: AraText.caption.copyWith(color: AraColors.accentWarning),
        ),
      ),
    ],
  );

  List<Widget> _maskQuestion(AppLocalizations l10n) => [
    Text(l10n.bahtinovMaskQuestion, style: kSimpleText),
    Text(l10n.bahtinovMaskQuestionDetail, style: AraText.caption),
    Row(
      spacing: AraSpace.s8,
      children: [
        Expanded(
          child: OutlinedButton(
            onPressed: () => setState(() {
              _askMask = false;
              _then = null;
            }),
            child: Text(l10n.bahtinovNotYet),
          ),
        ),
        Expanded(
          child: FilledButton(
            style: kSimpleGoStyle,
            onPressed: () async {
              final then = _then;
              setState(() {
                _askMask = false;
                _then = null;
              });
              await ref.read(bahtinovFocusProvider.notifier).finish();
              if (mounted) then?.call();
            },
            child: Text(l10n.bahtinovMaskRemoved),
          ),
        ),
      ],
    ),
  ];

  /// Manual: the full UI's Bahtinov readout, flat. The shutter starts and
  /// stops it; the Focuser window's dial (or the hand) turns the focuser.
  List<Widget> _bahtinov(
    AppLocalizations l10n,
    BahtinovFocusLive live,
    FocuserStatus? focuser,
  ) {
    final status = live.status;
    final (headline, colour) = BahtinovFocusCard.headlineFor(l10n, live);
    final steady = status.steady;
    final reading = status.active && steady != null && steady.detected;
    final hint = status.active ? bahtinovHint(l10n, status) : null;
    final exposure = ref.watch(simpleBahtinovExposureProvider);
    final measured = status.hasMeasurement && !live.finished;
    return [
      _twoLines(headline, kSimpleText.copyWith(color: colour)),
      if (focuser != null)
        Text(
          [
            if (focuser.position != null)
              l10n.focusFocuserAt('${focuser.position}'),
            if (focuser.name.isNotEmpty) focuser.name,
          ].join(' · '),
          style: AraText.caption,
        ),
      _slot(context, _kFocusManualReadoutHeight, [
      if (!status.active && !status.hasMeasurement && !live.finished)
        Text(l10n.bahtinovFitMaskDetail, style: AraText.caption),
      if (reading) ...[
        Row(
          mainAxisAlignment: MainAxisAlignment.center,
          crossAxisAlignment: CrossAxisAlignment.baseline,
          textBaseline: TextBaseline.alphabetic,
          spacing: AraSpace.s8,
          children: [
            Text(
              formatOffset(steady.offsetPx!.abs()),
              style: kSimpleText.copyWith(
                fontSize: 40,
                fontWeight: FontWeight.w300,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
            Text(l10n.bahtinovPxFromFocus, style: AraText.caption),
          ],
        ),
      ],
      if (hint != null) ...[
        Text(
          hint.title,
          textAlign: TextAlign.center,
          style: kSimpleText.copyWith(
            fontWeight: FontWeight.w600,
            color: switch (hint.advice) {
              TurnAdvice.atBest => AraColors.accentConnected,
              TurnAdvice.turnBack => AraColors.accentBusy,
              _ => AraColors.textPrimary,
            },
          ),
        ),
        Text(hint.detail, textAlign: TextAlign.center, style: AraText.caption),
      ],
      if (live.error != null)
        Text(
          failureReason(context.l10n, live.error!),
          style: kSimpleText.copyWith(color: AraColors.errorText),
          maxLines: 3,
        ),
      ]),
      _keepRoom(
        visible: !live.finished,
        child: Row(
          spacing: AraSpace.s12,
          children: [
            Expanded(child: Text(l10n.bahtinovExposure, style: kSimpleText)),
            SimpleSegments<double>(
              keyPrefix: 'simple-bahtinov-exposure',
              expand: false,
              options: [
                for (final s in kSimpleBahtinovExposures)
                  (s, '${s.round()} s', '${s.round()}'),
              ],
              selected: exposure,
              // A running readout keeps its exposure; Start again takes the
              // new one.
              onPick: status.active
                  ? null
                  : (s) => ref
                        .read(simpleBahtinovExposureProvider.notifier)
                        .set(s),
            ),
          ],
        ),
      ),
      _keepRoom(
        visible: measured,
        child: FilledButton(
          key: const Key('simple-bahtinov-finish'),
          style: steady?.withinZone == true ? kSimpleGoStyle : null,
          onPressed: live.busy ? null : () => setState(() => _askMask = true),
          child: Text(l10n.bahtinovFinish),
        ),
      ),
    ];
  }

  /// The guide camera, focused by hand: the full UI's guide-focus readout,
  /// flat. The shutter starts and stops it; its frames fill the screen.
  List<Widget> _guide(AppLocalizations l10n, GuideFocusLive live) {
    final status = live.status;
    final guider = ref.watch(guiderStatusProvider).asData?.value;
    final oag = ref.watch(
      phd2SettingsProvider.select((p) => p.guiderSetupType == 'oag'),
    );
    final gated =
        oag &&
        !ref.watch(autofocusLiveProvider.select((s) => s.focusedThisSession));
    final blocker = guideFocusBlocker(l10n, guider, active: status.active);
    final (headline, colour) = GuideFocusCard.headlineFor(
      l10n,
      status,
      gated: gated,
      blocked: blocker != null,
    );
    final latest = status.latest;
    final hint = status.active ? guideFocusHint(l10n, status) : null;
    final exposure = ref.watch(simpleGuideExposureProvider);
    return [
      _twoLines(headline, kSimpleText.copyWith(color: colour)),
      Text(
        oag ? l10n.focusGuideOag : l10n.focusGuideScope,
        style: AraText.caption,
      ),
      _slot(context, _kFocusGuideReadoutHeight, [
      if (gated && !status.active) ...[
        Text(l10n.focusGuideMainFirst, style: AraText.caption),
        OutlinedButton(
          key: const Key('simple-guide-already-focused'),
          onPressed: () =>
              ref.read(autofocusLiveProvider.notifier).markFocusedManually(),
          child: Text(l10n.focusAlreadyInFocus),
        ),
      ],
      if (blocker != null) Text(blocker, style: AraText.caption),
      if (latest != null)
        Row(
          spacing: AraSpace.s4,
          children: [
            _figure(l10n.focusStars, '${latest.stars}'),
            _figure(l10n.focusFrames, '${status.seq}'),
          ],
        ),
      if (hint != null) ...[
        Text(
          hint.title,
          textAlign: TextAlign.center,
          style: kSimpleText.copyWith(
            fontWeight: FontWeight.w600,
            color: switch (hint.advice) {
              TurnAdvice.atBest => AraColors.accentConnected,
              TurnAdvice.turnBack => AraColors.accentBusy,
              _ => AraColors.textPrimary,
            },
          ),
        ),
        Text(hint.detail, textAlign: TextAlign.center, style: AraText.caption),
      ],
      if ((live.error == null ? status.error : failureReason(context.l10n, live.error!))
          case final error?)
        Text(
          error,
          style: kSimpleText.copyWith(color: AraColors.errorText),
          maxLines: 3,
        ),
      ]),
      Row(
        spacing: AraSpace.s12,
        children: [
          Expanded(child: Text(l10n.bahtinovExposure, style: kSimpleText)),
          SimpleSegments<double>(
            keyPrefix: 'simple-guide-exposure',
            expand: false,
            options: [
              for (final s in kSimpleGuideExposures)
                (s, '${s.round()} s', '${s.round()}'),
            ],
            selected: exposure,
            onPick: status.active
                ? null
                : (s) => ref.read(simpleGuideExposureProvider.notifier).set(s),
          ),
        ],
      ),
    ];
  }
}

// One height from a run's start to its end (#1717): each view keeps a slot for
// what changes. Heights at 1.0x text; the slot scales with it.
const double _kFocusReadoutHeight = 72;
const double _kFocusChartHeight = 160;
const double _kFocusManualReadoutHeight = 124;
const double _kFocusGuideReadoutHeight = 132;

/// Holds [child]'s room while hidden; hidden, it takes no tap and is not read out.
Widget _keepRoom({required bool visible, required Widget child}) => Visibility(
  visible: visible,
  maintainState: true,
  maintainAnimation: true,
  maintainSize: true,
  child: child,
);

/// A status line with room for two lines, however short it is.
Widget _twoLines(String text, TextStyle style) => Stack(
  children: [
    _keepRoom(
      visible: false,
      child: Text('\n', style: style),
    ),
    Text(text, style: style, maxLines: 2),
  ],
);

/// A slot of one height: what fills it can change without moving the window.
/// shortcut: grows past its height for text too long for it (large text in a
/// long language), so nothing is cut; raise the heights if that shows.
Widget _slot(BuildContext context, double height, List<Widget> children) =>
    ConstrainedBox(
      constraints: BoxConstraints(
        minHeight: MediaQuery.textScalerOf(context).scale(height),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: 4,
        children: children,
      ),
    );

/// A figure tile: its label over its value, sharing a row with its siblings
/// (a finished run's best position / stars / fit R², the guide camera's
/// stars and frames).
Widget _figure(String label, String value) => Expanded(
  child: Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
    decoration: BoxDecoration(
      color: AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.simplePanel),
    ),
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: AraText.caption, maxLines: 1),
        Text(value, style: kSimpleText),
      ],
    ),
  ),
);
