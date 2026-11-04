import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/status_indicator.dart';

void main() {
  testWidgets(
    'a long label in a tight slot ellipsizes instead of overflowing',
    (tester) async {
      await tester.pumpWidget(
        const MaterialApp(
          home: Scaffold(
            body: Align(
              alignment: Alignment.topLeft,
              child: SizedBox(
                width: 160,
                child: StatusIndicator(
                  level: StatusLevel.error,
                  label: 'Diagnostics: 1 issue — critical',
                ),
              ),
            ),
          ),
        ),
      );
      expect(tester.takeException(), isNull);
      final text = tester.widget<Text>(
        find.text('Diagnostics: 1 issue — critical'),
      );
      expect(text.maxLines, 1);
      expect(text.overflow, TextOverflow.ellipsis);
      expect(
        tester.getSize(find.byType(StatusIndicator)).width,
        lessThanOrEqualTo(160),
      );
    },
  );

  testWidgets(
    'Night mode: a changing dot is a ring, the rest filled, so blue and green '
    'differ by shape where only brightness is left (user, 2026-11-04)',
    (tester) async {
      BoxDecoration dot(StatusLevel l) => tester
          .widget<Container>(find.descendant(
              of: find.byKey(ValueKey(l)), matching: find.byType(Container)))
          .decoration! as BoxDecoration;

      await tester.pumpWidget(MaterialApp(
        home: Row(children: [
          for (final l in StatusLevel.values) StatusDot.of(l, key: ValueKey(l)),
        ]),
      ));
      final ring = dot(StatusLevel.changing);
      expect(ring.color, isNull, reason: 'hollow');
      expect(ring.border, Border.all(color: AraColors.accentInfo, width: 2.5));
      for (final l in StatusLevel.values.where((l) => l != StatusLevel.changing)) {
        expect(dot(l).color, l.color,
            reason: '$l filled: the ring is the level\'s, not the colour\'s (info shares the blue)');
      }
    },
  );
}
