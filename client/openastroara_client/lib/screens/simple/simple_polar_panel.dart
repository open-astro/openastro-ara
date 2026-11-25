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
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_aligner_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../widgets/settings/section_load_state.dart';
import '../../widgets/imaging/polar_align_readout.dart';
import '../../widgets/imaging/polar_aligner_block.dart';
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
      whyOff: api == null ? l10n.shellRigNotConnected : l10n.shellMainCameraBusy,
      // It turns the mount: never under the main camera's frame.
      onPressed: api == null || (!active && (busy || ref.watch(mainCameraBusyProvider)))
          ? null
          : active
          ? () => run(l10n.polarActionStop, (api) => api.stop())
          : () => run(l10n.polarActionStart, (api) => api.start()),
    );
  }
}

/// The phase in words. Pure — unit-tested.
String simplePolarPhase(PolarAlignLive live, AppLocalizations l10n) =>
    switch (live.phase) {
      PolarAlignStates.homing => l10n.polarPhaseHoming,
      PolarAlignStates.seeding => l10n.polarPhaseSeeding,
      PolarAlignStates.adjusting => l10n.polarPhaseAdjusting,
      PolarAlignStates.paused => l10n.polarPhasePaused,
      PolarAlignStates.checking => l10n.polarPhaseChecking(live.checkSolvesDone, live.checkSolvesTotal),
      PolarAlignStates.checked => l10n.polarPhaseChecked,
      PolarAlignStates.failed => l10n.polarPhaseFailed,
      _ => l10n.polarPhaseReady,
    };

/// True while a check runs or shows its verdict: the panel then shows only the
/// phase line and the verdict. Pure — unit-tested.
bool simplePolarVerifying(PolarAlignLive live) =>
    live.phase == PolarAlignStates.checking ||
    live.phase == PolarAlignStates.checked;

/// The checked verdict's word in the user's language: #2072 with the result's
/// 1σ, the bands on the error plus twice it (so a noisy check never reads
/// Excellent); without one, the daemon's own verdict token, or the local bands
/// on the total for a token this build does not know.
String simplePolarVerdictLabel(PolarAlignCheckResult r, AppLocalizations l10n) {
  final token = polarCheckVerdict(r);
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

  /// Check: a three-point measurement of its own. From the adjust loop the rig
  /// ends the loop first (like Stop), then measures.
  Future<void> _check() => _run(
    context.l10n.polarActionCheck,
    (api) => api.start(stage: PolarAlignStages.check),
  );

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
    final verifying = simplePolarVerifying(live);
    final inTolerance =
        total != null &&
        settings != null &&
        total <= settings.targetToleranceArcmin;
    // #2072 inside the tolerance on the error alone, but not with twice its
    // 1σ: the panel says the measurement cannot confirm it. Done stays
    // available inside the tolerance (Joey, 2026-11-21).
    final unconfirmed = polarUnconfirmedText(l10n, total, live.totalSigmaArcmin, settings?.targetToleranceArcmin);
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
                      // #2072 the total with its 1σ: "0.2′ ± 1.1′".
                      polarErrorWithSigmaText(l10n, '${total.toStringAsFixed(1)}′', live.totalSigmaArcmin),
                      key: const Key('simple-polar-total'),
                      style: kSimpleText.copyWith(
                        fontSize: AraFontSize.simpleReadingLarge,
                        fontWeight: FontWeight.w300,
                      ),
                    ),
                  ],
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
            ],
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
            if (live.phase == PolarAlignStates.checked)
              FilledButton(
                key: const Key('simple-polar-done'),
                style: kSimpleGoStyle,
                onPressed: busy ? null : () => _done(inSimplePreflight),
                child: Text(_doneLabel(l10n, inSimplePreflight)),
              ),
            // After Done (or a Stop), and beside a verdict: Check again, a new
            // measurement that says how far it turns.
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
                onPressed: busy ? null : _check,
                child: Text(l10n.polarCheck),
              ),
            ],
            // #2072 inside the tolerance, but not with twice its uncertainty:
            // the measurement cannot confirm it.
            if (unconfirmed != null &&
                active &&
                !motorsBusy &&
                (live.phase == PolarAlignStates.adjusting ||
                    live.phase == PolarAlignStates.paused))
              Text(
                unconfirmed,
                key: const Key('simple-polar-unconfirmed'),
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
                maxLines: 4,
              ),
            if (checkCaption != null &&
                active &&
                !motorsBusy &&
                (live.phase == PolarAlignStates.adjusting ||
                    live.phase == PolarAlignStates.paused))
              Text(
                checkCaption,
                key: const Key('simple-polar-verify-caption'),
                style: AraText.caption,
                maxLines: 3,
              ),
            if (active &&
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
                      onPressed: !busy && inTolerance ? _check : null,
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
    // The main camera's frames (the profile's camera, #2082): the guider has
    // no picture.
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
