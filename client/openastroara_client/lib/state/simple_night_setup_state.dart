import 'dart:async';
import 'package:flutter/material.dart' show IconData, Icons;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../l10n/l10n.dart';
import '../screens/simple/simple_plan_panel.dart'
    show
        simpleDarkStartProvider,
        simpleRunProvider,
        simpleScheduleAllowedProvider;
import '../screens/simple/simple_polar_panel.dart' show simplePolarActive;
import '../util/friendly_error.dart';
import 'app_locale_state.dart';
import 'polar_align/polar_align_state.dart';
import 'setup/setup_readiness.dart';
import 'simple_messages_state.dart';
import 'simple_mode_state.dart';

enum SimpleModeScreen { nightSetup, preview, focus, polarAlign, plan }

extension SimpleModeScreenLabels on SimpleModeScreen {
  String labelOf(AppLocalizations l10n) => switch (this) {
    SimpleModeScreen.nightSetup => l10n.shellModeNightSetup,
    SimpleModeScreen.preview => l10n.shellModePreview,
    SimpleModeScreen.polarAlign => l10n.shellModePolarAlign,
    SimpleModeScreen.focus => l10n.shellModeFocus,
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
    SimpleModeScreen.plan => Icons.playlist_play,
  };
}

/// What Simple Mode shows. Two writers: the person's pick, and Night Setup's
/// steps. [SimpleModeScreen.nightSetup] is only ever picked: it starts Night
/// Setup, whose first step is what shows.
class SimpleShowingNotifier extends Notifier<SimpleModeScreen> {
  @override
  SimpleModeScreen build() {
    // Simple Mode opens on Preview, as when the shell held what shows; the
    // pick also ends a Night Setup left running (its strip was hidden before).
    ref.listen(simpleModeProvider, (prev, next) {
      if (prev?.value == false && next.value == true) {
        pick(SimpleModeScreen.preview);
      }
    });
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
/// align; the last one's Done starts imaging.
enum NightSetupStep { plan, focus, polarAlign }

extension NightSetupStepLabel on NightSetupStep {
  String label(AppLocalizations l10n) => switch (this) {
    NightSetupStep.focus => l10n.nightSetupStepFocus,
    NightSetupStep.polarAlign => l10n.nightSetupStepPolarAlign,
    NightSetupStep.plan => l10n.nightSetupStepPlan,
  };

  /// The mode each step runs on.
  SimpleModeScreen get screen => switch (this) {
    NightSetupStep.plan => SimpleModeScreen.plan,
    NightSetupStep.focus => SimpleModeScreen.focus,
    NightSetupStep.polarAlign => SimpleModeScreen.polarAlign,
  };
}

/// The step to start on (always the plan), or go to next from [after]: the
/// first setup step not done tonight, polar align skipped on a permanent
/// pier; null when none is left, which starts imaging. Pure — unit-tested.
NightSetupStep? nightSetupNext({
  NightSetupStep? after,
  required bool focused,
  required bool aligned,
  required bool permanentPier,
}) {
  for (final s in NightSetupStep.values) {
    if (after != null && s.index <= after.index) continue;
    if (s == NightSetupStep.focus && focused) continue;
    if (s == NightSetupStep.polarAlign && (aligned || permanentPier)) continue;
    return s;
  }
  return null;
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
      if (state != null && next.started) {
        state = null;
        ref.read(simpleShowingProvider.notifier)._show(SimpleModeScreen.plan);
      }
    });
    return null;
  }

  NightSetupStep? _next(NightSetupStep? after) => nightSetupNext(
    after: after,
    focused: ref.read(setupFocusStateProvider) == SetupStepState.done,
    aligned: ref.read(setupPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.read(simplePermanentPierProvider).value ?? false,
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
    if (step == NightSetupStep.polarAlign &&
        !simplePolarActive(ref.read(polarAlignLiveProvider))) {
      final api = ref.read(polarAlignApiProvider);
      if (api != null) unawaited(_polar(api.start, (l) => l.polarActionStart));
    }
  }

  /// A polar start or stop; one that fails says why in a pill (a step whose
  /// routine never started, or a mount still moving).
  Future<void> _polar(
    Future<void> Function() op,
    String Function(AppLocalizations l10n) action,
  ) async {
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
    if (api != null) unawaited(_polar(api.stop, (l) => l.polarActionStop));
  }
}

final nightSetupProvider = NotifierProvider<NightSetupNotifier, NightSetupStep?>(
  NightSetupNotifier.new,
);
