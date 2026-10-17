import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/equipment_chip.dart';
import 'package:openastroara/widgets/status_indicator.dart';

void main() {
  BoxDecoration dotOf(WidgetTester tester, String label, StatusLevel level) {
    final container = tester.widget<Container>(
        find.byKey(ValueKey('chip-dot-$label-${level.name}')));
    return container.decoration! as BoxDecoration;
  }

  testWidgets('busy is an amber ring, warning a solid amber dot', (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: Scaffold(
        body: Row(children: [
          EquipmentChip(icon: Icons.camera_alt, label: 'CAM', status: StatusLevel.busy),
          EquipmentChip(icon: Icons.filter_alt, label: 'FW', status: StatusLevel.warning),
          EquipmentChip(icon: Icons.adjust, label: 'FOC', status: StatusLevel.connected),
        ]),
      ),
    ));

    final ring = dotOf(tester, 'CAM', StatusLevel.busy);
    expect(ring.color, AraColors.bgPanel, reason: 'hollow: activity, not a problem');
    expect((ring.border! as Border).top.color, StatusLevel.busy.color);

    final solid = dotOf(tester, 'FW', StatusLevel.warning);
    expect(solid.color, StatusLevel.warning.color, reason: 'solid: a standing problem');
    expect((solid.border! as Border).top.color, AraColors.bgPanel);

    expect(dotOf(tester, 'FOC', StatusLevel.connected).color, StatusLevel.connected.color);
    expect(EquipmentChip.isRing(StatusLevel.busy), isTrue);
    expect(EquipmentChip.isRing(StatusLevel.warning), isFalse);
  });

  testWidgets('a tooltip explains the dot and reaches the semantics label', (tester) async {
    await tester.pumpWidget(const MaterialApp(
      home: Scaffold(
        body: EquipmentChip(
          icon: Icons.filter_alt,
          label: 'FW',
          status: StatusLevel.warning,
          tooltip: 'degraded: EFW_ERROR_REMOVED',
        ),
      ),
    ));
    expect(find.byTooltip('degraded: EFW_ERROR_REMOVED'), findsOneWidget);
    // The Tooltip merges its own semantics around the chip's; match the label by content.
    expect(find.bySemanticsLabel(RegExp(r'FW \(warning\): degraded: EFW_ERROR_REMOVED')), findsOneWidget);
  });
}
