import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
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
}
