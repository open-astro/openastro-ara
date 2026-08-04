import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/widgets/launchpad_button.dart';

void main() {
  Future<ProviderContainer> pump(WidgetTester tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    // Simulate an in-shell session: gates passed, offline on.
    container.read(profileGatePassedProvider.notifier).pass();
    container.read(offlineModeProvider.notifier).enter();
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: LaunchpadButton())),
    ));
    return container;
  }

  testWidgets('confirming re-arms both session gates', (tester) async {
    final container = await pump(tester);
    await tester.tap(find.text('Launchpad')); // the bar button
    await tester.pumpAndSettle();
    expect(find.text('Back to the launchpad?'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'Launchpad'));
    await tester.pumpAndSettle();
    expect(container.read(profileGatePassedProvider), isFalse);
    expect(container.read(offlineModeProvider), isFalse);
    // §30 — Launchpad means SCREEN ONE: the router must show the server
    // chooser, not resume at the profile box.
    expect(container.read(serverChooserRequestedProvider), isTrue);
  });

  testWidgets('cancel leaves the session untouched', (tester) async {
    final container = await pump(tester);
    await tester.tap(find.text('Launchpad'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(container.read(profileGatePassedProvider), isTrue);
    expect(container.read(offlineModeProvider), isTrue);
    expect(container.read(serverChooserRequestedProvider), isFalse);
  });

  test('confirming a server clears the chooser request (gate round-trip)', () {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    container.read(serverChooserRequestedProvider.notifier).request();
    expect(container.read(serverChooserRequestedProvider), isTrue);
    container.read(serverChooserRequestedProvider.notifier).clear();
    expect(container.read(serverChooserRequestedProvider), isFalse);
  });
}
