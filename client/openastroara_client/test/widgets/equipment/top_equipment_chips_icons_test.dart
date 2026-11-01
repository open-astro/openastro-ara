import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/equipment/equipment_glyphs.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';

void main() {
  testWidgets('device chips use the agreed icons: focus corners, toggle, shield with check; guider, panel and dome glyphs',
      (tester) async {
    await tester.pumpWidget(const ProviderScope(
      child: MaterialApp(home: Scaffold(body: SingleChildScrollView(scrollDirection: Axis.horizontal, child: TopEquipmentChips()))),
    ));
    await tester.pump();
    for (final icon in [Icons.center_focus_strong, Icons.toggle_on, Icons.verified_user]) {
      expect(find.byIcon(icon), findsOneWidget, reason: '$icon');
    }
    expect(find.byType(EquipmentGlyph), findsNWidgets(5), reason: 'mount, guider, filter wheel, flat panel, dome');
    expect(find.byTooltip(RegExp('^Switches')), findsOneWidget);
  });
}
