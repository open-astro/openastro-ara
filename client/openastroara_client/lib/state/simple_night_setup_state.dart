import 'dart:async';
import 'package:flutter/material.dart' show IconData, Icons;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../l10n/l10n.dart';
import '../screens/simple/simple_plan_panel.dart'
    show
        simpleDarkStartProvider,
        simpleRunProvider,
        simpleScheduleAllowedProvider;
import '../screens/simple/simple_guide_panel.dart'
    show simpleGuideActive, simpleGuiding;
import '../screens/simple/simple_polar_panel.dart' show simplePolarActive;
import '../models/polar_align.dart' show PolarAlignStates;
import '../util/friendly_error.dart';
import 'app_locale_state.dart';
import 'guider/guider_state.dart';
import 'imaging/capture_progress_state.dart';
import 'imaging/live_view_state.dart';
import 'sequencer/sequence_list_state.dart';
import 'polar_align/polar_align_state.dart';
import 'setup/setup_readiness.dart';
import 'simple_messages_state.dart';
import 'simple_mode_state.dart';

enum SimpleModeScreen { nightSetup, preview, focus, polarAlign, guide, plan }

extension SimpleModeScreenLabels on SimpleModeScreen {
  String labelOf(AppLocalizations l10n) => switch (this) {
    SimpleModeScreen.nightSetup => l10n.shellModeNightSetup,
    SimpleModeScreen.preview => l10n.shellModePreview,
    SimpleModeScreen.polarAlign => l10n.shellModePolarAlign,
    SimpleModeScreen.focus => l10n.shellModeFocus,
    SimpleModeScreen.guide => l10n.shellModeGuide,
    SimpleModeScreen.plan => l10n.shellModePlan,
  };

  /// The name on the 60 px rail, where "Polar align" doesn't fit.
  String railLabelOf(AppLocalizations l10n) => switch (this) {
    SimpleModeScreen.polarAlign => l10n.shellRailPolarAlign,
    SimpleModeScreen.nightSetup => l10n.shellRailNightSetup,
    _ => labelOf(l10n),
  };

  IconData get icon => switch (this) {
    SimpleModeScreen.nightSetup => Icons.rocket_launch_outlined,
    SimpleModeScreen.preview => Icons.visibility_outlined,
    SimpleModeScreen.focus => Icons.center_focus_strong,
    SimpleModeScreen.polarAlign => Icons.explore_outlined,
    SimpleModeScreen.guide => Icons.track_changes,
    SimpleModeScreen.plan => Icons.playlist_play,
  };
}

/// Simple Mode is showing: the feature AND the mode, read from the two sources
/// (a derived Provider with no reader is not recomputed).
bool _simpleModeActive(Ref ref) =>
    (ref.read(simpleModeFeatureProvider).value ?? false) &&
    (ref.read(simpleModeProvider).value ?? false);

/// What Simple Mode shows. Two writers: the person's pick, and Night Setup's
/// steps. [SimpleModeScreen.nightSetup] is only ever picked: it starts Night
/// Setup, whose first step is what shows.
class SimpleShowingNotifier extends Notifier<SimpleModeScreen> {
  @override
  SimpleModeScreen build() {
    // Simple Mode opens on Preview, as when the shell held what shows; the
    // pick also ends a Night Setup left running (its strip was hidden before).
    // The shell mounts on the feature AND the mode (simpleModeActiveProvider),
    // so both toggles count; the two sources are listened to, as a derived
    // Provider with no reader is not recomputed.
    var was = _simpleModeActive(ref);
    void opened(Object? prev, Object? next) {
      final now = _simpleModeActive(ref);
      if (!was && now) pick(SimpleModeScreen.preview);
      was = now;
    }

    ref.listen(simpleModeProvider, opened);
    ref.listen(simpleModeFeatureProvider, opened);
    return SimpleModeScreen.preview;
  }

  /// The mode grid, the rail, a search hit: picking a mode leaves Night Setup.
  void pick(SimpleModeScreen m) {
    final setup = ref.read(nightSetupProvider.notifier);
    if (m == SimpleModeScreen.nightSetup) {
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

/// Night Setup's steps, in the night's order: tonight's targets first, then
/// focus (polar alignment plate-solves and fails on a soft focus), then polar
/// align, then guide (user, 2026-11-04); the last one's Done starts
/// imaging.
enum NightSetupStep { plan, focus, polarAlign, guide }

extension NightSetupStepLabel on NightSetupStep {
  String label(AppLocalizations l10n) => switch (this) {
    NightSetupStep.focus => l10n.nightSetupStepFocus,
    NightSetupStep.polarAlign => l10n.nightSetupStepPolarAlign,
    NightSetupStep.plan => l10n.nightSetupStepPlan,
    NightSetupStep.guide => l10n.nightSetupStepGuide,
  };

  /// The mode each step runs on.
  SimpleModeScreen get screen => switch (this) {
    NightSetupStep.plan => SimpleModeScreen.plan,
    NightSetupStep.focus => SimpleModeScreen.focus,
    NightSetupStep.polarAlign => SimpleModeScreen.polarAlign,
    NightSetupStep.guide => SimpleModeScreen.guide,
  };
}

/// The step to start on (always the plan), or go to next from [after]: the
/// first setup step not done tonight, polar align skipped on a permanent
/// pier, guide skipped when already guiding or with no guider ([guided]);
/// null when none is left, which starts imaging. Pure — unit-tested.
NightSetupStep? nightSetupNext({
  NightSetupStep? after,
  required bool focused,
  required bool aligned,
  required bool permanentPier,
  required bool guided,
}) {
  for (final s in NightSetupStep.values) {
    if (after != null && s.index <= after.index) continue;
    if (s == NightSetupStep.focus && focused) continue;
    if (s == NightSetupStep.polarAlign && (aligned || permanentPier)) continue;
    if (s == NightSetupStep.guide && guided) continue;
    return s;
  }
  return null;
}

/// The main camera is taking frames, as the Guide shutter reads it
/// (simpleMainCameraBusy with a paused Queue idle, read once).
bool _mainCameraBusy(Ref ref) {
  final queue = ref.read(sequenceRunStateProvider).value?.state;
  return ref.read(captureProgressProvider).isCapturing ||
      ref.read(liveViewControllerProvider) ||
      ((queue?.isActive ?? false) && !queue!.isAnyPaused);
}

/// The guide step has nothing to do: guiding already, or no guider to guide
/// with (an unguided rig images without it).
bool _guided(Ref ref) {
  final g = ref.read(guiderStatusProvider).value;
  return g == null || !g.isConnected || simpleGuiding(g);
}

/// [nightSetupNext] from the rig's state now, watched: the step after [after].
NightSetupStep? simpleNightSetupNext(
  WidgetRef ref, {
  required NightSetupStep after,
}) {
  final g = ref.watch(guiderStatusProvider).value;
  return nightSetupNext(
    after: after,
    focused: ref.watch(setupFocusStateProvider) == SetupStepState.done,
    aligned: ref.watch(setupPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.watch(simplePermanentPierProvider).value ?? false,
    guided: g == null || !g.isConnected || simpleGuiding(g),
  );
}

/// Simple Mode's Night Setup: the night's steps in order, each handing over to
/// the next. Null while it isn't running. The plan queues tonight's targets;
/// a finished autofocus moves on to polar align, which starts its routine on
/// entry; the last step's Done starts imaging, which ends Night Setup.
class NightSetupNotifier extends Notifier<NightSetupStep?> {
  @override
  NightSetupStep? build() {
    ref.listen(setupFocusStateProvider, (prev, next) {
      if (state == NightSetupStep.focus &&
          prev != SetupStepState.done &&
          next == SetupStepState.done) {
        // The last step waits for its Done: imaging never starts on its own.
        if (_next(NightSetupStep.focus) case final n?) _go(n);
      }
    });
    ref.listen(simpleRunProvider, (prev, next) {
      // Imaging started (the last Done, or by hand): Night Setup is done. The
      // last Done already ended its polar routine; this stops nothing again.
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
    return null;
  }

  NightSetupStep? _next(NightSetupStep? after) => nightSetupNext(
    after: after,
    focused: ref.read(setupFocusStateProvider) == SetupStepState.done,
    aligned: ref.read(setupPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.read(simplePermanentPierProvider).value ?? false,
    guided: _guided(ref),
  );

  /// The polar step's Done completed its routine: leaving stops nothing.
  bool _polarFinished = false;

  void _go(NightSetupStep step) {
    if (state == NightSetupStep.polarAlign && step != NightSetupStep.polarAlign) {
      _endPolar();
    }
    if (step == NightSetupStep.polarAlign) _polarFinished = false;
    state = step;
    ref.read(simpleShowingProvider.notifier)._show(step.screen);
    // Entering polar align starts the routine (an Night Setup-only exception
    // to the shutter starting things; its red square still stops it).
    // Not over a finished check: coming back to the step must show the
    // verdict, not wipe it and send the mount Home again unasked.
    final polarLive = ref.read(polarAlignLiveProvider);
    if (step == NightSetupStep.polarAlign &&
        !simplePolarActive(polarLive) &&
        polarLive.phase != PolarAlignStates.checked) {
      final api = ref.read(polarAlignApiProvider);
      if (api != null) unawaited(_stepAction(api.start, (l) => l.polarActionStart));
    }
    // Entering guide starts the guider's routine, the same exception; leaving
    // the step keeps it guiding (imaging wants it). Not while the main camera
    // is busy (a shot, a loop, a plan's exposure): calibration turns the
    // mount, the same gate as the Guide shutter, which then says why.
    if (step == NightSetupStep.guide &&
        !simpleGuideActive(ref.read(guiderStatusProvider).value) &&
        !_mainCameraBusy(ref)) {
      unawaited(
        _stepAction(
          ref.read(guiderStatusProvider.notifier).startGuiding,
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
  void start() => _go(_next(null) ?? NightSetupStep.plan);

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
      _go(NightSetupStep.plan); // the plan was skipped: nothing to start yet
      return;
    }
    if (s == NightSetupStep.polarAlign) _endPolar();
    // Already imaging (Night Setup started over a running queue): done.
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

  /// The polar step's Done: its routine is complete, then on as [next].
  void polarDone() {
    _polarFinished = true;
    next();
  }

  void back() {
    final s = state;
    if (s != null && s.index > 0) _go(NightSetupStep.values[s.index - 1]);
  }

  /// Ends on the plan, where imaging is watched.
  void stop() {
    _end();
    ref.read(simpleShowingProvider.notifier)._show(SimpleModeScreen.plan);
  }

  /// Ends without touching what shows: a pick of another mode shows that.
  void _end() {
    if (state == NightSetupStep.polarAlign) _endPolar();
    state = null;
  }

  /// Leaving the polar step (Skip, Back, another mode) with its routine still
  /// running stops it: it moves the mount, and its stop button leaves the
  /// screen with the step. Done has already finished it.
  void _endPolar() {
    if (_polarFinished ||
        !simplePolarActive(ref.read(polarAlignLiveProvider))) {
      return;
    }
    final api = ref.read(polarAlignApiProvider);
    if (api != null) unawaited(_stepAction(api.stop, (l) => l.polarActionStop));
  }
}

final nightSetupProvider = NotifierProvider<NightSetupNotifier, NightSetupStep?>(
  NightSetupNotifier.new,
);
