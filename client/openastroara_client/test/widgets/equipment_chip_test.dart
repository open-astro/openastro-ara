import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/equipment/equipment_status_chip.dart';
import 'package:openastroara/widgets/equipment_chip.dart';
import 'package:openastroara/widgets/status_indicator.dart';

Future<void> _pumpAt(WidgetTester tester, double width, Widget child) async {
  tester.view.physicalSize = Size(width, 800);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(ProviderScope(
    child: MaterialApp(
      home: Scaffold(body: SingleChildScrollView(scrollDirection: Axis.horizontal, child: child)),
    ),
  ));
  await tester.pump();
}

const _chip = EquipmentChip(icon: Icons.camera_alt, label: 'Filter Wheel', status: StatusLevel.connected);

void main() {
  testWidgets('a wide window shows the full device name under the icon', (tester) async {
    await _pumpAt(tester, 1400, _chip);
    expect(find.text('Filter Wheel'), findsOneWidget);
  });

  testWidgets('a phone-width window shows the icon only, the name stays as the tooltip', (tester) async {
    await _pumpAt(tester, 400, _chip);
    expect(find.text('Filter Wheel'), findsNothing);
    expect(find.byTooltip('Filter Wheel'), findsOneWidget);
  });

  testWidgets('the top bar names every device in full', (tester) async {
    await _pumpAt(tester, 1600, const TopEquipmentChips());
    for (final name in const [
      'Camera', 'Filter Wheel', 'Focuser', 'Mount', 'Rotator', 'Guider',
      'Flat Panel', 'Switch', 'Weather', 'Safety', 'Dome',
    ]) {
      expect(find.text(name), findsOneWidget, reason: name);
    }
  });

  testWidgets('every chip is the same width, whatever its name, so the icons are evenly spaced', (tester) async {
    await _pumpAt(tester, 1400, const Row(children: [
      EquipmentChip(icon: Icons.home, label: 'Dome', status: StatusLevel.connected),
      EquipmentChip(icon: Icons.shield, label: 'Safety Monitor', status: StatusLevel.connected),
    ]));
    final widths = tester.widgetList(find.byType(EquipmentChip)).map((w) => tester.getSize(find.byWidget(w)).width).toSet();
    expect(widths, hasLength(1));
  });

  testWidgets('icon-only chips are the same width too', (tester) async {
    await _pumpAt(tester, 400, const Row(children: [
      EquipmentChip(icon: Icons.home, label: 'Dome', status: StatusLevel.connected),
      EquipmentChip(icon: Icons.shield, label: 'Safety Monitor', status: StatusLevel.connected),
    ]));
    final widths = tester.widgetList(find.byType(EquipmentChip)).map((w) => tester.getSize(find.byWidget(w)).width).toSet();
    expect(widths, hasLength(1));
  });

  test('active is green: amber is kept for issues', () {
    expect(StatusLevel.active.color, StatusLevel.connected.color);
    expect(StatusLevel.active.color, isNot(StatusLevel.busy.color));
  });

  testWidgets('an active device blinks its green dot; a merely connected one is steady', (tester) async {
    double dotOpacity() => tester.widget<FadeTransition>(find.byKey(const ValueKey('equipment-chip-dot'))).opacity.value;
    await _pumpAt(tester, 1400,
        const EquipmentChip(icon: Icons.camera_alt, label: 'Camera', status: StatusLevel.active));
    final seen = <double>{};
    for (var i = 0; i < 8; i++) {
      seen.add((dotOpacity() * 10).round() / 10);
      await tester.pump(const Duration(milliseconds: 150));
    }
    expect(seen.length, greaterThan(2), reason: 'the dot fades in and out');

    await _pumpAt(tester, 1400,
        const EquipmentChip(icon: Icons.camera_alt, label: 'Camera', status: StatusLevel.connected));
    expect(dotOpacity(), 1.0);
    await tester.pump(const Duration(milliseconds: 400));
    expect(dotOpacity(), 1.0);
  });
}
