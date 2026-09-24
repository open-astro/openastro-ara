import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequencer_toolbar.dart';

/// The Run tab toolbar must keep every action reachable at any width: the
/// old horizontal scroll row silently hid Delete / Validate / Export on a
/// phone or a half-width window. Utilities that don't fit now fold into a
/// "More" menu and the run verbs never leave the screen.
Future<void> pumpAt(WidgetTester tester, double width) async {
  await tester.binding.setSurfaceSize(Size(width, 800));
  addTearDown(() => tester.binding.setSurfaceSize(null));
  await tester.pumpWidget(
    ProviderScope(
      overrides: [sequenceApiProvider.overrideWithValue(null)],
      child: const MaterialApp(home: Scaffold(body: SequencerToolbar())),
    ),
  );
  await tester.pump();
}

Finder moreMenu() => find.byTooltip('More actions');

/// A row of the More menu carrying [label] (the item is generic over a
/// private type, so match on the base class).
Finder menuRow(String label) => find.ancestor(
  of: find.text(label),
  matching: find.byWidgetPredicate((w) => w is PopupMenuItem),
);

bool rowEnabled(WidgetTester tester, String label) =>
    (tester.widget(menuRow(label)) as PopupMenuItem).enabled;

void main() {
  testWidgets('wide: every utility is inline and there is no More menu', (
    tester,
  ) async {
    await pumpAt(tester, 2000);
    for (final l in [
      'New',
      'Load',
      'Import',
      'Save',
      'Export',
      'Validate',
      'Delete',
    ]) {
      expect(find.widgetWithText(TextButton, l), findsOneWidget, reason: l);
    }
    expect(moreMenu(), findsNothing);
    expect(find.widgetWithText(FilledButton, 'Run'), findsOneWidget);
  });

  testWidgets('medium: the run verbs keep labels, trailing utilities fold '
      'into More', (tester) async {
    await pumpAt(tester, 1000);
    expect(find.widgetWithText(TextButton, 'New'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Run'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Abort'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Delete'), findsNothing);
    expect(moreMenu(), findsOneWidget);

    await tester.tap(moreMenu());
    await tester.pumpAndSettle();
    expect(menuRow('Delete'), findsOneWidget);
    expect(menuRow('Validate'), findsOneWidget);
    // Offline with nothing selected → Delete is disabled in the menu too.
    expect(rowEnabled(tester, 'Delete'), isFalse);
    // Load fits inline at this width and never needs a server.
    expect(
      tester
          .widget<TextButton>(find.widgetWithText(TextButton, 'Load'))
          .onPressed,
      isNotNull,
    );
  });

  testWidgets('phone: run verbs shrink to icons with tooltips, all utilities '
      'live in More, the status line yields', (tester) async {
    await pumpAt(tester, 400);
    expect(find.byTooltip('Run'), findsOneWidget);
    expect(find.byTooltip('Pause'), findsOneWidget);
    expect(find.byTooltip('Skip'), findsOneWidget);
    expect(find.byTooltip('Abort'), findsOneWidget);
    expect(find.text('Run'), findsNothing);
    expect(find.textContaining('Offline'), findsNothing);
    expect(moreMenu(), findsOneWidget);
    // Whatever fits stays inline (a lone New may); the rest — Delete above
    // all, the one the bug report was about — is in the menu.
    const all = [
      'New',
      'Load',
      'Import',
      'Save',
      'Export',
      'Validate',
      'Delete',
    ];
    final inline = [
      for (final l in all)
        if (find.widgetWithText(TextButton, l).evaluate().isNotEmpty) l,
    ];
    expect(inline, isNot(contains('Delete')));
    expect(inline.length, lessThan(3));

    await tester.tap(moreMenu());
    await tester.pumpAndSettle();
    for (final l in all.where((l) => !inline.contains(l))) {
      expect(menuRow(l), findsOneWidget, reason: l);
    }
  });

  testWidgets('nothing overflows the row at any width', (tester) async {
    for (final w in [320.0, 360.0, 480.0, 640.0, 800.0, 1024.0, 1280.0]) {
      await pumpAt(tester, w);
      expect(tester.takeException(), isNull, reason: 'width $w');
    }
  });
}
