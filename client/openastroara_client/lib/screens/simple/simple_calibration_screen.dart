import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/guider/guider_build_activity_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_preflight_state.dart';
import 'simple_calibration.dart';
import 'simple_screen.dart';

/// Calibration (#1757): flats, darks and bias, and the Guide Camera's own;
/// the shutter shoots what is missing, the chip says how much.
class SimpleCalibrationScreen extends SimpleScreen {
  const SimpleCalibrationScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.calibration;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.calibration;

  @override
  Widget panel(SimpleScreenHost host) => const SimpleCalibrationPanel();

  @override
  Widget get shutter => const SimpleCalibrationShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) =>
      simpleCalibrationSummary(ref, context.l10n);

  /// A Guide Camera build ends on the rig long after its tap: how it ended,
  /// as a message, whichever mode shows.
  @override
  void listen(BuildContext context, WidgetRef ref) {
    ref.listen(guiderBuildActivityProvider, (prev, next) {
      for (final a in CalibrationArtifact.values) {
        final was = prev?[a]?.phase, now = next[a]?.phase;
        if (now == was ||
            (now != CalibrationBuildPhase.failed &&
                now != CalibrationBuildPhase.complete)) {
          continue;
        }
        final l = context.l10n;
        final name = a == CalibrationArtifact.darkLibrary
            ? l.calibrationGuideDarks
            : l.calibrationGuideDefect;
        final failed = now == CalibrationBuildPhase.failed;
        ref
            .read(simpleMessagesProvider.notifier)
            .show(
              failed ? SimpleMessageKind.error : SimpleMessageKind.result,
              failed ? l.calibrationGuideBuildFailed(name) : l.calibrationGuideBuilt(name),
              detail: failed ? next[a]!.error : null,
              origin: SimpleStatusOrigin.calibration,
            );
      }
    });
  }
}
