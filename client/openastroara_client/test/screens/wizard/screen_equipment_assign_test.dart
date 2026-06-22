import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';

void main() {
  Future<void> pump(WidgetTester tester) async {
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ScreenEquipmentAssign())),
    ));
    await tester.pump();
  }

  testWidgets('Switch is a discoverable slot like every other device', (tester) async {
    await pump(tester);

    // The Switch slot is present and no longer carries the disabled
    // "multi-switch in progress" message.
    expect(find.text('Switch'), findsOneWidget);
    expect(find.textContaining('Multi-switch support is in progress'), findsNothing);

    // Every slot offers a Choose action (all discoverable now); pre-fix, Switch
    // had no button so this was 10. The count mirrors `_slots` in
    // screen_equipment_discovery.dart — update it (deliberately) when a slot is
    // added or removed.
    expect(find.widgetWithText(TextButton, 'Choose'), findsNWidgets(11));
  });
}
