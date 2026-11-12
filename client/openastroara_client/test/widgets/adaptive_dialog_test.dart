import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/adaptive_dialog.dart';

void main() {
  testWidgets(
    'showAdaptiveAlert opens the iOS alert route on iOS (a tap outside leaves it open) '
    'and the Material dialog route elsewhere (a tap outside closes it)',
    (tester) async {
      ModalRoute<Object?>? route;
      await tester.pumpWidget(
        MaterialApp(
          home: Builder(
            builder: (context) => Center(
              child: TextButton(
                onPressed: () => showAdaptiveAlert<void>(
                  context: context,
                  builder: (dialogContext) {
                    route = ModalRoute.of(dialogContext);
                    return adaptiveAlertDialog(title: const Text('Question'));
                  },
                ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      final ios =
          Theme.of(tester.element(find.text('open'))).platform ==
          TargetPlatform.iOS;
      expect(
        route,
        ios ? isA<CupertinoDialogRoute<void>>() : isA<DialogRoute<void>>(),
      );

      await tester.tapAt(const Offset(5, 5));
      await tester.pumpAndSettle();
      expect(find.text('Question'), ios ? findsOneWidget : findsNothing);
    },
    variant: TargetPlatformVariant({
      TargetPlatform.iOS,
      TargetPlatform.android,
      TargetPlatform.macOS,
    }),
  );
}
