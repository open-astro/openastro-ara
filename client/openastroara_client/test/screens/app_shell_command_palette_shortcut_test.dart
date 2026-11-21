import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/night_hotkey.dart';

/// Opens on Settings instead of Planning, whose planetarium server leaves a
/// periodic timer running after the test.
class _StartOnSettings extends SelectedTabIndexNotifier {
  @override
  int build() => kOptionsTabIndex;
}

void main() {
  testWidgets('§61 ⌘K opens the command palette under the root NightHotkey',
      (tester) async {
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    // The test font is taller than the real one and overflows the top-bar chips.
    tester.platformDispatcher.textScaleFactorTestValue = 0.5;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    // NightHotkey (main.dart) wraps the whole app in an autofocus Focus that
    // wins focus before the shell mounts; ⌘K must still reach the shell.
    final container = ProviderContainer(overrides: [
      selectedTabIndexProvider.overrideWith(_StartOnSettings.new),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: NightHotkey(child: AppShell())),
    ));
    await tester.pump();

    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyK);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pump();

    expect(find.byType(Dialog), findsOneWidget);
  });
}
