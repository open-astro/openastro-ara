import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'simple_guide_panel.dart';
import 'simple_screen.dart';

/// Guide: the Guider panel, the start/stop shutter, the RMS on the chip.
class SimpleGuideScreen extends SimpleScreen {
  const SimpleGuideScreen();

  @override
  Widget panel(SimpleScreenHost host) => const SimpleGuidePanel();

  @override
  Widget get shutter => const SimpleGuideShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleGuideSummary(ref);
}
