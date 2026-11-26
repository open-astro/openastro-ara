import 'dart:async';
import 'package:flutter/material.dart' show IconData, Icons;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../l10n/l10n.dart';
import 'sequencer/sequence_list_state.dart' show sequenceRunStateProvider;
import 'simple_run_state.dart'
    show
        SimpleRunPhase,
        simpleDarkStartProvider,
        simplePlanTotal,
        simpleRunPhase,
        simpleRunProvider,
        simpleScheduleAllowedProvider;
import '../models/polar_align.dart' show PolarAlignLastResult, PolarAlignStates;
import '../models/autofocus_run.dart' show AutofocusRunStates;
import '../util/friendly_error.dart';
import 'app_locale_state.dart';
import 'guider/guider_state.dart';
import 'imaging/main_camera_state.dart';
import 'focus/autofocus_live_state.dart';
import 'polar_align/polar_align_state.dart';
import 'setup/setup_readiness.dart';
import 'simple_messages_state.dart';
import 'simple_mode_state.dart';

enum SimpleModeScreen {
  preflight,
  preview,
  focus,
  polarAlign,
  guide,
  calibration,
  plan,
}

/// The right rail's tabs, top to bottom: the order of the night (#1911).
/// Preflight is no tab; it keeps a temporary row under them until it goes.
const railOrder = <SimpleModeScreen>[
  SimpleModeScreen.preview,
  SimpleModeScreen.focus,
  SimpleModeScreen.polarAlign,
  SimpleModeScreen.guide,
  SimpleModeScreen.plan,
  SimpleModeScreen.calibration,
];

extension SimpleModeScreenLabels on SimpleModeScreen {
  String labelOf(AppLocalizations l10n) => switch (this) {
    SimpleModeScreen.preflight => l10n.shellModePreflight,
    SimpleModeScreen.preview => l10n.shellModePreview,
    SimpleModeScreen.polarAlign => l10n.shellModePolarAlign,
    SimpleModeScreen.focus => l10n.shellModeFocus,
    SimpleModeScreen.guide => l10n.shellModeGuide,
    SimpleModeScreen.calibration => l10n.shellModeCalibration,
    SimpleModeScreen.plan => l10n.shellModePlan,
  };

  /// The name on the 60 px rail, where "Polar align" doesn't fit.
  String railLabelOf(AppLocalizations l10n) => switch (this) {
    SimpleModeScreen.polarAlign => l10n.shellRailPolarAlign,
    SimpleModeScreen.preflight => l10n.shellRailPreflight,
    _ => labelOf(l10n),
  };

  IconData get icon => switch (this) {
    SimpleModeScreen.preflight => Icons.rocket_launch_outlined,
    SimpleModeScreen.preview => Icons.visibility_outlined,
    SimpleModeScreen.focus => Icons.center_focus_strong,
    SimpleModeScreen.polarAlign => Icons.explore_outlined,
    SimpleModeScreen.guide => Icons.track_changes,
    SimpleModeScreen.calibration => Icons.layers_outlined,
    SimpleModeScreen.plan => Icons.playlist_play,
  };
}

/// Simple Mode is showing: Companion Mode, or the feature AND the mode, read
/// from the sources (a derived Provider with no reader is not recomputed).
bool _simpleModeActive(Ref ref) =>
    ref.read(companionModeProvider) ||
    ((ref.read(simpleModeFeatureProvider).value ?? false) &&
        (ref.read(simpleModeProvider).value ?? false));

/// What Simple Mode shows. Two writers: the person's pick, and Preflight's
/// steps. [SimpleModeScreen.preflight] is only ever picked: it starts
/// Preflight, whose first step is what shows.
class SimpleShowingNotifier extends Notifier<SimpleModeScreen> {
  @override
  SimpleModeScreen build() {
    // Simple Mode opens on Preview, as when the shell held what shows; the
    // pick also ends a Preflight left running (its strip was hidden before).
    // The shell mounts on Companion Mode, or the feature AND the mode
    // (simpleModeActiveProvider), so all three count; each source is listened
    // to, as a derived Provider with no reader is not recomputed.
    var was = _simpleModeActive(ref);
    void opened(Object? prev, Object? next) {
      final now = _simpleModeActive(ref);
      if (!was && now) pick(SimpleModeScreen.preview);
      was = now;
    }

    ref.listen(simpleModeProvider, opened);
    ref.listen(simpleModeFeatureProvider, opened);
    ref.listen(companionModeProvider, opened);
    return SimpleModeScreen.preview;
  }

  /// The mode grid, the rail, a search hit: picking a mode leaves Preflight.
  void pick(SimpleModeScreen m) {
    final setup = ref.read(simplePreflightProvider.notifier);
    if (m == SimpleModeScreen.preflight) {
      setup.start();
      return;
    }
    setup._end();
    state = m;
  }

  void _show(SimpleModeScreen m) => state = m;
}

final simpleShowingProvider =
    NotifierProvider<SimpleShowingNotifier, SimpleModeScreen>(
      SimpleShowingNotifier.new,
    );

/// Preflight's steps, in the night's order: tonight's targets first, then
/// focus (polar alignment plate-solves and fails on a soft focus), then polar
/// align, then guide (user, 2026-11-04); the last one's Done starts
/// imaging.
enum SimplePreflightStep { plan, focus, polarAlign, guide }

extension SimplePreflightStepLabel on SimplePreflightStep {
  String label(AppLocalizations l10n) => switch (this) {
    SimplePreflightStep.focus => l10n.simplePreflightStepFocus,
    SimplePreflightStep.polarAlign => l10n.simplePreflightStepPolarAlign,
    SimplePreflightStep.plan => l10n.simplePreflightStepPlan,
    SimplePreflightStep.guide => l10n.simplePreflightStepGuide,
  };

  /// The mode each step runs on.
  SimpleModeScreen get screen => switch (this) {
    SimplePreflightStep.plan => SimpleModeScreen.plan,
    SimplePreflightStep.focus => SimpleModeScreen.focus,
    SimplePreflightStep.polarAlign => SimpleModeScreen.polarAlign,
    SimplePreflightStep.guide => SimpleModeScreen.guide,
  };
}

/// #1565 — the rig's last polar-align routine, read once per server so a
/// relaunch still knows the mount was aligned tonight, and again each time a
/// routine starts or ends, so a newer routine replaces it. [last] is null from
/// an older daemon, before any routine ended, or when the read fails; [state]
/// is the daemon's routine state at the read ([PolarAlignStates]), which
/// [doneTonightProvider] reads for a failed routine the live view never saw.
final polarAlignLastResultProvider =
    FutureProvider<({PolarAlignLastResult? last, String? state})>((ref) async {
      final api = ref.watch(polarAlignApiProvider);
      ref.watch(polarAlignLiveProvider.select((l) => l.isActive));
      if (api == null) return (last: null, state: null);
      try {
        final status = await api.getStatus();
        return (last: status?.lastResult, state: status?.state);
      } catch (_) {
        return (last: null, state: null);
      }
    });

/// Night Setup's focus step: the shared readiness plus a run the rig kept from
/// earlier tonight. (The clock is read when a dependency changes, which is
/// often enough for a step that only flips at the 12 h line.)
final nightFocusStateProvider = Provider<SetupStepState>(
  (ref) =>
      focusStepState(ref.watch(autofocusLiveProvider), now: DateTime.now()),
);

/// Night Setup's polar step: the shared readiness plus the rig's last routine.
final nightPolarAlignStateProvider = Provider<SetupStepState>(
  (ref) => polarAlignStepState(
    ref.watch(polarAlignLiveProvider),
    last: ref.watch(polarAlignLastResultProvider).value?.last,
    now: DateTime.now(),
  ),
);

/// Whether Focus counts as done tonight: the #1565 read AND the latest run did
/// not fail. A manual "in focus" mark since that run clears the failure.
bool focusDoneTonight(SetupStepState read, AutofocusLive live) =>
    read == SetupStepState.done &&
    !(live.run.state == AutofocusRunStates.failed && !live.manualMarkSinceRun);

/// Whether Polar align counts as done tonight: the #1565 read AND the latest
/// routine did not fail. A live phase other than idle wins; while it is idle
/// (a relaunch) the daemon's [fetchedState] says how the last routine ended.
bool polarDoneTonight(
  SetupStepState read,
  PolarAlignLive live,
  String? fetchedState,
) {
  final failed = live.phase == PolarAlignStates.idle
      ? fetchedState == PolarAlignStates.failed
      : live.phase == PolarAlignStates.failed;
  return read == SetupStepState.done && !failed;
}

/// The modes done tonight (Focus, Polar align), for the rail's ticks (#1911).
final doneTonightProvider = Provider<Set<SimpleModeScreen>>((ref) {
  final fetched = ref.watch(polarAlignLastResultProvider).value;
  return {
    if (focusDoneTonight(
      ref.watch(nightFocusStateProvider),
      ref.watch(autofocusLiveProvider),
    ))
      SimpleModeScreen.focus,
    if (polarDoneTonight(
      ref.watch(nightPolarAlignStateProvider),
      ref.watch(polarAlignLiveProvider),
      fetched?.state,
    ))
      SimpleModeScreen.polarAlign,
  };
});

/// The modes that run now, for the rail's dots: a plan imaging, Smart Focus,
/// a polar routine, the guider.
final railRunningProvider = Provider<Set<SimpleModeScreen>>((ref) {
  final phase = simpleRunPhase(
    ref.watch(simpleRunProvider),
    ref.watch(sequenceRunStateProvider).value,
  );
  return {
    if (phase == SimpleRunPhase.imaging || phase == SimpleRunPhase.pausing)
      SimpleModeScreen.plan,
    if (ref.watch(autofocusLiveProvider.select((l) => l.run.isRunning)))
      SimpleModeScreen.focus,
    if (ref.watch(polarAlignLiveProvider.select((l) => l.isActive)))
      SimpleModeScreen.polarAlign,
    if (ref.watch(guiderStatusProvider.select((s) => s.value?.isBusy ?? false)))
      SimpleModeScreen.guide,
  };
});

/// "12/40": the frames taken of the plan's total while it images; null else.
final railFigureProvider = Provider<String?>((ref) {
  final run = ref.watch(simpleRunProvider);
  final info = ref.watch(sequenceRunStateProvider).value;
  final phase = simpleRunPhase(run, info);
  if (run.targets.isEmpty ||
      info?.sequenceId != run.sequenceId ||
      (phase != SimpleRunPhase.imaging && phase != SimpleRunPhase.pausing)) {
    return null;
  }
  final total = run.targets.fold(
    0,
    (n, t) => n + simplePlanTotal(t.steps) + t.flatFrames,
  );
  return '${info?.framesCaptured ?? 0}/$total';
});

/// The step to start on (always the plan), or go to next from [after]: the
/// first setup step not done tonight, polar align skipped on a permanent
/// pier, guide skipped when already guiding or with no guider ([guided]);
/// null when none is left, which starts imaging. Pure — unit-tested.
SimplePreflightStep? preflightNextStep({
  SimplePreflightStep? after,
  required bool focused,
  required bool aligned,
  required bool permanentPier,
  required bool guided,
}) {
  for (final s in SimplePreflightStep.values) {
    if (after != null && s.index <= after.index) continue;
    if (s == SimplePreflightStep.focus && focused) continue;
    if (s == SimplePreflightStep.polarAlign && (aligned || permanentPier)) continue;
    if (s == SimplePreflightStep.guide && guided) continue;
    return s;
  }
  return null;
}

/// The guide step has nothing to do: guiding already, or no guider to guide
/// with (an unguided rig images without it).
bool _guided(Ref ref) {
  final g = ref.read(guiderStatusProvider).value;
  return g == null || !g.isConnected || g.isCorrecting;
}

/// [preflightNextStep] from the rig's state now, watched: the step after [after].
SimplePreflightStep? simplePreflightNext(
  WidgetRef ref, {
  required SimplePreflightStep after,
}) {
  final g = ref.watch(guiderStatusProvider).value;
  return preflightNextStep(
    after: after,
    focused: ref.watch(nightFocusStateProvider) == SetupStepState.done,
    aligned: ref.watch(nightPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.watch(simplePermanentPierProvider).value ?? false,
    guided: g == null || !g.isConnected || g.isCorrecting,
  );
}

/// Simple Mode's Preflight: the night's steps in order, each handing over to
/// the next. Null while it isn't running. The plan queues tonight's targets;
/// a finished autofocus moves on to polar align, whose run waits for Home and
/// Start; the last step's Done starts imaging, which ends Preflight.
class SimplePreflightNotifier extends Notifier<SimplePreflightStep?> {
  @override
  SimplePreflightStep? build() {
    ref.listen(nightFocusStateProvider, (prev, next) {
      if (state == SimplePreflightStep.focus &&
          prev != SetupStepState.done &&
          next == SetupStepState.done) {
        // The last step waits for its Done: imaging never starts on its own.
        if (_next(SimplePreflightStep.focus) case final n?) _go(n);
      }
    });
    ref.listen(simpleRunProvider, (prev, next) {
      // Imaging started (the last Done, or by hand): Preflight is done. The
      // polar result's Done found its run over; this stops nothing.
      // Not a run's updates while it images (ALP-4982).
      if (state != null && next.started && !(prev?.started ?? false)) {
        state = null;
        ref.read(simpleShowingProvider.notifier)._show(SimpleModeScreen.plan);
      }
    });
    // Closing Simple Mode ends the setup flow (not a queue it started): in
    // Advanced nothing here may move the mount or start the guider (#1513).
    var was = _simpleModeActive(ref);
    void closed(Object? prev, Object? next) {
      final now = _simpleModeActive(ref);
      if (was && !now) _end();
      was = now;
    }

    ref.listen(simpleModeProvider, closed);
    ref.listen(simpleModeFeatureProvider, closed);
    ref.listen(companionModeProvider, closed);
    return null;
  }

  SimplePreflightStep? _next(SimplePreflightStep? after) => preflightNextStep(
    after: after,
    focused: ref.read(nightFocusStateProvider) == SetupStepState.done,
    aligned: ref.read(nightPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.read(simplePermanentPierProvider).value ?? false,
    guided: _guided(ref),
  );

  void _go(SimplePreflightStep step) {
    if (state == SimplePreflightStep.polarAlign && step != SimplePreflightStep.polarAlign) {
      _endPolar();
    }
    state = step;
    ref.read(simpleShowingProvider.notifier)._show(step.screen);
    // Entering polar align does not start the run: it turns the mount, so
    // the user sends the mount Home and starts it.
    // Entering guide starts the guider's routine; leaving
    // the step keeps it guiding (imaging wants it). Not while the main camera
    // is busy (a shot, a loop, a plan's exposure): calibration turns the
    // mount, the same gate as the Guide shutter, which then says why.
    if (step == SimplePreflightStep.guide &&
        !(ref.read(guiderStatusProvider).value?.isBusy ?? false) &&
        !ref.read(mainCameraTakingFramesProvider)) {
      unawaited(
        _stepAction(
          ref.read(guiderStatusProvider.notifier).startGuidingTracked,
          (l) => l.guideActionStart,
          origin: SimpleStatusOrigin.guide,
        ),
      );
    }
  }

  /// A step's start or stop (polar align, guiding); one that fails says why
  /// in a pill (a step whose routine never started, or a mount still moving).
  Future<void> _stepAction(
    Future<void> Function() op,
    String Function(AppLocalizations l10n) action, {
    SimpleStatusOrigin origin = SimpleStatusOrigin.polar,
  }) async {
    try {
      await op();
    } catch (e) {
      if (!ref.mounted) return;
      final l10n = l10nFor(ref.read(appLocaleProvider));
      ref
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.error,
            friendlyError(l10n, e, action: action(l10n)),
            origin: origin,
          );
    }
  }

  // The plan always comes first; the fallback keeps a reordered list safe.
  void start() => _go(_next(null) ?? SimplePreflightStep.plan);

  /// The plan's Next, a Done, or Skip: on to the next step; from the last one,
  /// imaging starts (at dark when it is before dark, as the plan sheet's
  /// green button would), or, with nothing queued, back to the plan.
  void next() {
    final s = state;
    if (s == null) return;
    if (_next(s) case final n?) {
      _go(n);
      return;
    }
    if (ref.read(simpleRunProvider).sequenceId == null) {
      _go(SimplePreflightStep.plan); // the plan was skipped: nothing to start yet
      return;
    }
    if (s == SimplePreflightStep.polarAlign) _endPolar();
    // Already imaging (Preflight started over a running queue): done.
    if (ref.read(simpleRunProvider).started) {
      state = null;
      return;
    }
    final startAt = ref.read(simpleScheduleAllowedProvider)
        ? ref.read(simpleDarkStartProvider)
        : null;
    unawaited(
      ref.read(simpleRunProvider.notifier).startQueue(startAt: startAt),
    );
  }

  void back() {
    final s = state;
    if (s != null && s.index > 0) _go(SimplePreflightStep.values[s.index - 1]);
  }

  /// Ends on the plan, where imaging is watched.
  void stop() {
    _end();
    ref.read(simpleShowingProvider.notifier)._show(SimpleModeScreen.plan);
  }

  /// Ends without touching what shows: a pick of another mode shows that.
  void _end() {
    if (state == SimplePreflightStep.polarAlign) _endPolar();
    state = null;
  }

  /// Leaving the polar step (Skip, Back, another mode) with its run still
  /// going stops it: it moves the mount, and its stop button leaves the
  /// screen with the step. The result's Done finds it over: nothing to stop.
  void _endPolar() {
    if (!ref.read(polarAlignLiveProvider).isActive) return;
    final api = ref.read(polarAlignApiProvider);
    if (api != null) unawaited(_stepAction(api.stop, (l) => l.polarActionStop));
  }
}

final simplePreflightProvider = NotifierProvider<SimplePreflightNotifier, SimplePreflightStep?>(
  SimplePreflightNotifier.new,
);
