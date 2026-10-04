import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
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
}
