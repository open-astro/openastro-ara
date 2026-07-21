import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/sky_atlas/planning_visuals.dart';

Widget _host(Widget child) =>
    MaterialApp(home: Scaffold(body: Center(child: SizedBox(width: 300, child: child))));

void main() {
  testWidgets('DarkWindowStrip renders end labels and a semantics window',
      (tester) async {
    final start = DateTime.utc(2026, 7, 21, 3, 40);
    final end = DateTime.utc(2026, 7, 21, 9, 10);
    await tester.pumpWidget(_host(DarkWindowStrip(
      windowStartUtc: start,
      windowEndUtc: end,
      transitUtc: DateTime.utc(2026, 7, 21, 6, 20),
      nowUtc: DateTime.utc(2026, 7, 21, 5, 0),
    )));
    // Three tiny time labels (start, transit, end) — local clock, so just
    // count them rather than pinning tz-dependent strings.
    expect(find.byType(Text), findsNWidgets(3));
    expect(
      find.bySemanticsLabel(RegExp('Dark window .* transit .*')),
      findsOneWidget,
    );
  });

  testWidgets('BudgetRing announces banked/needed and checks when complete',
      (tester) async {
    await tester
        .pumpWidget(_host(const BudgetRing(banked: 6.8, needed: 9)));
    expect(find.bySemanticsLabel('6.8 of 9 hours captured'), findsOneWidget);
    expect(find.byIcon(Icons.check), findsNothing);

    await tester
        .pumpWidget(_host(const BudgetRing(banked: 9.5, needed: 9)));
    expect(find.byIcon(Icons.check), findsOneWidget);
  });
}
