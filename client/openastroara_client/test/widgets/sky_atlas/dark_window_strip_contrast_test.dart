import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/sky_atlas/planning_visuals.dart';

double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

void main() {
  // #1843: the strip's times were text-disabled (2.4:1 on the panel).
  for (final (name, bg) in const [
    ('bg-panel', AraColors.bgPanel),
    ('bg-panel-alt', AraColors.bgPanelAlt),
    ('bg-input', AraColors.bgInput),
  ]) {
    testWidgets('Dark Window strip times read at 4.5:1 on $name', (
      tester,
    ) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: ColoredBox(
              color: bg,
              child: SizedBox(
                width: 300,
                child: DarkWindowStrip(
                  windowStartUtc: DateTime.utc(2026, 7, 21, 3, 40),
                  windowEndUtc: DateTime.utc(2026, 7, 21, 9, 10),
                  transitUtc: DateTime.utc(2026, 7, 21, 6, 20),
                  nowUtc: DateTime.utc(2026, 7, 21, 5, 0),
                ),
              ),
            ),
          ),
        ),
      );
      final times = tester.widgetList<Text>(find.byType(Text)).toList();
      expect(times, hasLength(3));
      for (final t in times) {
        expect(
          _contrast(t.style!.color!, bg),
          greaterThanOrEqualTo(4.5),
          reason: t.data,
        );
      }
    });
  }
}
