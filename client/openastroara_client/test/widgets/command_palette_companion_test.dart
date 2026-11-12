import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/command_palette.dart';

/// Opens the palette with Companion Mode forced to [companion] and types [query].
Future<void> _search(WidgetTester tester, {required bool companion, required String query}) async {
  await tester.pumpWidget(ProviderScope(
    overrides: [companionModeProvider.overrideWithValue(companion)],
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
  await tester.enterText(find.byType(TextField), query);
  await tester.pump();
}

void main() {
  // #1774 — the palette is where a search hit turns into a way into the full UI.
  testWidgets('Companion Mode: the palette has no hit that opens the full UI', (tester) async {
    await _search(tester, companion: true, query: 'planning');
    expect(find.text('Go to Planning'), findsNothing);

    await tester.enterText(find.byType(TextField), 'library');
    await tester.pump();
    expect(find.text('Open Image Library'), findsNothing);
  });

  testWidgets('outside Companion Mode the palette offers the full UI hits', (tester) async {
    await _search(tester, companion: false, query: 'planning');
    expect(find.text('Go to Planning'), findsOneWidget);

    await tester.enterText(find.byType(TextField), 'library');
    await tester.pump();
    expect(find.text('Open Image Library'), findsOneWidget);
  });
}
