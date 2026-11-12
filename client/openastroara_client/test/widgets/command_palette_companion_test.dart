import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/command_palette.dart';

/// Opens the real palette with the Companion Mode seams set to [platform]
/// and [shortestSide], so the test goes through `companionModeProvider`.
Future<void> _openPalette(
  WidgetTester tester, {
  required TargetPlatform platform,
  required double shortestSide,
}) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        companionPlatformProvider.overrideWithValue(platform),
        companionShortestSideProvider.overrideWithValue(shortestSide),
      ],
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
    ),
  );
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
}

Future<void> _search(WidgetTester tester, String q) async {
  await tester.enterText(find.byType(TextField), q);
  await tester.pump();
}

void main() {
  // #1774: the palette is where a search hit turns into a way into the full UI.
  testWidgets('#1774 the command palette on a phone offers no full UI hit', (
    tester,
  ) async {
    await _openPalette(tester, platform: TargetPlatform.iOS, shortestSide: 390);
    await _search(tester, 'experimental');
    expect(find.text('Experimental'), findsNothing);
    await _search(tester, 'planning');
    expect(find.text('Go to Planning'), findsNothing);
    await _search(tester, 'library');
    expect(find.text('Open Image Library'), findsNothing);
  });

  testWidgets(
    '#1774 the command palette on a desktop still offers the full UI hits',
    (tester) async {
      await _openPalette(
        tester,
        platform: TargetPlatform.macOS,
        shortestSide: 1080,
      );
      await _search(tester, 'experimental');
      expect(find.text('Experimental'), findsWidgets);
      await _search(tester, 'planning');
      expect(find.text('Go to Planning'), findsOneWidget);
      await _search(tester, 'library');
      expect(find.text('Open Image Library'), findsOneWidget);
    },
  );
}
