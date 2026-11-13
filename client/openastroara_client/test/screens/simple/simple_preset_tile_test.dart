import 'dart:ui' show Tristate;

import 'package:flutter/cupertino.dart' show CupertinoPicker;
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/theme/ara_colors.dart';

Future<List<int>> _pump(
  WidgetTester tester, {
  bool enabled = true,
  TargetPlatform? platform,
}) async {
  tester.view.physicalSize = const Size(800, 1000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final picks = <int>[];
  await tester.pumpWidget(
    MaterialApp(
      theme: ThemeData(platform: platform ?? TargetPlatform.iOS),
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
        ),
      ),
    ),
  );
  return picks;
}

bool _popup(WidgetTester tester) =>
    tester.state<NavigatorState>(find.byType(Navigator)).canPop();

void main() {
  testWidgets('the wheel opens in place and Done sets it', (tester) async {
    final picks = await _pump(tester);
    await tester.tap(find.byKey(const Key('tile')));
    await tester.pumpAndSettle();
    expect(_popup(tester), isFalse, reason: 'no modal popup route');
    tester
        .widget<CupertinoPicker>(find.byType(CupertinoPicker))
        .onSelectedItemChanged!(0);
    await tester.tap(find.text('Done'));
    await tester.pumpAndSettle();
    expect(picks, [5]);
    expect(find.byType(CupertinoPicker), findsNothing);
  });

  testWidgets(
    "each phone its own picker: the iOS wheel, Android's flat number picker",
    (tester) async {
      final picks = await _pump(tester, platform: TargetPlatform.android);
      await tester.tap(find.byKey(const Key('tile')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('simple-wheel-android')), findsOneWidget);
      expect(find.byType(CupertinoPicker), findsNothing);
      tester
          .widget<ListWheelScrollView>(find.byType(ListWheelScrollView))
          .onSelectedItemChanged!(2);
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();
      expect(picks, [20]);
      await _pump(tester, platform: TargetPlatform.iOS);
      await tester.tap(find.byKey(const Key('tile')));
      await tester.pumpAndSettle();
      expect(find.byType(CupertinoPicker), findsOneWidget);
      expect(find.byKey(const Key('simple-wheel-android')), findsNothing);
    },
  );

  testWidgets('a disabled tile greys out and tells screen readers', (
    tester,
  ) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, enabled: false);
    final color = tester
        .widget<RichText>(
          find
              .descendant(
                of: find.byKey(const Key('tile')),
                matching: find.byType(RichText),
              )
              .last,
        )
        .text
        .style!
        .color;
    expect(color, AraColors.textDisabled);
    final node = tester.getSemantics(find.byType(InkWell));
    expect(node.label, 'Frames');
    expect(node.value, '× 10');
    expect(node.flagsCollection.isEnabled, Tristate.isFalse);
    handle.dispose();
  });

  testWidgets('a tile disabled while open folds away and takes no pick', (
    tester,
  ) async {
    final picks = await _pump(tester);
    await tester.tap(find.byKey(const Key('tile')));
    await tester.pumpAndSettle();
    expect(find.byType(CupertinoPicker), findsOneWidget);
    await _pump(tester, enabled: false);
    await tester.pumpAndSettle();
    expect(find.byType(CupertinoPicker), findsNothing);
    expect(picks, isEmpty);
  });

  group('SimpleCountField', () {
    Future<List<int>> pumpCount(
      WidgetTester tester, {
      int value = 10,
      double width = 300,
    }) async {
      final sets = <int>[];
      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Center(
              child: SizedBox(
                width: width,
                child: StatefulBuilder(
                  builder: (context, setState) => SimpleCountField(
                    id: 'n',
                    label: 'Frames',
                    value: value,
                    onChanged: (n) => setState(() {
                      value = n;
                      sets.add(n);
                    }),
                  ),
                ),
              ),
            ),
          ),
        ),
      );
      return sets;
    }

    testWidgets('a typed count is set as typed, any number, not only presets', (
      tester,
    ) async {
      final sets = await pumpCount(tester);
      await tester.enterText(find.byKey(const Key('n')), '47');
      await tester.pump();
      expect(sets.last, 47);
      expect(
        tester.widget<TextField>(find.byKey(const Key('n'))).keyboardType,
        TextInputType.number,
        reason: 'the phone\'s own number pad',
      );
    });

    testWidgets('- and + step by one and stop at 1', (tester) async {
      final sets = await pumpCount(tester, value: 2);
      await tester.tap(find.byKey(const Key('n-up')));
      await tester.pump();
      expect(sets, [3]);
      await tester.tap(find.byKey(const Key('n-down')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('n-down')));
      await tester.pump();
      expect(sets, [3, 2, 1]);
      expect(
        tester.widget<IconButton>(find.byKey(const Key('n-down'))).onPressed,
        isNull,
        reason: 'nothing under 1',
      );
    });

    testWidgets(
      'an empty or 0 field puts the last count back when it is left',
      (tester) async {
        final sets = await pumpCount(tester, value: 12);
        await tester.enterText(find.byKey(const Key('n')), '0');
        await tester.pump();
        expect(sets, isEmpty);
        await tester.testTextInput.receiveAction(TextInputAction.done);
        await tester.pump();
        expect(find.text('12'), findsOneWidget);
      },
    );

    testWidgets(
      'too narrow for the buttons, the field alone stays and takes typing',
      (tester) async {
        final sets = await pumpCount(tester, width: 93);
        expect(tester.takeException(), isNull);
        expect(find.byKey(const Key('n-up')), findsNothing);
        await tester.enterText(find.byKey(const Key('n')), '30');
        await tester.pump();
        expect(sets, [30]);
      },
    );
  });
}
