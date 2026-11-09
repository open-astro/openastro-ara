
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/simple_mode_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/simple_preflight_state.dart';
import 'simple_guide_panel.dart' show simpleGuiding;
import 'simple_plan_panel.dart' show simpleRunProvider;
export '../../state/simple_preflight_state.dart'
    show
        SimplePreflightStep,
        SimplePreflightStepLabel,
        preflightNextStep,
        simplePreflightNext,
        SimplePreflightNotifier,
        simplePreflightProvider;
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_shell.dart' show SimplePanel;


/// Preflight's step strip, centred under the status strip: the steps behind
/// ticked green when done tonight and amber when skipped, the current one
/// lit, then Back and Skip.
class SimplePreflightStrip extends ConsumerWidget {
  const SimplePreflightStrip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final step = ref.watch(simplePreflightProvider);
    if (step == null) return const SizedBox.shrink();
    final pilot = ref.read(simplePreflightProvider.notifier);
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
        key: const Key('preflight-strip'),
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s8),
        child: SizedBox(
          height: 48,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              for (final s in SimplePreflightStep.values) ...[
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
                    SimplePreflightStep.focus => focused,
                    SimplePreflightStep.polarAlign => aligned,
                    SimplePreflightStep.plan =>
                      ref.watch(simpleRunProvider).sequenceId != null,
                    SimplePreflightStep.guide => simpleGuiding(
                      ref.watch(guiderStatusProvider).value,
                    ),
                  },
                ),
              ],
              const SizedBox(width: AraSpace.s8),
              TextButton(
                key: const Key('preflight-back'),
                onPressed: step.index > 0 ? pilot.back : null,
                child: Text(context.l10n.simplePreflightBack),
              ),
              TextButton(
                key: const Key('preflight-skip'),
                onPressed: pilot.next,
                child: Text(context.l10n.simplePreflightSkip),
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

  final SimplePreflightStep step;
  final SimplePreflightStep current;

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
          ? l10n.simplePreflightStepDone(label)
          : behind
          ? l10n.simplePreflightStepSkipped(label)
          : on
          ? l10n.simplePreflightStepCurrent(label)
          : l10n.simplePreflightStepNext(label),
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
