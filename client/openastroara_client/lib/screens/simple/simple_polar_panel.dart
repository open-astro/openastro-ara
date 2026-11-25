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
import '../../state/polar_align/polar_align_flow_state.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_aligner_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../widgets/settings/section_load_state.dart';
import '../../widgets/imaging/polar_align_readout.dart';
import '../../widgets/imaging/polar_aligner_block.dart';
import '../../widgets/imaging/polar_flow.dart';
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

/// The rail's shutter in Polar align: starts the run once the mount is at
/// Home (the panel's Home button gets it there); while a run goes, or the
/// mount goes Home, its square stops it.
class SimplePolarShutter extends ConsumerWidget {
  const SimplePolarShutter({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final api = ref.watch(polarAlignApiProvider);
    final active = ref.watch(polarAlignLiveProvider).isActive;
    // Busy while any panel's call runs: a second press would send a start
    // twice. Stop never waits, as it halts a routine that moves the mount.
    final busy = ref.watch(polarAlignActionProvider).busy;
    final read = ref.watch(polarReadyGateProvider);
    final gate = read.value;
    final goingHome = !active && gate == PolarReadyGate.goingHome;
    // A mount that could not be read: the rig judges the start, as in the panel.
    final startable = gate == PolarReadyGate.start || (gate == null && read is AsyncError);
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
      running: active || goingHome,
      label: active || goingHome ? l10n.polarStop : l10n.polarStart,
      whyOff: api == null
          ? l10n.shellRigNotConnected
          : switch (gate) {
              PolarReadyGate.home => l10n.polarHomeHint,
              PolarReadyGate.parked => l10n.refusalPolarAlignMountParked,
              PolarReadyGate.moving => l10n.polarMountMoving,
              PolarReadyGate.noMount => l10n.refusalMountNotConnected,
              _ => l10n.shellMainCameraBusy,
            },
      onPressed: api == null
          ? null
          : active
          ? () => run(l10n.polarActionStop, (api) => api.stop())
          : goingHome
          ? () => run(l10n.polarActionStopMount, (_) => polarStopGoingHome(ref))
          // It turns the mount: never under the main camera's frame.
          : startable && !busy && !ref.watch(mainCameraBusyProvider)
          ? () => run(l10n.polarActionStart, (api) => api.start())
          : null,
    );
  }
}

/// Polar align's panel beside the right rail, one step at a time
/// ([PolarFlowStep]): Home then Start with the routine's settings (exposure,
/// Loop/Single); a calm line while the rig measures; the bullseye, the
/// altitude and azimuth turns, Take frame (Single) and Done (the user's call,
/// whenever they are happy) while the user turns the bolts; then the verdict,
/// with Done and Adjust again. The rail's shutter starts and stops it.
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

  /// Done on the result: the run is over and the mount is Home, so nothing is
  /// sent. In Preflight it moves on to the next step.
  void _leaveResult(bool inSimplePreflight) {
    ref.read(polarAlignLiveProvider.notifier).dismissResult();
    if (inSimplePreflight) ref.read(simplePreflightProvider.notifier).next();
  }

  String _resultDoneLabel(AppLocalizations l10n, bool inSimplePreflight) =>
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
    // Null until the profile's copy is in from the rig (ADR 0015): the
    // settings say so, with Retry, and no readout is judged against a made-up
    // tolerance.
    final loaded = ref.watch(polarAlignSettingsProvider);
    final settings = loaded.value;
    final command = ref.watch(polarAlignActionProvider);
    final busy = command.busy;
    final southern = (ref.watch(siteSettingsProvider).value?.latitudeDeg ?? 0) < 0;
    final step = polarFlowStep(live);
    final adjusting = step == PolarFlowStep.adjusting;
    final total = live.totalErrorArcmin;
    // #2072 inside the tolerance on the error alone, but not with twice its
    // 1σ: the panel says the measurement cannot confirm it. Done never waits:
    // it is the user's call.
    final unconfirmed = polarUnconfirmedText(l10n, total, live.totalSigmaArcmin, settings?.targetToleranceArcmin);
    final color = zoneColor(total);
    // #1971 while the Polar Aligner's motors work, the arrows dim and Done
    // steps aside (user, 2026-11-16).
    final motorsBusy = polarAlignerMotorsBusy(live);
    final aligner = polarAlignerShown(ref.watch(polarAlignerProvider));
    // At large text the tiles go under the bullseye at their own height, and
    // the settings stack: a caption and a two-line move no longer fit 86 px.
    final stack = simpleStackText(context);

    Widget caution(String text, Key key) => Text(
      text,
      key: key,
      style: kSimpleText.copyWith(color: AraColors.accentBusy),
      maxLines: 4,
    );

    // A command the rig did not take, in every step.
    final failure = command.error == null
        ? null
        : Text(
            command.error!,
            key: const Key('simple-polar-error'),
            style: kSimpleText.copyWith(color: AraColors.errorText),
            maxLines: 4,
            overflow: TextOverflow.ellipsis,
          );

    // The settings for the next frame: a row each like the Focus panel's
    // choices (user, 2026-11-04: one row read oddly); side by side while the
    // user adjusts, so the bullseye panel still fits an iPad.
    final sideBySide = adjusting && !stack;
    final setup = settings == null
        ? SectionNotLoaded(
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
        : Flex(
            key: const Key('simple-polar-setup'),
            direction: sideBySide ? Axis.horizontal : Axis.vertical,
            crossAxisAlignment: sideBySide
                ? CrossAxisAlignment.center
                : CrossAxisAlignment.stretch,
            spacing: AraSpace.s8,
            children: [
              for (final w in <Widget>[
                SimpleSegments<String>(
                  options: [
                    (PolarAlignLoopModes.loop, l10n.polarLoop, 'loop'),
                    (PolarAlignLoopModes.single, l10n.polarSingle, 'single'),
                  ],
                  selected: settings.loopMode,
                  onPick: (mode) => _save((s) => s.copyWith(loopMode: mode)),
                ),
                SimplePresetTile<Duration>(
                  key: const Key('simple-polar-exposure'),
                  label: l10n.polarExposure,
                  value: _exposure(settings),
                  options: exposurePresets(minSec: 0, maxSec: 60),
                  format: formatExposure,
                  onSelected: (d) => _save(
                    (s) => s.copyWith(exposureSeconds: d.inMilliseconds / 1000),
                  ),
                ),
              ])
                sideBySide ? Expanded(child: w) : w,
            ],
          );

    final List<Widget> children = switch (step) {
      PolarFlowStep.ready => [
        if (polarReadyNote(l10n, live) case final note?)
          Text(
            note.text,
            key: const Key('simple-polar-ready-note'),
            style: kSimpleText.copyWith(
              color: note.error ? AraColors.errorText : AraColors.textSecondary,
            ),
            maxLines: 6,
            overflow: TextOverflow.ellipsis,
          ),
        ?failure,
        PolarReadyControl(
          primaryStyle: kSimpleGoStyle,
          lineStyle: kSimpleCaption,
          // It turns the mount: never under the main camera's frame.
          startBlocked: ref.watch(mainCameraBusyProvider) ? l10n.shellMainCameraBusy : null,
        ),
        const SizedBox(height: AraSpace.s8),
        setup,
        // Its own fixed slot under the settings (user, 2026-11-16).
        if (aligner) const PolarAlignerBlock(),
      ],
      PolarFlowStep.measuring || PolarFlowStep.goingHome || PolarFlowStep.checking => [
        const SizedBox(height: AraSpace.s8),
        PolarWorkingLine(polarFlowLine(l10n, live) ?? '', style: kSimpleText),
        const SizedBox(height: AraSpace.s8),
        ?failure,
        if (aligner && step == PolarFlowStep.measuring) const PolarAlignerBlock(),
      ],
      PolarFlowStep.adjusting => [
        setup,
        if (aligner) const PolarAlignerBlock(),
        // #2072 the camera sits where the readout reads the altitude bolt poorly.
        if (live.readoutCaution == PolarAlignReadoutCautions.altitudeWeak)
          caution(l10n.polarReadoutCautionAltitudeWeak, const Key('simple-polar-readout-caution')),
        // #2082 a point's frames stayed scattered after their retake: the
        // readout rests on noisy solves.
        if (live.measurementNoisy)
          caution(l10n.polarNoisyMeasurement, const Key('simple-polar-noisy')),
        if (total != null) ..._readout(l10n, live, southern, color, motorsBusy, stack),
        // The full panel's nudge, cut to two lines for the 248 px panel.
        if (_bolt.nudge case final altitude?)
          Text(
            l10n.polarBoltWorse(altitude ? l10n.polarAltitude : l10n.polarAzimuth),
            style: kSimpleText.copyWith(color: AraColors.accentBusy),
          ),
        if (live.consecutiveSolveFailures > 0)
          Text(
            l10n.polarNoSolve(live.consecutiveSolveFailures),
            style: kSimpleText.copyWith(color: AraColors.accentBusy),
          ),
        ?failure,
        // #2072 inside the tolerance, but not with twice its uncertainty:
        // the measurement cannot confirm it.
        if (unconfirmed != null && !motorsBusy)
          caution(unconfirmed, const Key('simple-polar-unconfirmed')),
        Row(
          spacing: AraSpace.s8,
          children: [
            // Single waits for a tap before each frame; Loop shoots on its own.
            if (settings != null && _single(settings))
              Expanded(
                child: FilledButton.tonal(
                  key: const Key('simple-polar-capture'),
                  onPressed: busy
                      ? null
                      : () => _run(l10n.polarActionCapture, (api) => api.requestCapture()),
                  child: Text(l10n.polarTakeFrame),
                ),
              ),
            if (!motorsBusy)
              Expanded(
                child: FilledButton(
                  key: const Key('simple-polar-done'),
                  style: kSimpleGoStyle,
                  onPressed: busy ? null : () => _run(l10n.polarActionComplete, (api) => api.complete()),
                  child: Text(l10n.polarDone),
                ),
              ),
          ],
        ),
      ],
      PolarFlowStep.returningHome => [
        const SizedBox(height: AraSpace.s8),
        PolarVerdictView(result: live.checkResult!, compact: true),
        const SizedBox(height: AraSpace.s8),
        PolarWorkingLine(l10n.polarReturningHome, style: kSimpleCaption),
        ?failure,
      ],
      PolarFlowStep.result => [
        const SizedBox(height: AraSpace.s8),
        PolarVerdictView(result: live.checkResult!, compact: true),
        const SizedBox(height: AraSpace.s8),
        ?failure,
        FilledButton(
          key: const Key('simple-polar-result-done'),
          style: kSimpleGoStyle,
          onPressed: () => _leaveResult(inSimplePreflight),
          child: Text(_resultDoneLabel(l10n, inSimplePreflight)),
        ),
        // A new run: the mount is Home again.
        OutlinedButton(
          key: const Key('simple-polar-adjust-again'),
          onPressed: busy ? null : () => _run(l10n.polarActionStart, (api) => api.start()),
          child: Text(l10n.polarAdjustAgain),
        ),
      ],
    };

    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 420,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: children,
        ),
      ),
    );
  }

  /// The live readout while the user turns the bolts: the total error with
  /// its zone, the bullseye, and a tile per axis with the turn that closes it.
  List<Widget> _readout(
    AppLocalizations l10n,
    PolarAlignLive live,
    bool southern,
    Color color,
    bool motorsBusy,
    bool stack,
  ) {
    final total = live.totalErrorArcmin!;
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
    final bullseye = SizedBox.square(
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
    return [
      Row(
        mainAxisAlignment: MainAxisAlignment.end,
        spacing: AraSpace.s8,
        children: [
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
      ),
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
    ];
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
