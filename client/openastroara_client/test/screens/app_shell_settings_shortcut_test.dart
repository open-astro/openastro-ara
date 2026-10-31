import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/app_shell.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/widgets/night_hotkey.dart';

/// Opens on Run: Planning's planetarium server and Live's guiding poll
/// leave periodic timers running after the test.
class _StartOnRun extends SelectedTabIndexNotifier {
  @override
  int build() => 2;
}

void main() {
  for (final (name, modifier) in [
    ('⌘,', LogicalKeyboardKey.metaLeft),
    ('Ctrl+,', LogicalKeyboardKey.controlLeft),
  ]) {
    testWidgets('$name opens Settings (the Options tab)', (tester) async {
      tester.view.physicalSize = const Size(1600, 1000);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      // The test font is taller than the real one and overflows the top-bar chips.
      tester.platformDispatcher.textScaleFactorTestValue = 0.5;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      final container = ProviderContainer(overrides: [
        selectedTabIndexProvider.overrideWith(_StartOnRun.new),
      ]);
      addTearDown(container.dispose);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: NightHotkey(child: AppShell())),
      ));
      await tester.pump();

      await tester.sendKeyDownEvent(modifier);
      await tester.sendKeyEvent(LogicalKeyboardKey.comma);
      await tester.sendKeyUpEvent(modifier);
      await tester.pump();

      expect(container.read(selectedTabIndexProvider), kOptionsTabIndex);
    });
  }
}
