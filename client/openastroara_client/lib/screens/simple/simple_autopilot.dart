import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/polar_align/polar_align_state.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/simple_mode_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_plan_panel.dart' show simpleRunProvider;
import 'simple_polar_panel.dart' show simplePolarActive;
import 'simple_preview_panel.dart' show kSimpleText;
import 'simple_shell.dart' show SimplePanel;

/// Autopilot's steps, in the night's order: focus first (polar alignment
/// plate-solves and fails on a soft focus), then polar align, then the plan.
enum AutopilotStep { focus, polarAlign, plan }

extension AutopilotStepLabel on AutopilotStep {
  String get label => switch (this) {
    AutopilotStep.focus => 'Focus',
    AutopilotStep.polarAlign => 'Polar align',
    AutopilotStep.plan => 'Plan',
  };
}

/// The step to start on, or go to next from [after]: the first not done
/// tonight, polar align skipped on a permanent pier; the plan is never done
/// beforehand. Pure — unit-tested.
AutopilotStep autopilotNext({
  AutopilotStep? after,
  required bool focused,
  required bool aligned,
  required bool permanentPier,
}) {
  for (final s in AutopilotStep.values) {
    if (after != null && s.index <= after.index) continue;
    if (s == AutopilotStep.focus && focused) continue;
    if (s == AutopilotStep.polarAlign && (aligned || permanentPier)) continue;
    return s;
  }
  return AutopilotStep.plan;
}

/// Simple Mode's Autopilot: the night's steps in order, each handing over to
/// the next. Null while Autopilot isn't running. A finished autofocus moves
/// on to polar align, which starts its routine on entry; polar align's Done
/// moves on to the plan; starting the plan ends Autopilot.
class AutopilotNotifier extends Notifier<AutopilotStep?> {
  @override
  AutopilotStep? build() {
    ref.listen(setupFocusStateProvider, (prev, next) {
      if (state == AutopilotStep.focus &&
          prev != SetupStepState.done &&
          next == SetupStepState.done) {
        _go(_next(AutopilotStep.focus));
      }
    });
    ref.listen(simpleRunProvider, (prev, next) {
      if (state == AutopilotStep.plan && next.started) state = null;
    });
    return null;
  }

  AutopilotStep _next(AutopilotStep? after) => autopilotNext(
    after: after,
    focused: ref.read(setupFocusStateProvider) == SetupStepState.done,
    aligned: ref.read(setupPolarAlignStateProvider) == SetupStepState.done,
    permanentPier: ref.read(simplePermanentPierProvider).value ?? false,
  );

  void _go(AutopilotStep step) {
    state = step;
    // Entering polar align starts the routine (an Autopilot-only exception
    // to the shutter starting things; its red square still stops it).
    if (step == AutopilotStep.polarAlign &&
        !simplePolarActive(ref.read(polarAlignLiveProvider))) {
      final api = ref.read(polarAlignApiProvider);
      if (api != null) unawaited(api.start().catchError((_) {}));
    }
  }

  void start() => _go(_next(null));

  /// Polar align's Done, or Skip: on to the next step.
  void next() {
    final s = state;
    if (s != null) _go(_next(s));
  }

  void back() {
    final s = state;
    if (s != null && s.index > 0) _go(AutopilotStep.values[s.index - 1]);
  }

  void stop() => state = null;
}

final autopilotProvider = NotifierProvider<AutopilotNotifier, AutopilotStep?>(
  AutopilotNotifier.new,
);

/// Autopilot's step strip, centred under the status strip: the steps behind
/// ticked green when done tonight and amber when skipped, the current one
/// lit, then Back and Skip.
class AutopilotStrip extends ConsumerWidget {
  const AutopilotStrip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final step = ref.watch(autopilotProvider);
    if (step == null) return const SizedBox.shrink();
    final pilot = ref.read(autopilotProvider.notifier);
    final focused = ref.watch(setupFocusStateProvider) == SetupStepState.done;
    final aligned =
        ref.watch(setupPolarAlignStateProvider) == SetupStepState.done ||
        (ref.watch(simplePermanentPierProvider).value ?? false);
    return SimplePanel(
      key: const Key('autopilot-strip'),
      padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
      child: SizedBox(
        height: 48,
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            for (final s in AutopilotStep.values) ...[
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
                  AutopilotStep.focus => focused,
                  AutopilotStep.polarAlign => aligned,
                  AutopilotStep.plan => false,
                },
              ),
            ],
            const SizedBox(width: AraSpace.s8),
            TextButton(
              key: const Key('autopilot-back'),
              onPressed: step.index > 0 ? pilot.back : null,
              child: const Text('Back'),
            ),
            TextButton(
              key: const Key('autopilot-skip'),
              onPressed: step == AutopilotStep.plan ? null : pilot.next,
              child: const Text('Skip'),
            ),
          ],
        ),
      ),
    );
  }
}

class _Step extends StatelessWidget {
  const _Step({required this.step, required this.current, required this.done});

  final AutopilotStep step;
  final AutopilotStep current;

  /// Done tonight; a step behind the current one that isn't was skipped.
  final bool done;

  @override
  Widget build(BuildContext context) {
    final behind = step.index < current.index;
    final on = step == current;
    return Container(
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
                      color: on ? AraColors.accentInfo : AraColors.textDisabled,
                      width: 1.5,
                    ),
                  ),
                  child: Text(
                    '${step.index + 1}',
                    style: kSimpleText.copyWith(
                      fontSize: 11,
                      color: on ? AraColors.accentInfo : AraColors.textSecondary,
                    ),
                  ),
                ),
          Text(
            step.label,
            style: kSimpleText.copyWith(
              color: on ? AraColors.textPrimary : AraColors.textSecondary,
            ),
          ),
        ],
      ),
    );
  }
}

/// Whether to offer Autopilot: nothing done yet tonight (no focus, no polar
/// alignment unless the pier is permanent, no plan started).
// shortcut: doesn't check that it's night; gate on the site's twilight once
// the client has it at hand.
bool autopilotOffered(WidgetRef ref) =>
    ref.watch(autopilotProvider) == null &&
    ref.watch(setupFocusStateProvider) != SetupStepState.done &&
    ref.watch(setupPolarAlignStateProvider) != SetupStepState.done &&
    !ref.watch(simpleRunProvider).started;

/// The offer under the status strip: "Nothing done yet tonight", Start
/// Autopilot, and × to dismiss it.
class AutopilotOffer extends StatelessWidget {
  const AutopilotOffer({
    super.key,
    required this.onStart,
    required this.onDismiss,
  });

  final VoidCallback onStart;
  final VoidCallback onDismiss;

  @override
  Widget build(BuildContext context) => Container(
    key: const Key('autopilot-offer'),
    padding: const EdgeInsets.fromLTRB(16, 6, 4, 6),
    decoration: BoxDecoration(
      color: AraColors.overlayScrim,
      border: Border.all(color: AraColors.accentConnected),
      borderRadius: BorderRadius.circular(10),
    ),
    child: Row(
      mainAxisSize: MainAxisSize.min,
      spacing: AraSpace.s12,
      children: [
        // Wraps on a narrow window rather than pushing the buttons off.
        Flexible(
          child: Text('Nothing done yet tonight', style: kSimpleText),
        ),
        FilledButton(
          key: const Key('autopilot-start'),
          style: FilledButton.styleFrom(
            backgroundColor: AraColors.accentConnected,
          ),
          onPressed: onStart,
          child: const Text('Start Autopilot'),
        ),
        IconButton(
          tooltip: 'Not now',
          icon: const Icon(Icons.close, size: 18),
          onPressed: onDismiss,
        ),
      ],
    ),
  );
}
