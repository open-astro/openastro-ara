import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/simple_preflight_state.dart';
import 'simple_focus_screen.dart';
import 'simple_guide_screen.dart';
import 'simple_polar_screen.dart';
import 'simple_preview_screen.dart';
import 'simple_queue_screen.dart';

/// One of Simple Mode's five Screens (Preview, Focus, Polar align, Guide,
/// Queue): the parts the shell renders for whichever one shows (#1593).
/// Preflight is not a Screen; it chooses which Screen shows.
abstract class SimpleScreen {
  const SimpleScreen();

  static SimpleScreen of(SimpleModeScreen id) => switch (id) {
    SimpleModeScreen.focus => const SimpleFocusScreen(),
    SimpleModeScreen.polarAlign => const SimplePolarScreen(),
    SimpleModeScreen.guide => const SimpleGuideScreen(),
    SimpleModeScreen.plan => const SimpleQueueScreen(),
    // Preflight before its first step shows Preview.
    SimpleModeScreen.preview || SimpleModeScreen.preflight =>
      const SimplePreviewScreen(),
  };

  /// What the settings chip opens beside the rail.
  Widget panel(SimpleScreenHost host);

  /// The rail's shutter button.
  Widget get shutter;

  /// The settings chip's caption on the rail.
  String caption(BuildContext context, WidgetRef ref);
}

/// What a Screen's parts call back into the shell for.
class SimpleScreenHost {
  const SimpleScreenHost({required this.onAddTarget, required this.onEditTarget});

  /// Queue: open the run sheet to add a target, or to edit row [i].
  final VoidCallback onAddTarget;
  final ValueChanged<int> onEditTarget;
}
