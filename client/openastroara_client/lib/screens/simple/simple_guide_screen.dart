import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/simple_preflight_state.dart';
import '../../state/simple_messages_state.dart';
import 'simple_guide_panel.dart';
import 'simple_screen.dart';

/// Guide: the Guider panel, the start/stop shutter, the RMS on the chip.
class SimpleGuideScreen extends SimpleScreen {
  const SimpleGuideScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.guide;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.guide;

  @override
  Widget panel(SimpleScreenHost host) => const SimpleGuidePanel();

  @override
  Widget get shutter => const SimpleGuideShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleGuideSummary(ref);
}
