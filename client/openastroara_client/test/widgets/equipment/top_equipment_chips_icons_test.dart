import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';

void main() {
  Future<void> pump(WidgetTester tester, Widget chips) async {
    await tester.pumpWidget(ProviderScope(
      child: MaterialApp(home: Scaffold(body: SingleChildScrollView(scrollDirection: Axis.horizontal, child: chips))),
    ));
    await tester.pump();
  }

  // Simple Mode's own icons only show for connected gear (its strip hides the
  // rest), so they are seen on the rig rather than here.
  testWidgets('the full UI keeps its own chips: as on master', (tester) async {
    await pump(tester, const TopEquipmentChips());
    for (final icon in [Icons.gps_fixed, Icons.adjust, Icons.power, Icons.shield_outlined, Icons.wb_sunny, Icons.home_outlined]) {
      expect(find.byIcon(icon), findsOneWidget, reason: '$icon');
    }
    expect(find.byType(EquipmentGlyph), findsNWidgets(2), reason: 'mount and filter wheel only');
    expect(find.byTooltip(RegExp('^Switch\\b(?!es)')), findsOneWidget);
  });
}
