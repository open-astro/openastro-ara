import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/app_locale_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_mode_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import 'simple_plan_panel.dart'
    show
        simpleDarkStartProvider,
        simpleRunProvider,
        simpleScheduleAllowedProvider;
import 'simple_polar_panel.dart' show simplePolarActive;
import 'simple_preview_panel.dart' show kSimpleText;
import 'simple_shell.dart' show SimplePanel, kSimpleGoStyle, simpleFrost;

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
      // Imaging started (the last Done, or by hand): Night Setup is done.
      if (state != null && next.started) state = null;
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

  void stop() {
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

/// Night Setup's step strip, centred under the status strip: the steps behind
/// ticked green when done tonight and amber when skipped, the current one
/// lit, then Back and Skip.
class NightSetupStrip extends ConsumerWidget {
  const NightSetupStrip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final step = ref.watch(nightSetupProvider);
    if (step == null) return const SizedBox.shrink();
    final pilot = ref.read(nightSetupProvider.notifier);
    final focused = ref.watch(setupFocusStateProvider) == SetupStepState.done;
    final aligned =
        ref.watch(setupPolarAlignStateProvider) == SetupStepState.done ||
        (ref.watch(simplePermanentPierProvider).value ?? false);
    // shortcut: on a window narrower than the strip (a one-third Split View) it
    // shrinks to fit rather than overflowing by ~125 px; the phone layout
    // (ALP-4865) should drop the labels of the steps behind and ahead instead.
    return FittedBox(
      fit: BoxFit.scaleDown,
      child: SimplePanel(
        key: const Key('night-setup-strip'),
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
        child: SizedBox(
          height: 48,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              for (final s in NightSetupStep.values) ...[
                if (s.index > 0)
                  const Padding(
                    padding: EdgeInsets.symmetric(horizontal: 2),
                    child: Icon(
                      Icons.chevron_right,
                      size: 16,
                      color: AraColors.textDisabled,
                    ),
                  ),
                _Step(
                  step: s,
                  current: step,
                  done: switch (s) {
                    NightSetupStep.focus => focused,
                    NightSetupStep.polarAlign => aligned,
                    NightSetupStep.plan =>
                      ref.watch(simpleRunProvider).sequenceId != null,
                  },
                ),
              ],
              const SizedBox(width: AraSpace.s8),
              TextButton(
                key: const Key('night-setup-back'),
                onPressed: step.index > 0 ? pilot.back : null,
                child: Text(context.l10n.nightSetupBack),
              ),
              TextButton(
                key: const Key('night-setup-skip'),
                onPressed: pilot.next,
                child: Text(context.l10n.nightSetupSkip),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _Step extends StatelessWidget {
  const _Step({required this.step, required this.current, required this.done});

  final NightSetupStep step;
  final NightSetupStep current;

  /// Done tonight; a step behind the current one that isn't was skipped.
  final bool done;

  @override
  Widget build(BuildContext context) {
    final behind = step.index < current.index;
    final on = step == current;
    final l10n = context.l10n;
    final label = step.label(l10n);
    // The tick and skip mark are icons: screen readers get the state in words.
    return Semantics(
      label: behind && done
          ? l10n.nightSetupStepDone(label)
          : behind
          ? l10n.nightSetupStepSkipped(label)
          : on
          ? l10n.nightSetupStepCurrent(label)
          : l10n.nightSetupStepNext(label),
      excludeSemantics: true,
      child: Container(
        height: 36,
        padding: const EdgeInsets.symmetric(horizontal: 10),
        decoration: BoxDecoration(
          color: on ? AraColors.selectionTint : null,
          borderRadius: BorderRadius.circular(8),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          spacing: 6,
          children: [
            behind && done
                ? const Icon(
                    Icons.check_circle,
                    size: 20,
                    color: AraColors.accentConnected,
                  )
                : behind
                ? const Icon(
                    Icons.skip_next,
                    size: 20,
                    color: AraColors.accentBusy,
                  )
                : Container(
                    width: 20,
                    height: 20,
                    alignment: Alignment.center,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      border: Border.all(
                        color: on
                            ? AraColors.accentInfo
                            : AraColors.textDisabled,
                        width: 1.5,
                      ),
                    ),
                    child: Text(
                      '${step.index + 1}',
                      style: kSimpleText.copyWith(
                        fontSize: 11,
                        color: on
                            ? AraColors.accentInfo
                            : AraColors.textSecondary,
                      ),
                    ),
                  ),
            Text(
              label,
              style: kSimpleText.copyWith(
                color: on ? AraColors.textPrimary : AraColors.textSecondary,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Whether to offer Night Setup: nothing done yet tonight (no focus, no polar
/// alignment unless the pier is permanent, no plan started).
// shortcut: doesn't check that it's night; gate on the site's twilight once
// the client has it at hand.
bool nightSetupOffered(WidgetRef ref) =>
    ref.watch(nightSetupProvider) == null &&
    ref.watch(setupFocusStateProvider) != SetupStepState.done &&
    ref.watch(setupPolarAlignStateProvider) != SetupStepState.done &&
    !ref.watch(simpleRunProvider).started;

/// The offer under the status strip: "Nothing done yet tonight", Start
/// Night Setup, and × to dismiss it.
class NightSetupOffer extends StatelessWidget {
  const NightSetupOffer({
    super.key,
    required this.onStart,
    required this.onDismiss,
  });

  final VoidCallback onStart;
  final VoidCallback onDismiss;

  @override
  Widget build(BuildContext context) => simpleFrost(
    Container(
      key: const Key('night-setup-offer'),
      padding: const EdgeInsets.fromLTRB(16, 6, 4, 6),
      decoration: BoxDecoration(
        color: AraColors.overlayScrim,
        border: Border.all(color: AraColors.accentConnected),
        borderRadius: BorderRadius.circular(10),
      ),
      // On a narrow window the buttons drop under the words, rather than the
      // words squeezing into a column tall enough to cover the rails.
      child: Wrap(
        spacing: AraSpace.s12,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text(context.l10n.nightSetupNothingDone, style: kSimpleText),
          FilledButton(
            key: const Key('night-setup-start'),
            style: kSimpleGoStyle,
            onPressed: onStart,
            child: Text(context.l10n.nightSetupStart),
          ),
          IconButton(
            tooltip: context.l10n.nightSetupNotNow,
            icon: const Icon(Icons.close, size: 18),
            onPressed: onDismiss,
          ),
        ],
      ),
    ),
  );
}
