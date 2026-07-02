import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/command_palette.dart';

void main() {
  testWidgets(
      '§68.4 activating a help hit closes the palette and opens the help sheet',
      (tester) async {
    await tester.pumpWidget(ProviderScope(
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => Center(
              child: ElevatedButton(
                onPressed: () => showCommandPalette(context),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField), 'equipment hub down');
    await tester.pump();
    expect(find.text('AlpacaBridge not detected?'), findsOneWidget,
        reason: 'the troubleshoot help entry is the top hit');

    await tester.tap(find.text('AlpacaBridge not detected?'));
    await tester.pumpAndSettle();

    // The help sheet is open: title + the install command from the body.
    expect(find.text('AlpacaBridge not detected?'), findsOneWidget);
    expect(find.textContaining('sudo apt install alpaca-bridge'), findsOneWidget);
    // And the palette's search field is gone (the palette popped).
    expect(find.byType(TextField), findsNothing);
  });
}
