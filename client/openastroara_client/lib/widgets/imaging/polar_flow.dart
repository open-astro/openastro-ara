import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../services/polar_align_api.dart' show PolarAlignClient;
import '../../state/equipment/mount_home_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/night_mode_state.dart';
import '../../state/polar_align/polar_align_flow_state.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../util/polar_align_error_text.dart';
import '../adaptive_controls.dart';
import 'polar_align_readout.dart';

// The Polar Align flow's shared pieces, drawn the same in the Setup panel and
// Simple Mode (ADR 0013 rule 3): the words for each step, the Ready step's
// Home / Start control and the verdict.

/// The calm line of a running step ("Measuring — point 2 of 3", "Going
/// Home…"); null on Ready and on the result. Pure — unit-tested.
String? polarFlowLine(AppLocalizations l, PolarAlignLive live) =>
    switch (polarFlowStep(live)) {
      PolarFlowStep.measuring => switch (live.measuringPoint) {
        final point? => l.polarMeasuringPoint(point, 3),
        null => l.polarMeasuring,
      },
      PolarFlowStep.adjusting =>
        live.phase == PolarAlignStates.paused
            ? l.polarPhasePaused
            : l.polarPhaseAdjusting,
      PolarFlowStep.goingHome => l.polarPhaseHoming,
      // The solve being taken, not the count behind it.
      PolarFlowStep.checking => l.polarPhaseChecking(
        math.min(live.checkSolvesDone + 1, live.checkSolvesTotal),
        live.checkSolvesTotal,
      ),
      PolarFlowStep.returningHome => l.polarReturningHome,
      PolarFlowStep.ready || PolarFlowStep.result => null,
    };

/// A verdict token's word (`excellent` … `far_off`).
String polarVerdictLabel(AppLocalizations l, String token) => switch (token) {
  'excellent' => l.polarVerdictExcellent,
  'very_good' => l.polarVerdictVeryGood,
  'good' => l.polarVerdictGood,
  'rough' => l.polarVerdictRough,
  _ => l.polarVerdictFarOff,
};

/// What a verdict means for the night, in one sentence.
String polarVerdictMeaning(AppLocalizations l, String token) => switch (token) {
  'excellent' => l.polarVerdictExcellentMeaning,
  'very_good' => l.polarVerdictVeryGoodMeaning,
  'good' => l.polarVerdictGoodMeaning,
  'rough' => l.polarVerdictRoughMeaning,
  _ => l.polarVerdictFarOffMeaning,
};

/// A result's total with its 1σ, unsigned: `0.5′ ± 0.6′`.
String polarVerdictTotal(AppLocalizations l, PolarAlignCheckResult r) =>
    polarErrorWithSigmaText(
      l,
      '${r.totalErrorArcmin.abs().toStringAsFixed(1)}′',
      r.totalSigmaArcmin,
    );

/// What the last run left to say on the Ready step: why it failed, that it
/// was stopped, or the result the user left with Done. Null before any run.
/// Pure — unit-tested.
({String text, bool error})? polarReadyNote(
  AppLocalizations l,
  PolarAlignLive live,
) => switch (live.phase) {
  PolarAlignStates.failed => (
    text: polarAlignErrorText(l, live) ?? l.polarPhaseFailed,
    error: true,
  ),
  PolarAlignStates.stopped => (text: l.polarStoppedLine, error: false),
  PolarAlignStates.checked => switch (live.checkResult) {
    final r? => (
      text: l.polarLastResult(
        polarVerdictLabel(l, polarCheckVerdict(r)),
        polarVerdictTotal(l, r),
      ),
      error: false,
    ),
    null => null,
  },
  _ => null,
};

/// Stop while the mount goes Home: the Mount tab's own stop (abort the slew).
Future<void> polarStopGoingHome(WidgetRef ref) async {
  ref.read(mountHomeProvider.notifier).clear();
  await ref.read(mountProvider.notifier).abortSlew();
}

/// The Ready step's one action and its one quiet line: Home while the mount
/// can find Home and is not there (Going Home… with Stop while it moves),
/// then Start Polar Alignment. A failure shows as the routine's action error
/// ([polarAlignActionProvider]), where the panel around this says it.
class PolarReadyControl extends ConsumerWidget {
  const PolarReadyControl({
    super.key,
    this.primaryStyle,
    this.lineStyle = const TextStyle(
      fontSize: 14,
      color: AraColors.textSecondary,
      height: 1.4,
    ),
    this.startBlocked,
  });

  /// The primary button's style; the theme's filled button when null.
  final ButtonStyle? primaryStyle;
  final TextStyle lineStyle;

  /// Why Start is off on this surface (Simple Mode: the main camera is busy);
  /// null when nothing here holds it.
  final String? startBlocked;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final busy = ref.watch(polarAlignActionProvider).busy;
    // The profile's own turn: unsaid until it is in from the rig (ADR 0015).
    final turn = ref.watch(polarAlignSettingsProvider).value?.seedRotationDeg;

    Future<void> run(
      String verb,
      Future<void> Function(PolarAlignClient api) op,
    ) => ref
        .read(polarAlignActionProvider.notifier)
        .run(op, describe: (e) => friendlyError(l10n, e, action: verb));

    Widget primary(Key key, String label, VoidCallback? onPressed) =>
        FilledButton(
          key: key,
          style: primaryStyle,
          onPressed: onPressed,
          child: Text(label),
        );

    Widget line(String text) => Text(
      text,
      key: const Key('polar-align-ready-line'),
      textAlign: TextAlign.center,
      style: lineStyle,
    );

    Widget start({required bool enabled, String? why}) => _stack([
      primary(
        const Key('polar-align-start'),
        l10n.polarStart,
        enabled && !busy && startBlocked == null
            ? () => run(l10n.polarActionStart, (api) => api.start())
            : null,
      ),
      if (why ?? startBlocked case final text?)
        line(text)
      else if (turn != null)
        line(l10n.polarStartHint(turn.toStringAsFixed(0))),
    ]);

    return switch (ref.watch(polarReadyGateProvider)) {
      AsyncValue(:final value?) => switch (value) {
        PolarReadyGate.noMount => start(
          enabled: false,
          why: l10n.refusalMountNotConnected,
        ),
        PolarReadyGate.moving => start(
          enabled: false,
          why: l10n.polarMountMoving,
        ),
        PolarReadyGate.start => start(enabled: true),
        PolarReadyGate.parked => _stack([
          primary(
            const Key('polar-align-unpark'),
            l10n.polarUnpark,
            busy
                ? null
                : () => run(
                    l10n.polarActionUnpark,
                    (_) => ref.read(mountProvider.notifier).unpark(),
                  ),
          ),
          line(l10n.refusalPolarAlignMountParked),
        ]),
        PolarReadyGate.home => _stack([
          primary(
            const Key('polar-align-home'),
            l10n.polarHome,
            busy
                ? null
                : () => run(
                    l10n.polarActionHome,
                    (_) => ref.read(mountHomeProvider.notifier).home(),
                  ),
          ),
          line(l10n.polarHomeHint),
        ]),
        PolarReadyGate.goingHome => _stack([
          PolarWorkingLine(l10n.polarPhaseHoming, style: lineStyle),
          OutlinedButton(
            key: const Key('polar-align-home-stop'),
            // Stop never waits on another call: it halts a moving mount.
            onPressed: () =>
                run(l10n.polarActionStopMount, (_) => polarStopGoingHome(ref)),
            child: Text(l10n.polarStop),
          ),
        ]),
      },
      // The mount could not be read: say so, and let the rig judge a Start.
      AsyncError(:final error) => start(
        enabled: true,
        why: friendlyError(l10n, error, action: l10n.polarActionReadMount),
      ),
      _ => const Center(
        child: SizedBox.square(
          dimension: 24,
          child: AdaptiveProgressIndicator(strokeWidth: 2.5),
        ),
      ),
    };
  }

  static Widget _stack(List<Widget> children) => Column(
    mainAxisSize: MainAxisSize.min,
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: AraSpace.s12,
    children: children,
  );
}

/// A running step's calm line beside a spinner, read out when it changes.
class PolarWorkingLine extends StatelessWidget {
  const PolarWorkingLine(this.text, {super.key, required this.style});

  final String text;
  final TextStyle style;

  @override
  Widget build(BuildContext context) => Semantics(
    liveRegion: true,
    child: Row(
      mainAxisAlignment: MainAxisAlignment.center,
      spacing: AraSpace.s12,
      children: [
        const SizedBox.square(
          dimension: 18,
          child: AdaptiveProgressIndicator(strokeWidth: 2),
        ),
        Flexible(
          child: Text(
            text,
            key: const Key('polar-align-working'),
            style: style,
          ),
        ),
      ],
    ),
  );
}

/// The verdict of a run: the rating, the measured error with its 1σ, what it
/// means, and both axes. #2072 the rating is on the error plus twice its 1σ,
/// so a noisy measurement never reads Excellent. [compact] is Simple Mode's
/// panel; the Setup panel draws it large.
class PolarVerdictView extends ConsumerWidget {
  const PolarVerdictView({
    super.key,
    required this.result,
    this.compact = false,
  });

  final PolarAlignCheckResult result;
  final bool compact;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    // Night mode's red filter keeps only luminance: the zone hue would be the
    // dimmest thing on screen, so the words carry the meaning at full brightness.
    final night = switch (ref.watch(nightModeProvider)) {
      AsyncData(:final value) => value,
      _ => false,
    };
    final color = night
        ? AraColors.textPrimary
        : zoneColor(result.totalErrorArcmin);
    final token = polarCheckVerdict(result);
    final rated = polarRatedArcmin(
      result.totalErrorArcmin,
      result.totalSigmaArcmin,
    );
    final ratingSize = compact ? AraFontSize.simpleReadingLarge : 44.0;
    return Semantics(
      container: true,
      liveRegion: true,
      child: Column(
        key: const Key('polar-align-verdict'),
        mainAxisSize: MainAxisSize.min,
        spacing: compact ? AraSpace.s8 : AraSpace.s12,
        children: [
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              spacing: AraSpace.s12,
              children: [
                Icon(
                  polarErrorRatingIcon(rated),
                  size: ratingSize,
                  color: color,
                ),
                Text(
                  polarVerdictLabel(l10n, token),
                  key: const Key('polar-align-verdict-rating'),
                  style: TextStyle(
                    fontSize: ratingSize,
                    fontWeight: FontWeight.w700,
                    height: 1.1,
                    color: color,
                  ),
                ),
              ],
            ),
          ),
          FittedBox(
            fit: BoxFit.scaleDown,
            child: Text(
              l10n.polarVerdictTotal(polarVerdictTotal(l10n, result)),
              key: const Key('polar-align-verdict-total'),
              style: TextStyle(
                fontSize: compact ? AraFontSize.simpleReading : 28,
                fontWeight: FontWeight.w300,
                color: AraColors.textPrimary,
                fontFeatures: const [FontFeature.tabularFigures()],
              ),
            ),
          ),
          Text(
            polarVerdictMeaning(l10n, token),
            key: const Key('polar-align-verdict-meaning'),
            textAlign: TextAlign.center,
            style: TextStyle(
              fontSize: compact ? 14 : 16,
              color: AraColors.textPrimary,
              height: 1.4,
            ),
          ),
          Text(
            l10n.polarVerdictAxes(
              formatArcmin(result.altErrorArcmin),
              formatArcmin(result.azErrorArcmin),
            ),
            key: const Key('polar-align-verdict-axes'),
            textAlign: TextAlign.center,
            style: const TextStyle(
              fontSize: 14,
              color: AraColors.textSecondary,
              fontFeatures: [FontFeature.tabularFigures()],
            ),
          ),
          // #2082 a point of the measurement stayed scattered after its retake.
          if (result.noisy)
            Text(
              l10n.polarNoisyMeasurement,
              key: const Key('polar-align-verdict-noisy'),
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 14,
                color: AraColors.accentBusy,
                height: 1.4,
              ),
            ),
        ],
      ),
    );
  }
}
