import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_preflight_state.dart';
import 'simple_polar_panel.dart';
import 'simple_screen.dart';

/// Polar align: the routine's settings, its shutter, exposure and Loop/Single
/// on the chip.
class SimplePolarScreen extends SimpleScreen {
  const SimplePolarScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.polarAlign;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.polar;

  @override
  Widget panel(SimpleScreenHost host) => const SimplePolarPanel();

  @override
  Widget get shutter => const SimplePolarShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simplePolarSummary(
    ref.watch(polarAlignSettingsProvider).value ?? const PolarAlignSettings(),
    context.l10n,
  );

  /// The routine's own camera frames while it runs.
  @override
  Widget? frame(WidgetRef ref) =>
      simplePolarActive(ref.watch(polarAlignLiveProvider))
      ? const SimplePolarFrame()
      : null;

  /// A routine that fails says why on the left, fading like any error
  /// (user, 2026-11-04: not a sticky status pill).
  @override
  void listen(BuildContext context, WidgetRef ref) {
    ref.listen(polarAlignLiveProvider, (prev, next) {
      if (prev?.phase == PolarAlignStates.failed ||
          next.phase != PolarAlignStates.failed) {
        return;
      }
      ref
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.error,
            context.l10n.polarPhaseFailed,
            detail: next.errorMessage ?? next.errorReason,
            origin: SimpleStatusOrigin.polar,
          );
    });
  }
}
