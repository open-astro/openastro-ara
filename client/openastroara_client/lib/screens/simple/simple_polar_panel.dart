import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart';
import '../../state/imaging/main_camera_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../util/polar_align_error_text.dart';
import '../../state/polar_align/polar_align_preflight_state.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_aligner_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../widgets/settings/section_load_state.dart';
import '../../widgets/imaging/polar_align_readout.dart';
import '../../widgets/imaging/polar_aligner_block.dart';
import '../../widgets/imaging/polar_preflight_checklist.dart';
import '../../widgets/imaging/polar_start_confirm.dart';
import 'simple_preflight.dart'
    show SimplePreflightStep, simplePreflightProvider, simplePreflightNext;
import 'simple_preview_panel.dart'
    show
        SimplePresetTile,
        SimpleShutterButton,
        exposurePresets,
        formatExposure,
        kSimpleText;
import 'simple_shell.dart'
    show SimplePanel, SimpleSegments, kSimpleGoStyle, simpleStackText;
import '../../state/simple_messages_state.dart';

/// True while the server runs any phase of the routine.
bool simplePolarActive(PolarAlignLive live) => live.isActive;

Duration _exposure(PolarAlignSettings s) =>
    Duration(milliseconds: (s.exposureSeconds * 1000).round());

bool _single(PolarAlignSettings s) => s.loopMode == PolarAlignLoopModes.single;

/// "45 s" over "Single": the rail chip's caption in Polar align, the
/// routine's setup on two lines (one doesn't fit the 60 px chip). Empty while
/// the profile's copy is not in from the rig (ADR 0015). Pure — unit-tested.
String simplePolarSummary(PolarAlignSettings? s, AppLocalizations l10n) {
  if (s == null) return '';
  return '${formatExposure(_exposure(s))}\n'
      '${_single(s) ? l10n.polarSingle : l10n.polarLoop}';
}

/// The rail's shutter in Polar align: starts the routine; while it runs, its
/// square stops it.
class SimplePolarShutter extends ConsumerWidget {
  const SimplePolarShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(polarAlignApiProvider);
    final active = simplePolarActive(ref.watch(polarAlignLiveProvider));
    // Busy while any panel's call runs: a second press would send a start
    // twice. Stop never waits, as it halts a routine that moves the mount.
    final busy = ref.watch(polarAlignActionProvider).busy;
    // #2068 — a pre-flight check that fails: the rig would refuse the start.
    final blocked = !active && polarPreflightBlocksStart(ref.watch(polarAlignPreflightProvider));
    final notify = userNotifier(context, origin: SimpleStatusOrigin.polar);
    final l10n = context.l10n;
    final action = ref.read(polarAlignActionProvider.notifier);
    // #1764 — through the shared module (ADR 0013 rule 3). Its failure goes to
    // a pill only: the panel's line would say it a second time.
    Future<void> run(String verb, Future<void> Function(PolarAlignClient api) op) async {
      String? failure;
      await action.run(op, describe: (e) {
        failure = friendlyError(l10n, e, action: verb);
        return null;
      });
      if (failure != null) notify(failure!, error: true, snackBarRed: false);
    }

    return SimpleShutterButton(
      start: true,
      running: active,
      label: active ? l10n.polarStop : l10n.polarStart,
      whyOff: api == null
          ? l10n.shellRigNotConnected
          : blocked
          ? l10n.polarPreflightBlocked
          : l10n.shellMainCameraBusy,
      // It turns the mount: never under the main camera's frame.
      onPressed: api == null || blocked || (!active && (busy || ref.watch(mainCameraBusyProvider)))
          ? null
          : active
          ? () => run(l10n.polarActionStop, (api) => api.stop())
          // #2048 — through the confirm step: nothing moves until the user says so.
          : () => run(l10n.polarActionStart, (api) => confirmAndStartPolarAlign(context, api)),
    );
  }
}

/// The phase in words. Pure — unit-tested.
String simplePolarPhase(PolarAlignLive live, AppLocalizations l10n) =>
    switch (live.phase) {
      PolarAlignStates.homing when live.waitingForHome => l10n.polarPhaseNeedsHome,
      PolarAlignStates.homing => l10n.polarPhaseHoming,
      PolarAlignStates.seeding => l10n.polarPhaseSeeding,
      PolarAlignStates.adjusting when live.stage == PolarAlignStages.coarse =>
        l10n.polarPhaseRough,
      PolarAlignStates.adjusting => l10n.polarPhaseAdjusting,
      PolarAlignStates.paused => l10n.polarPhasePaused,
      PolarAlignStates.checking => l10n.polarPhaseChecking(live.checkSolvesDone, live.checkSolvesTotal),
      PolarAlignStates.checked => l10n.polarPhaseChecked,
      PolarAlignStates.failed => l10n.polarPhaseFailed,
      _ => l10n.polarPhaseReady,
    };

/// Which stepper step the routine is on: 0 Rough, 1 Fine, 2 Check. Null when
/// nothing runs and there is no verdict to show. Each is its own Start
/// (#2082): none leads into another. A null stage is an older
/// daemon's one-stage routine: the fine step. Pure — unit-tested.
int? simplePolarStep(PolarAlignLive live) {
  if (live.phase == PolarAlignStates.checking ||
      live.phase == PolarAlignStates.checked) {
    return 2;
  }
  if (!live.isActive) return null;
  return live.stage == PolarAlignStages.coarse ? 0 : 1;
}

/// The rough stage's side-of-pole sentence in the user's language, or null
/// until both errors are known. Pure — unit-tested.
String? simplePolarSideText(PolarAlignLive live, AppLocalizations l10n) =>
    polarSideOfPoleText(l10n, live.altErrorArcmin, live.azErrorArcmin);

/// #2081 the Dec spread (arcsec) the panel warns about, or null: the check's
/// on its verdict, else the seed's while the live readout runs (adjusting or
/// paused, not while verifying). Pure — unit-tested.
double? simplePolarDecSpread(PolarAlignLive live, {required bool verifying}) {
  if (live.phase == PolarAlignStates.checked) return live.checkResult?.decSpreadArcsec;
  if (verifying) return null;
  return live.phase == PolarAlignStates.adjusting || live.phase == PolarAlignStates.paused
      ? live.decSpreadArcsec
      : null;
}

/// The checked verdict's word in the user's language: the daemon's own verdict
/// token, or the local bands on the total for a token this build does not know.
String simplePolarVerdictLabel(PolarAlignCheckResult r, AppLocalizations l10n) {
  final token = polarRatingForVerdict(r.verdict) == null ? polarVerdictFor(r.totalErrorArcmin) : r.verdict;
  return switch (token) {
    'excellent' => l10n.polarVerdictExcellent,
    'very_good' => l10n.polarVerdictVeryGood,
    'good' => l10n.polarVerdictGood,
    'rough' => l10n.polarVerdictRough,
    _ => l10n.polarVerdictFarOff,
  };
}

/// Polar align's panel beside the right rail: the routine's settings
/// (exposure, Loop/Single); while it runs, the phase, the total error with its
/// zone, the bullseye, the altitude and azimuth turns, Take frame (Single),
/// Check and Done (once inside the profile's tolerance); after Done (or a
/// Stop), Check again. The rail's shutter starts and stops it.
class SimplePolarPanel extends ConsumerStatefulWidget {
  const SimplePolarPanel({super.key});

  @override
  ConsumerState<SimplePolarPanel> createState() => _SimplePolarPanelState();
}

class _SimplePolarPanelState extends ConsumerState<SimplePolarPanel> {
  final _bolt = BoltNudgeTracker();

  @override
  void initState() {
    super.initState();
    _bolt.track(
      ref.read(polarAlignLiveProvider),
    ); // the first frame is the baseline
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  /// The WS stream may have skipped past routine events fired before this
  /// panel opened: seed from REST.
  Future<void> _hydrate() =>
      ref.read(polarAlignFrameProvider.notifier).hydrate();

  // [action] completes "Couldn't <action>".
  /// True when the rig took it.
  Future<bool> _run(
    String action,
    Future<void> Function(PolarAlignClient api) op,
  ) {
    final l10n = context.l10n;
    return ref
        .read(polarAlignActionProvider.notifier)
        .run(op, describe: (e) => friendlyError(l10n, e, action: action));
  }

  /// [change] on the rig's copy of the profile section (ADR 0015): refused,
  /// nothing sent, when it is not loaded; a failed save rolls back, and a 412
  /// reloads the rig's copy (this screen has no Reload of its own).
  void _save(PolarAlignSettings Function(PolarAlignSettings) change) {
    final l10n = context.l10n;
    ref
        .read(polarAlignActionProvider.notifier)
        .run(
          (_) => ref
              .read(polarAlignSettingsProvider.notifier)
              .changeAndSave(change),
          describe: (e) =>
              e is SectionNotLoadedException || e is SectionConflictException
              ? e.toString()
              : friendlyError(l10n, e, action: l10n.polarActionSave),
        );
  }

  /// #2082 — Check: a confirmed Start(check) through the rig's sweep plan. From
  /// the adjust loop the rig ends the loop first (like Stop), then measures.
  Future<void> _check() {
    final l10n = context.l10n;
    return _run(
      l10n.polarActionCheck,
      (api) => confirmAndStartPolarAlign(context, api, stage: PolarAlignStages.check),
    );
  }

  /// #2082 — Rough first: the main camera's measurement, a confirmed Start
  /// through the rig's sweep plan, offered after the guide camera could not
  /// solve the pole field (Joey, 2026-11-20).
  Future<void> _roughFirst() {
    final l10n = context.l10n;
    return _run(
      l10n.polarActionStart,
      (api) => confirmAndStartPolarAlign(context, api, stage: PolarAlignStages.coarse),
    );
  }

  /// Finish the routine. In Preflight, Done moves on (the routine is complete:
  /// nothing to stop).
  Future<void> _done(bool inSimplePreflight) async {
    // Read before the await: the panel may have closed by the time the rig answers.
    final pilot = ref.read(simplePreflightProvider.notifier);
    final done = await _run(
      context.l10n.polarActionComplete,
      (api) => api.complete(),
    );
    if (inSimplePreflight && done) pilot.polarDone();
  }

  String _doneLabel(AppLocalizations l10n, bool inSimplePreflight) =>
      !inSimplePreflight
      ? l10n.polarDone
      : simplePreflightNext(ref, after: SimplePreflightStep.polarAlign) ==
            SimplePreflightStep.guide
      ? l10n.preflightNextGuide
      : l10n.preflightDoneStartImaging;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final inSimplePreflight = ref.watch(simplePreflightProvider) == SimplePreflightStep.polarAlign;
    final live = ref.watch(polarAlignLiveProvider);
    ref.listen(
      polarAlignLiveProvider,
      (_, next) => setState(() => _bolt.track(next)),
    );
    // Null until the profile's copy is in from the rig (ADR 0015): Setup
    // says so, with Retry, and nothing is judged against a made-up tolerance.
    final loaded = ref.watch(polarAlignSettingsProvider);
    final settings = loaded.value;
    final command = ref.watch(polarAlignActionProvider);
    final busy = command.busy;
    final southern = (ref.watch(siteSettingsProvider).value?.latitudeDeg ?? 0) < 0;
    final active = simplePolarActive(live);
    final total = live.totalErrorArcmin;
    final step = simplePolarStep(live);
    // Verifying and Verified show only the phase line and the verdict.
    final verifying = step == 2;
    final inTolerance =
        total != null &&
        settings != null &&
        total <= settings.targetToleranceArcmin;
    final color = zoneColor(total);
    // #1971 while the Polar Aligner's motors work, the arrows dim and
    // Verify and Done step aside (user, 2026-11-16).
    final motorsBusy = polarAlignerMotorsBusy(live);
    final aligner = polarAlignerShown(ref.watch(polarAlignerProvider));
    // How far Check turns the mount, said before it moves (#2082); only the
    // rig's own step, so none until its settings are in (ADR 0015).
    final checkCaption = settings == null
        ? null
        : l10n.polarCheckCaption(settings.checkStepDeg.toStringAsFixed(0));
    // After Done or a Stop, and beside a verdict: a new Check from where the mount is.
    final checkAgain = !active &&
        (live.phase == PolarAlignStates.stopped || live.phase == PolarAlignStates.checked);
    final error =
        command.error ??
        (live.phase == PolarAlignStates.failed
            ? polarAlignErrorText(context.l10n, live)
            : null);

    // A turn as a tile: the axis, a big arrow and the move, read at arm's
    // length while the hands are on the bolts.
    Widget turn(
      String id,
      String axis,
      double? value,
      (IconData, String) hint,
    ) => Container(
      key: Key('simple-polar-turn-$id'),
      padding: const EdgeInsets.all(AraSpace.s8),
      decoration: BoxDecoration(
        color: AraColors.tileFill,
        borderRadius: BorderRadius.circular(AraRadius.simplePanel),
      ),
      child: Row(
        spacing: AraSpace.s8,
        children: [
          // 0.3: the approved mock's dimmed arrow.
          if (motorsBusy)
            Opacity(opacity: 0.3, child: Icon(hint.$1, size: 70, color: AraColors.textPrimary))
          else
            Icon(hint.$1, size: 70, color: AraColors.textPrimary),
          Expanded(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // One line, scaled down when the convention (#2083) makes it long.
                FittedBox(
                  fit: BoxFit.scaleDown,
                  alignment: Alignment.centerLeft,
                  child: Text(axis, key: Key('simple-polar-turn-$id-axis'), style: AraText.caption, maxLines: 1),
                ),
                Text(
                  value == null
                      ? hint.$2
                      : '${hint.$2} ${value.abs().toStringAsFixed(1)}′',
                  style: kSimpleText,
                  maxLines: 2,
                ),
              ],
            ),
          ),
        ],
      ),
    );
    // At large text the tiles go under the bullseye at their own height, and
    // the settings stack: a caption and a two-line move no longer fit 86 px.
    final stack = simpleStackText(context);
    final bullseye = total == null
        ? const SizedBox.shrink()
        : SizedBox.square(
            key: const Key('simple-polar-bullseye'),
            dimension: 180,
            child: CustomPaint(
              painter: BullseyePainter(
                edgeInset: 1,
                dotFraction: bullseyeDotFraction(
                  live.azErrorArcmin,
                  live.altErrorArcmin,
                  bullseyeRangeArcmin(total),
                  southern: southern,
                ),
                zoneColor: color,
                reticle: nearPoleReticle(total),
              ),
            ),
          );
    final turns = [
      turn(
        'alt',
        l10n.polarAltitude,
        live.altErrorArcmin,
        polarAltHint(l10n, live.altErrorArcmin),
      ),
      turn(
        'az',
        // #2083 the azimuth's convention: a true arc, not NINA's angle.
        '${l10n.polarAzimuth} · ${l10n.polarAzimuthArc}',
        live.azErrorArcmin,
        polarAzHint(l10n, live.azErrorArcmin, southern: southern),
      ),
    ];

    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            // The phase and the total error on one line.
            if (active || live.phase == PolarAlignStates.failed || live.phase == PolarAlignStates.checked)
              Row(
                spacing: AraSpace.s8,
                children: [
                  Expanded(
                    child: Text(
                      simplePolarPhase(live, l10n),
                      style: kSimpleText,
                    ),
                  ),
                  if ((active || live.phase == PolarAlignStates.checked) && total != null) ...[
                    Icon(Icons.circle, size: 12, color: color),
                    Text(
                      '${total.toStringAsFixed(1)}′',
                      style: kSimpleText.copyWith(
                        fontSize: AraFontSize.simpleReadingLarge,
                        fontWeight: FontWeight.w300,
                      ),
                    ),
                  ],
                ],
              ),
            if (step != null)
              _PolarStepper(
                key: const Key('simple-polar-stepper'),
                step: step,
                done: live.phase == PolarAlignStates.checked,
                doneLabel: l10n.polarStepDone,
                labels: [
                  l10n.polarStepRough,
                  l10n.polarStepFine,
                  l10n.polarStepVerify,
                ],
              ),
            if (!verifying) ...[
              Text(l10n.polarSetup, style: AraText.caption),
              // The settings for the next frame: a row each like the Focus
              // panel's choices (user, 2026-11-04: one row read oddly); side by
              // side while it runs, so the bullseye panel still fits an iPad.
              if (settings == null)
                SectionNotLoaded(
                  key: const Key('simple-polar-setup-not-loaded'),
                  section: loaded,
                  padding: EdgeInsets.zero,
                  onRetry: () {
                    final api = ref.read(profileApiProvider);
                    if (api != null) {
                      ref.read(polarAlignSettingsProvider.notifier).load(api);
                    }
                  },
                )
              else
                Flex(
                  key: const Key('simple-polar-setup'),
                  direction: active && !stack ? Axis.horizontal : Axis.vertical,
                  crossAxisAlignment: active && !stack
                      ? CrossAxisAlignment.center
                      : CrossAxisAlignment.stretch,
                  spacing: AraSpace.s8,
                  children: [
                    for (final w in <Widget>[
                      SimpleSegments<String>(
                        options: [
                          (PolarAlignLoopModes.loop, l10n.polarLoop, 'loop'),
                          (
                            PolarAlignLoopModes.single,
                            l10n.polarSingle,
                            'single',
                          ),
                        ],
                        selected: settings.loopMode,
                        onPick: (mode) =>
                            _save((s) => s.copyWith(loopMode: mode)),
                      ),
                      SimplePresetTile<Duration>(
                        key: const Key('simple-polar-exposure'),
                        label: l10n.polarExposure,
                        value: _exposure(settings),
                        options: exposurePresets(minSec: 0, maxSec: 60),
                        format: formatExposure,
                        onSelected: (d) => _save(
                          (s) => s.copyWith(
                            exposureSeconds: d.inMilliseconds / 1000,
                          ),
                        ),
                      ),
                    ])
                      active && !stack ? Expanded(child: w) : w,
                  ],
                ),
              // Its own fixed slot between Setup and Error (user, 2026-11-16).
              if (aligner) const PolarAlignerBlock(),
              // #2068 the checks the rig makes before a start; the shutter is off while one fails.
              if (!active) const PolarPreflightChecklist(),
            ],
            // Rough stage: which side of the pole, in words.
            if (active && live.stage == PolarAlignStages.coarse)
              Text(
                simplePolarSideText(live, l10n) ?? l10n.polarRoughWaiting,
                key: const Key('simple-polar-side'),
                style: kSimpleText,
                maxLines: 3,
              ),
            // #2083 a hand-turn run: the turn it asks for, or that Ara moves nothing.
            if (active &&
                live.handTurn &&
                (live.phase == PolarAlignStates.seeding || live.phase == PolarAlignStates.checking))
              Text(
                polarHandTurnText(l10n, live.handTurnWait) ?? l10n.polarHandTurnRunNote,
                key: const Key('simple-polar-hand-turn'),
                style: kSimpleText.copyWith(
                  color: live.handTurnWait == null ? null : AraColors.accentBusy,
                ),
                maxLines: 4,
              ),
            // #2072 the camera sits where the readout reads the altitude bolt poorly.
            if (active &&
                !verifying &&
                live.readoutCaution == PolarAlignReadoutCautions.altitudeWeak)
              Text(
                l10n.polarReadoutCautionAltitudeWeak,
                key: const Key('simple-polar-readout-caution'),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
                maxLines: 4,
              ),
            // #2081 the mount's Dec moved across the three points: the live readout, or the check's verdict, may be off.
            if (simplePolarDecSpread(live, verifying: verifying) case final spread?)
              Text(
                l10n.polarDecSpreadWarning(spread.toStringAsFixed(1)),
                key: const Key('simple-polar-dec-spread'),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
                maxLines: 4,
              ),
            // #2082 a point's frames stayed scattered after their retake: the
            // readout, or the check's verdict, rests on noisy solves.
            if ((active && !verifying && live.measurementNoisy) ||
                (live.phase == PolarAlignStates.checked && live.checkResult?.noisy == true))
              Text(
                l10n.polarNoisyMeasurement,
                key: const Key('simple-polar-noisy'),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
                maxLines: 4,
              ),
            // The check's verdict beside the last run's readout (#2082).
            if (live.phase == PolarAlignStates.checked && live.checkResult != null)
              Text(
                live.checkResult!.fineDeltaArcmin == null
                    ? l10n.polarCheckedLine(simplePolarVerdictLabel(live.checkResult!, l10n))
                    : l10n.polarCheckedVsFine(
                        simplePolarVerdictLabel(live.checkResult!, l10n),
                        live.checkResult!.fineDeltaArcmin!.toStringAsFixed(1),
                      ),
                key: const Key('simple-polar-checked'),
                style: kSimpleText,
                maxLines: 3,
              ),
            if (active &&
                total != null &&
                live.phase != PolarAlignStates.homing &&
                !verifying) ...[
              Text(l10n.polarErrorHeading, style: AraText.caption),
              Flex(
                direction: stack ? Axis.vertical : Axis.horizontal,
                crossAxisAlignment: stack
                    ? CrossAxisAlignment.stretch
                    : CrossAxisAlignment.center,
                spacing: stack ? AraSpace.s8 : AraSpace.s12,
                children: [
                  // Its own size, centred, when it stands over the tiles.
                  if (stack) Center(child: bullseye) else bullseye,
                  if (stack)
                    ...turns
                  else
                    Expanded(
                      child: SizedBox(
                        height: 180,
                        child: Column(
                          spacing: AraSpace.s8,
                          children: [for (final t in turns) Expanded(child: t)],
                        ),
                      ),
                    ),
                ],
              ),
              if (polarInitialErrorText(l10n, live.initialErrorArcmin) case final warning?)
                Text(
                  warning,
                  key: const Key('simple-polar-initial-error'),
                  style: kSimpleText.copyWith(color: AraColors.accentBusy),
                  maxLines: 4,
                ),
            ],
            // The full panel's nudge, cut to two lines for the 248 px panel.
            if (active && _bolt.nudge != null)
              Text(
                l10n.polarBoltWorse(
                  _bolt.nudge! ? l10n.polarAltitude : l10n.polarAzimuth,
                ),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            if (active && live.consecutiveSolveFailures > 0)
              Text(
                l10n.polarNoSolve(live.consecutiveSolveFailures),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
              ),
            if (error != null)
              Text(
                error,
                style: kSimpleText.copyWith(color: AraColors.errorText),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
            if (active &&
                live.stage == PolarAlignStages.coarse &&
                !live.waitingForHome) ...[
              // Inside the tolerance (#1875): the rough alignment may be enough.
              // #2082 — no Next: fine; the guide camera is a new Start.
              if (total != null && settings != null)
                Text(
                  inTolerance
                      ? l10n.polarInsideTolerance(
                          polarToleranceText(settings.targetToleranceArcmin),
                        )
                      : l10n.polarOutsideTolerance(
                          polarToleranceText(settings.targetToleranceArcmin),
                        ),
                  key: const Key('simple-polar-tolerance-caption'),
                  style: AraText.caption,
                  maxLines: 3,
                ),
              if (inTolerance && !motorsBusy)
                OutlinedButton(
                  key: const Key('simple-polar-done-rough'),
                  onPressed: busy ? null : () => _done(inSimplePreflight),
                  child: Text(_doneLabel(l10n, inSimplePreflight)),
                ),
            ],
            if (live.phase == PolarAlignStates.checked)
              FilledButton(
                key: const Key('simple-polar-done'),
                style: kSimpleGoStyle,
                onPressed: busy ? null : () => _done(inSimplePreflight),
                child: Text(_doneLabel(l10n, inSimplePreflight)),
              ),
            // #2082 — after Done (or a Stop), and beside a verdict: Check again,
            // a confirmed new measurement that says how far it turns.
            if (checkAgain) ...[
              if (checkCaption != null)
                Text(
                  checkCaption,
                  key: const Key('simple-polar-check-again-caption'),
                  style: AraText.caption,
                  maxLines: 3,
                ),
              OutlinedButton(
                key: const Key('simple-polar-check-again'),
                // #2068 — off while a check that does not depend on the turn step fails.
                onPressed: busy || polarPreflightBlocksCheck(ref.watch(polarAlignPreflightProvider)) ? null : _check,
                child: Text(l10n.polarCheck),
              ),
            ],
            // #2082 — after the guide camera could not solve the pole field:
            // the main camera's measurement, which says how far it turns. Not
            // after a run on the main camera, which just failed.
            if (!active &&
                live.phase == PolarAlignStates.failed &&
                live.errorReason == 'seed_solve_failed' &&
                !live.usesMainCamera) ...[
              if (settings != null)
                Text(
                  l10n.polarRoughFirstCaption(
                    settings.seedRotationDeg.toStringAsFixed(0),
                  ),
                  key: const Key('simple-polar-rough-caption'),
                  style: AraText.caption,
                  maxLines: 3,
                ),
              OutlinedButton(
                key: const Key('simple-polar-rough-first'),
                // #2068 — a start like the shutter's: off while a check fails.
                onPressed: busy || polarPreflightBlocksStart(ref.watch(polarAlignPreflightProvider)) ? null : _roughFirst,
                child: Text(l10n.polarRoughFirst),
              ),
            ],
            if (checkCaption != null &&
                active &&
                !motorsBusy &&
                live.stage != PolarAlignStages.coarse &&
                (live.phase == PolarAlignStates.adjusting ||
                    live.phase == PolarAlignStates.paused))
              Text(
                checkCaption,
                key: const Key('simple-polar-verify-caption'),
                style: AraText.caption,
                maxLines: 3,
              ),
            // A null stage is an older daemon's one-stage routine: the fine buttons.
            if (active &&
                live.stage != PolarAlignStages.coarse &&
                (live.phase == PolarAlignStates.adjusting || live.phase == PolarAlignStates.paused))
              Row(
                spacing: AraSpace.s8,
                children: [
                  // Single waits for a tap before each frame; Loop shoots on
                  // its own.
                  if (settings != null && _single(settings))
                    Expanded(
                      child: FilledButton.tonal(
                        key: const Key('simple-polar-capture'),
                        onPressed: busy
                            ? null
                            : () => _run(
                                l10n.polarActionCapture,
                                (api) => api.requestCapture(),
                              ),
                        child: Text(l10n.polarTakeFrame),
                      ),
                    ),
                  if (!motorsBusy) ...[
                  Expanded(
                    child: OutlinedButton(
                      key: const Key('simple-polar-check'),
                      onPressed: !busy &&
                              inTolerance &&
                              !polarPreflightBlocksCheck(ref.watch(polarAlignPreflightProvider))
                          ? _check
                          : null,
                      child: Text(l10n.polarCheck),
                    ),
                  ),
                  Expanded(
                    child: FilledButton(
                      key: const Key('simple-polar-done'),
                      style: kSimpleGoStyle,
                      onPressed: !busy && inTolerance
                          ? () => _done(inSimplePreflight)
                          : null,
                      child: Text(_doneLabel(l10n, inSimplePreflight)),
                    ),
                  ),
                  ],
                ],
              ),
          ],
        ),
      ),
    );
  }
}

/// The polar-align camera's latest frame filling the image area while the
/// routine runs.
class SimplePolarFrame extends ConsumerWidget {
  const SimplePolarFrame({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // The main camera's frames (the rough stage, or the profile's camera,
    // #2082): the guider has no picture.
    final mainCamera = ref.watch(
      polarAlignLiveProvider.select((l) => l.usesMainCamera),
    );
    final frame = ref.watch(polarAlignFrameProvider);
    final jpeg = mainCamera ? null : frame;
    return ColoredBox(
      color: AraColors.imageBackdrop,
      child: jpeg == null
          ? const SizedBox.expand()
          : Center(child: Image.memory(jpeg, gaplessPlayback: true)),
    );
  }
}

/// Rough · Fine · Check: the step the routine is on is filled, with a tick
/// once a check finished. Each is its own Start (#2082), so no step before it
/// carries a tick.
class _PolarStepper extends StatelessWidget {
  const _PolarStepper({
    super.key,
    required this.step,
    required this.done,
    required this.doneLabel,
    required this.labels,
  });

  final int step;

  /// The step the routine is on has finished (a check's verdict).
  final bool done;
  final String doneLabel;
  final List<String> labels;

  @override
  Widget build(BuildContext context) => Row(
    spacing: AraSpace.s4,
    children: [
      for (var i = 0; i < labels.length; i++)
        Expanded(
          child: Semantics(
            selected: i == step,
            child: Container(
              key: Key('simple-polar-step-$i'),
              padding: const EdgeInsets.symmetric(vertical: AraSpace.s4),
              alignment: Alignment.center,
              decoration: BoxDecoration(
                // White on selectionBg is 4.6:1 and dimmer than accentInfo at night.
                color: i == step ? AraColors.selectionBg : AraColors.tileFill,
                borderRadius: BorderRadius.circular(AraRadius.simplePanel),
              ),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                spacing: AraSpace.s4,
                children: [
                  // An icon, not U+2713: the glyph falls back to a box in Roboto.
                  if (i == step && done)
                    Icon(
                      Icons.check,
                      size: AraText.caption.fontSize,
                      color: AraColors.accentConnected,
                      semanticLabel: doneLabel,
                    ),
                  Flexible(
                    child: Text(
                      labels[i],
                      style: AraText.caption.copyWith(
                        color: i == step
                            ? AraColors.selectionFg
                            : AraColors.textSecondary,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
    ],
  );
}
