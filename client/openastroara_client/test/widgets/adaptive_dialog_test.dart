import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/theme/ara_theme.dart';
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

  // #1843 A4: the theme error is error-text now; a filled destructive action
  // stays Fault Red, a text one takes the theme error as its label.
  testWidgets(
    'a destructive dialog action fills Fault Red and labels a text one in error-text',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildAraTheme().copyWith(platform: TargetPlatform.android),
          home: Builder(
            builder: (context) => Column(
              children: [
                adaptiveDialogAction(
                  context,
                  onPressed: () {},
                  isDestructive: true,
                  filled: true,
                  child: const Text('Stop'),
                ),
                adaptiveDialogAction(
                  context,
                  onPressed: () {},
                  isDestructive: true,
                  child: const Text('Delete'),
                ),
              ],
            ),
          ),
        ),
      );
      final filled = tester.widget<FilledButton>(find.byType(FilledButton));
      expect(filled.style?.backgroundColor?.resolve({}), AraColors.accentError);
      final text = tester.widget<TextButton>(find.byType(TextButton));
      expect(text.style?.foregroundColor?.resolve({}), AraColors.errorText);
    },
  );
}
