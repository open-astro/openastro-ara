import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/wizard_shell.dart';
import 'package:openastroara/state/wizard_state.dart';

// The per-screen field-validation cases that lived here died with the retired
// §37 capture-setup screens (§76 — those settings default and move to
// Options). What remains is the CONTRACT: screens publish validity, the
// controller resets it on navigation, and the shell gates Next on it.

void main() {
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

  testWidgets('shell disables Next + Skip while the step is invalid',
      (tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    // Step 1 (Welcome) publishes no validation of its own — drive the provider
    // directly to exercise the shell's gate.
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: WizardShell()),
    ));
    await tester.pump();

    FilledButton nextButton() =>
        tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Next'));
    TextButton skipButton() => tester.widget<TextButton>(
        find.widgetWithText(TextButton, 'Skip — use defaults'));
    expect(nextButton().onPressed, isNotNull, reason: 'enabled initially');

    container.read(wizardStepValidProvider.notifier).setValid(false);
    await tester.pump();
    expect(nextButton().onPressed, isNull, reason: 'disabled while invalid');
    expect(skipButton().onPressed, isNull,
        reason: 'a skippable gate is no gate');

    container.read(wizardStepValidProvider.notifier).setValid(true);
    await tester.pump();
    expect(nextButton().onPressed, isNotNull, reason: 're-enabled when fixed');
  });
}
