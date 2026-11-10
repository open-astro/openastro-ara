import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/focus/autofocus_live_state.dart';
import '../../state/focus/bahtinov_focus_state.dart';
import '../../state/focus/guide_focus_state.dart';
import '../../state/simple_messages_state.dart';
import '../../widgets/focus/focusing_pane.dart' show MainFocusCard;
import '../../state/simple_preflight_state.dart';
import 'simple_focus_panel.dart';
import 'simple_screen.dart';

/// Focus: the focus panel, its shutter, Auto/Manual/Guide on the chip.
class SimpleFocusScreen extends SimpleScreen {
  const SimpleFocusScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.focus;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.focus;

  @override
  Widget panel(SimpleScreenHost host) => const SimpleFocusPanel();

  @override
  Widget get shutter => const SimpleFocusShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleFocusSummary(ref);

  /// The run's frame, measured stars circled, when asked; Manual's Bahtinov
  /// crop, spikes and all; on the guide camera the guider's frames.
  @override
  Widget? frame(WidgetRef ref) {
    final bytes = ref.watch(simpleFocusScopeProvider) == SimpleFocusScope.guide
        ? ref.watch(guideFocusProvider.select((l) => l.frame))
        : ref.watch(effectiveFocusMethodProvider) == MainFocusMethod.bahtinov
        ? ref.watch(bahtinovFocusProvider.select((l) => l.frame))
        : ref.watch(simpleFocusStarsShownProvider)
        ? ref.watch(autofocusLiveProvider.select((l) => l.frame))
        : null;
    return bytes == null ? null : simpleScreenFrame(bytes);
  }

  /// Leaving Manual before Finish asks for the mask to come off (DESIGN.md
  /// Focus; ALP-4982), once Focus has gone.
  @override
  void onLeave(BuildContext context, WidgetRef ref) {
    final bahtinov = ref.read(bahtinovFocusProvider);
    if (!bahtinov.status.hasMeasurement || bahtinov.finished) return;
    final l10n = context.l10n;
    ref
        .read(simpleMessagesProvider.notifier)
        .show(
          SimpleMessageKind.question,
          l10n.bahtinovMaskQuestion,
          detail: l10n.bahtinovMaskQuestionDetail,
          actions: [
            SimpleMessageAction(l10n.bahtinovNotYet, () {}),
            SimpleMessageAction(
              l10n.bahtinovMaskRemoved,
              () =>
                  unawaited(ref.read(bahtinovFocusProvider.notifier).finish()),
            ),
          ],
        );
  }

  /// Only a failure seen happening: Ara keeps its last run until it restarts,
  /// so the first read after a launch or reconnect is old news (ALP-4998).
  @override
  void listen(BuildContext context, WidgetRef ref) {
    ref.listen(
      autofocusLiveProvider.select((l) => (hydrated: l.hydrated, run: l.run)),
      (prev, live) {
        final next = live.run;
        if (prev == null ||
            !prev.hydrated ||
            prev.run.isFailed ||
            !next.isFailed) {
          return;
        }
        ref
            .read(simpleMessagesProvider.notifier)
            .show(
              SimpleMessageKind.error,
              context.l10n.focusCardFailed,
              detail: MainFocusCard.failureText(context.l10n, next),
              origin: SimpleStatusOrigin.focus,
            );
      },
    );
  }
}
