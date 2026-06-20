import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_capture_setup.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/state/wizard_state.dart';

void main() {
  // Pump a single wizard screen in its own container so the test can read back
  // the validity provider the screen publishes.
  Future<ProviderContainer> pumpScreen(WidgetTester tester, Widget screen) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    // Keep the autoDispose validity provider alive the way the live WizardShell
    // does (it watches it) — otherwise it resets to true across each pump.
    container.listen(wizardStepValidProvider, (_, _) {});
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(home: Scaffold(body: screen)),
    ));
    return container;
  }

  group('screen publishes validity', () {
    testWidgets('ScreenPlateSolve marks invalid radius then recovers',
        (tester) async {
      final container = await pumpScreen(tester, const ScreenPlateSolve());
      expect(container.read(wizardStepValidProvider), isTrue);

      // Search radius is the 3rd field (ASTAP path, star DB, radius).
      await tester.enterText(find.byType(TextField).at(2), '999');
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isFalse);

      await tester.enterText(find.byType(TextField).at(2), '45');
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isTrue);
    });

    testWidgets('ScreenAutofocus marks out-of-range steps then recovers',
        (tester) async {
      final container = await pumpScreen(tester, const ScreenAutofocus());
      // Fields: exposure (0), steps (1), step size (2).
      await tester.enterText(find.byType(TextField).at(1), '99'); // > 31
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isFalse);

      await tester.enterText(find.byType(TextField).at(1), '7');
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isTrue);
    });
  });

  test('controller resets validity to true on navigation', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final controller = container.read(wizardControllerProvider.notifier);

    container.read(wizardStepValidProvider.notifier).set(false);
    expect(container.read(wizardStepValidProvider), isFalse);

    controller.next();
    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'a fresh screen starts valid');

    container.read(wizardStepValidProvider.notifier).set(false);
    controller.back();
    expect(container.read(wizardStepValidProvider), isTrue);
  });

  testWidgets('shell disables Next while a screen has a validation error',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    container.read(wizardControllerProvider.notifier).jumpTo(11); // plate solve
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: WizardShell()),
    ));
    await tester.pump();

    FilledButton nextButton() => tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Next'));
    expect(nextButton().onPressed, isNotNull, reason: 'enabled initially');

    await tester.enterText(find.byType(TextField).at(2), '999'); // invalid
    await tester.pump();
    expect(nextButton().onPressed, isNull, reason: 'disabled while invalid');

    await tester.enterText(find.byType(TextField).at(2), '45'); // valid
    await tester.pump();
    expect(nextButton().onPressed, isNotNull, reason: 're-enabled when fixed');
  });
}
