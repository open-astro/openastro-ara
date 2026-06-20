import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart';
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

  // The field carrying a given label — resilient to field reordering, unlike a
  // positional TextField index.
  Finder fieldByLabel(String label) => find.ancestor(
        of: find.text(label),
        matching: find.byType(TextField),
      );

  group('screen publishes validity', () {
    testWidgets('ScreenPlateSolve marks invalid radius then recovers',
        (tester) async {
      final container = await pumpScreen(tester, const ScreenPlateSolve());
      expect(container.read(wizardStepValidProvider), isTrue);

      await tester.enterText(fieldByLabel('Search radius (°)'), '999'); // > 180
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isFalse);

      await tester.enterText(fieldByLabel('Search radius (°)'), '45');
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isTrue);
    });

    testWidgets('ScreenAutofocus marks out-of-range steps then recovers',
        (tester) async {
      final container = await pumpScreen(tester, const ScreenAutofocus());
      await tester.enterText(fieldByLabel('Steps'), '99'); // > 31
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isFalse);

      await tester.enterText(fieldByLabel('Steps'), '7');
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isTrue);
    });

    testWidgets('ScreenSafety recovers validity when switched to Ignore',
        (tester) async {
      final container = await pumpScreen(tester, const ScreenSafety());
      // The resume-delay field is visible (default unsafe action is not Ignore).
      // A 20-digit value overflows int64 → unparseable → marks the screen invalid.
      await tester.enterText(fieldByLabel('Resume delay (minutes)'), '9' * 20);
      await tester.pump();
      expect(container.read(wizardStepValidProvider), isFalse);

      // Switching the unsafe-action dropdown to Ignore hides + clears the delay
      // field, which must clear the error and restore validity.
      await tester.tap(find.byType(DropdownMenu<UnsafeConditionAction?>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Ignore').last);
      await tester.pumpAndSettle();
      expect(container.read(wizardStepValidProvider), isTrue);
    });
  });

  test('controller resets validity to true on navigation', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    final controller = container.read(wizardControllerProvider.notifier);

    container.read(wizardStepValidProvider.notifier).setValid(false);
    expect(container.read(wizardStepValidProvider), isFalse);

    controller.next();
    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'a fresh screen starts valid');

    container.read(wizardStepValidProvider.notifier).setValid(false);
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

    await tester.enterText(
        fieldByLabel('Search radius (°)'), '999'); // invalid
    await tester.pump();
    expect(nextButton().onPressed, isNull, reason: 'disabled while invalid');

    await tester.enterText(fieldByLabel('Search radius (°)'), '45'); // valid
    await tester.pump();
    expect(nextButton().onPressed, isNotNull, reason: 're-enabled when fixed');
  });
}
