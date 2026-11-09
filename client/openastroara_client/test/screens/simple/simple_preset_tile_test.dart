import 'dart:ui' show Tristate;

import 'package:flutter/cupertino.dart' show CupertinoPicker;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/theme/ara_colors.dart';

Future<List<int>> _pump(WidgetTester tester, {bool wheel = false, bool enabled = true}) async {
  tester.view.physicalSize = const Size(800, 1000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final picks = <int>[];
  await tester.pumpWidget(
    MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: SimplePresetTile<int>(
          key: const Key('tile'),
          label: 'Frames',
          value: 10,
          options: const [5, 10, 20],
          format: (n) => '× $n',
          onSelected: enabled ? picks.add : null,
          wheel: wheel,
        ),
      ),
    ),
  );
  return picks;
}

bool _popup(WidgetTester tester) => tester.state<NavigatorState>(find.byType(Navigator)).canPop();

void main() {
  testWidgets('a tap folds the list out in place; a pick sets it and folds it away', (tester) async {
    final picks = await _pump(tester);
    expect(find.byKey(const Key('simple-preset-2')), findsNothing, reason: 'folded until tapped');
    await tester.tap(find.byKey(const Key('tile')));
    await tester.pumpAndSettle();
    expect(_popup(tester), isFalse, reason: 'no menu route');
    expect(tester.getSize(find.byKey(const Key('simple-preset-2'))).height, greaterThanOrEqualTo(44));
    await tester.tap(find.byKey(const Key('simple-preset-2')));
    await tester.pumpAndSettle();
    expect(picks, [20]);
    expect(find.byKey(const Key('simple-preset-2')), findsNothing);
  });

  testWidgets('the wheel opens in place and Done sets it', (tester) async {
    final picks = await _pump(tester, wheel: true);
    await tester.tap(find.byKey(const Key('tile')));
    await tester.pumpAndSettle();
    expect(_popup(tester), isFalse, reason: 'no modal popup route');
    tester.widget<CupertinoPicker>(find.byType(CupertinoPicker)).onSelectedItemChanged!(0);
    await tester.tap(find.text('Done'));
    await tester.pumpAndSettle();
    expect(picks, [5]);
    expect(find.byType(CupertinoPicker), findsNothing);
  });

  testWidgets('a disabled tile greys out and tells screen readers', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, enabled: false);
    final color = tester.widget<RichText>(find.descendant(of: find.byKey(const Key('tile')), matching: find.byType(RichText)).last).text.style!.color;
    expect(color, AraColors.textDisabled);
    final node = tester.getSemantics(find.byType(InkWell));
    expect(node.label, 'Frames');
    expect(node.value, '× 10');
    expect(node.flagsCollection.isEnabled, Tristate.isFalse);
    handle.dispose();
  });

  testWidgets('a tile disabled while open folds away and takes no pick', (tester) async {
    final picks = await _pump(tester);
    await tester.tap(find.byKey(const Key('tile')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-preset-2')), findsOneWidget);
    await _pump(tester, enabled: false);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('simple-preset-2')), findsNothing);
    expect(picks, isEmpty);
  });
}
