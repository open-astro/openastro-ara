import 'dart:typed_data';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/simple_messages_state.dart';
import '../../state/simple_preflight_state.dart';
import 'simple_calibration_screen.dart';
import 'simple_focus_screen.dart';
import 'simple_guide_screen.dart';
import 'simple_polar_screen.dart';
import 'simple_preview_screen.dart';
import 'simple_queue_screen.dart';

/// One of Simple Mode's six Screens (Preview, Focus, Polar align, Guide,
/// Calibration, Queue): the parts the shell renders for whichever one shows (#1593).
/// Preflight is not a Screen; it chooses which Screen shows.
abstract class SimpleScreen {
  const SimpleScreen();

  static SimpleScreen of(SimpleModeScreen id) => switch (id) {
    SimpleModeScreen.focus => const SimpleFocusScreen(),
    SimpleModeScreen.polarAlign => const SimplePolarScreen(),
    SimpleModeScreen.guide => const SimpleGuideScreen(),
    SimpleModeScreen.calibration => const SimpleCalibrationScreen(),
    SimpleModeScreen.plan => const SimpleQueueScreen(),
    // Preflight before its first step shows Preview.
    SimpleModeScreen.preview || SimpleModeScreen.preflight =>
      const SimplePreviewScreen(),
  };

  /// Which Screen this is.
  SimpleModeScreen get id;

  /// The pills this Screen posts: a tap on one opens this Screen's panel.
  SimpleStatusOrigin get origin;

  /// The Screen a status pill from [origin] is about; Preview for anything
  /// that no Screen posts (a device pill opens its window instead).
  static SimpleModeScreen showing(SimpleStatusOrigin origin) =>
      all.where((s) => s.origin == origin).firstOrNull?.id ??
      SimpleModeScreen.preview;

  /// ⌘. / Ctrl+.: only the Screen that runs the plan acts on it.
  void onStopShortcut(BuildContext context, WidgetRef ref) {}

  /// What the settings chip opens beside the rail.
  Widget panel(SimpleScreenHost host);

  /// A sheet that takes the panel's place (Queue: the plan's sheet); null for
  /// Screens without one.
  Widget? overlay(SimpleScreenHost host) => null;

  /// What stays in the bottom-right corner (Queue: the plan squares).
  Widget? tracker(SimpleScreenHost host) => null;

  /// The rail's shutter button.
  Widget get shutter;

  /// The settings chip's caption on the rail.
  String caption(BuildContext context, WidgetRef ref);

  /// What fills the screen behind the panels instead of the last frame; null
  /// for the last frame (or the sky, when there is none).
  Widget? frame(WidgetRef ref) => null;

  /// Called once this Screen stops showing.
  void onLeave(BuildContext context, WidgetRef ref) {}

  /// Called for every Screen on every shell build, so a Screen's failure
  /// listener posts its pill while another Screen shows.
  void listen(BuildContext context, WidgetRef ref) {}

  /// Every Screen once, for [listen].
  static const all = <SimpleScreen>[
    SimplePreviewScreen(),
    SimpleFocusScreen(),
    SimplePolarScreen(),
    SimpleGuideScreen(),
    SimpleCalibrationScreen(),
    SimpleQueueScreen(),
  ];
}

/// The screen-filling frame a Screen shows from its own camera.
Widget simpleScreenFrame(Uint8List bytes) => Image.memory(
  bytes,
  key: const Key('simple-focus-frame'),
  fit: BoxFit.contain,
  gaplessPlayback: true,
);

/// What a Screen's parts call back into the shell for.
class SimpleScreenHost {
  const SimpleScreenHost({
    required this.onAddTarget,
    required this.onEditTarget,
    this.editing,
    this.onShowSky,
    this.onPickInSky,
    this.onSheetDone,
    this.onTrackerTap,
  });

  /// Queue: open the run sheet to add a target, or to edit row [i].
  final VoidCallback onAddTarget;
  final ValueChanged<int> onEditTarget;

  /// Queue: the row the sheet edits; the sheet's sky buttons; the sheet done
  /// (a plan created); a tap on the plan squares.
  final int? editing;
  final VoidCallback? onShowSky;
  final VoidCallback? onPickInSky;
  final VoidCallback? onSheetDone;
  final VoidCallback? onTrackerTap;
}
