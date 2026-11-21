import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/safety_policies_panel.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

import '../../../support/safety_section.dart';

/// #2044 (ADR 0009 rule 1): the full UI edits the three meridian fields Simple
/// Mode's Limits group reads, beside "Stop before meridian (min)".
Future<ProviderContainer> _pump(WidgetTester tester) async {
  tester.view.physicalSize = const Size(1600, 4000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  final c = ProviderContainer(overrides: [
    activeServerProvider.overrideWithValue(null),
    // The rig's copy of the section (ADR 0015): the rows edit it.
    safetyFromRig(),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: SafetyPoliciesPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return c;
}

Future<void> _type(WidgetTester tester, String label, String text) async {
  final row = find.widgetWithText(EditableNumberRow, label);
  await tester.enterText(find.descendant(of: row, matching: find.byType(TextField)), text);
  await tester.testTextInput.receiveAction(TextInputAction.done);
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('the Advanced panel edits flip after the meridian and its latest time', (tester) async {
    final c = await _pump(tester);
    await _type(tester, 'Flip after the meridian (min)', '15');
    await _type(tester, 'Latest flip after the meridian (min)', '20');
    final s = c.read(safetyPoliciesProvider).value!;
    expect((s.minutesAfterMeridian, s.maxMinutesAfterMeridian), (15, 20));
  });

  testWidgets('the flip-off stop has its own row, not the flip-on Stop before meridian', (tester) async {
    final c = await _pump(tester);
    expect(find.text('Stop before meridian (min)'), findsOneWidget);
    expect(find.text('Stop before meridian, flip off (min)'), findsOneWidget);
    await _type(tester, 'Stop before meridian, flip off (min)', '10');
    final s = c.read(safetyPoliciesProvider).value!;
    expect((s.stopBeforeMeridianMin, s.meridianPauseMin), (10, 5));
  });
}
