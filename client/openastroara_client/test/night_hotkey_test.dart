import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/main.dart';
import 'package:openastroara/services/night_mode_prefs_service.dart';
import 'package:openastroara/state/night_mode_state.dart';

/// The night-mode hotkey is Ctrl+N / ⌘N — a bare N must type, not toggle
/// (typing "ldn" into the sky search used to flip the display).
/// In-memory prefs: real file IO never completes inside a widget test's
/// fake-async zone (the first version of this test hung on the load).
class _MemPrefs extends NightModePrefsService {
  bool value = false;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

void main() {
  Future<ProviderContainer> pump(WidgetTester tester) async {
    final container = ProviderContainer(overrides: [
      nightModePrefsProvider.overrideWithValue(_MemPrefs()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Consumer(
          builder: (context, ref, _) =>
              withNightHotkey(ref, const Scaffold(body: Text('root'))),
        ),
      ),
    ));
    await container.read(nightModeProvider.future);
    await tester.pump();
    return container;
  }

  bool night(ProviderContainer c) => c.read(nightModeProvider).value ?? false;

  testWidgets('a bare N does nothing; Ctrl+N toggles', (tester) async {
    final c = await pump(tester);
    expect(night(c), isFalse);

    await tester.sendKeyEvent(LogicalKeyboardKey.keyN);
    await tester.pump();
    expect(night(c), isFalse, reason: 'N alone must type, not toggle');

    await tester.sendKeyDownEvent(LogicalKeyboardKey.controlLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyN);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.controlLeft);
    await tester.pump();
    expect(night(c), isTrue);
  });

  testWidgets('⌘N toggles too', (tester) async {
    final c = await pump(tester);
    await tester.sendKeyDownEvent(LogicalKeyboardKey.metaLeft);
    await tester.sendKeyEvent(LogicalKeyboardKey.keyN);
    await tester.sendKeyUpEvent(LogicalKeyboardKey.metaLeft);
    await tester.pump();
    expect(night(c), isTrue);
  });
}
