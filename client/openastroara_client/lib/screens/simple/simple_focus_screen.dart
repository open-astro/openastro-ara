import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'simple_focus_panel.dart';
import 'simple_screen.dart';

/// Focus: the focus panel, its shutter, Auto/Manual/Guide on the chip.
class SimpleFocusScreen extends SimpleScreen {
  const SimpleFocusScreen();

  @override
  Widget panel(SimpleScreenHost host) => const SimpleFocusPanel();

  @override
  Widget get shutter => const SimpleFocusShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleFocusSummary(ref);
}
