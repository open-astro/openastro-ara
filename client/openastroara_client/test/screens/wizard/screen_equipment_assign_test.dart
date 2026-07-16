import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';
import 'package:openastroara/state/wizard_state.dart';

void main() {
  Future<ProviderContainer> pump(WidgetTester tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ScreenEquipmentAssign())),
    ));
    await tester.pump();
    return container;
  }

  testWidgets('Switch is a multi-assign section, every other type is a slot',
      (tester) async {
    await pump(tester);

    // The dedicated Switches section replaces the old single-assign slot:
    // a rig can carry several switch hubs (§6.4 multi-switch).
    expect(find.text('Switches'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Add switch'), findsOneWidget);

    // The single-assign slots (Switch no longer among them; mirrors `_slots`
    // in screen_equipment_discovery.dart — update deliberately with it).
    expect(find.widgetWithText(TextButton, 'Choose'), findsNWidgets(10));
  });

  testWidgets('assigned switches render as removable chips', (tester) async {
    final container = await pump(tester);
    final draft = container.read(wizardControllerProvider).draft;
    draft.equipment.switchDeviceIds.addAll(['sw-power', 'sw-dew']);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ScreenEquipmentAssign())),
    ));
    await tester.pump();

    expect(find.text('2 assigned'), findsOneWidget);
    expect(find.byType(InputChip), findsNWidgets(2));

    // Delete one — the draft loses exactly that id. (The section sits below
    // the ten slots, off the test viewport — scroll it into view first.)
    await tester.ensureVisible(find.widgetWithText(InputChip, 'sw-power'));
    await tester.tap(find.descendant(
        of: find.widgetWithText(InputChip, 'sw-power'),
        matching: find.byType(Icon)));
    await tester.pump();
    expect(draft.equipment.switchDeviceIds, ['sw-dew']);
    expect(find.text('1 assigned'), findsOneWidget);
  });
}
