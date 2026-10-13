import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/util/optimal_sub_calculator.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';
import 'package:openastroara/widgets/sky_atlas/optimal_sub_calculator_dialog.dart';

const _rig = CalculatorInputs(
  readNoiseE: 1.5,
  fullWellE: 50000,
  electronsPerAdu: 0.8,
  pixelSizeUm: 3.76,
  apertureMm: 91,
  focalLengthMm: 447,
  quantumEfficiency: 0.8,
  skyMagPerArcsec2: 20.5,
  filterBandwidthNm: 100,
);

Future<void> _pump(
  WidgetTester tester, {
  Map<String, double> k = const {},
  Map<String, double> bandwidth = const {},
}) async {
  tester.view.physicalSize = const Size(1400, 2400);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      child: MaterialApp(
        home: OptimalSubCalculatorDialog(
          initial: _rig,
          kByKind: k,
          bandwidthByKind: bandwidth,
        ),
      ),
    ),
  );
  await tester.pump();
}

Future<void> _type(WidgetTester tester, String label, String text) async {
  final row = find.widgetWithText(EditableNumberRow, label);
  await tester.scrollUntilVisible(
    row,
    200,
    scrollable: find.byType(Scrollable).first,
  );
  await tester.enterText(
    find.descendant(of: row, matching: find.byType(TextField)),
    text,
  );
  await tester.testTextInput.receiveAction(TextInputAction.done);
  await tester.pumpAndSettle();
}

String _suggested(WidgetTester tester) =>
    tester.widget<Text>(find.textContaining('Suggested exposure:')).data!;

void main() {
  testWidgets('#1140 NEXTGEN §5 the calculator recomputes as inputs change', (
    tester,
  ) async {
    await _pump(tester);
    expect(find.text('Optimal Sub calculator'), findsOneWidget);
    // Integer rows keep their zeros: a focus-out commit must not read 50000 back as 5.
    expect(
      tester
          .widget<EditableNumberRow>(
            find.widgetWithText(EditableNumberRow, 'Full well (e⁻)'),
          )
          .currentValue,
      '50000',
    );
    expect(
      tester
          .widget<EditableNumberRow>(
            find.widgetWithText(EditableNumberRow, 'Peak altitude (°)'),
          )
          .currentValue,
      '60',
    );
    final before = _suggested(tester);
    expect(
      find.textContaining('Integration budget:'),
      findsNothing,
      reason: 'no target yet',
    );

    // A darker sky lengthens the Glover floor.
    await _type(tester, 'Sky brightness (mag/arcsec²)', '21.9');
    final darker = _suggested(tester);
    expect(darker, isNot(before));

    // A target surface brightness turns the budget on, with its audit lines.
    await _type(tester, 'Surface brightness (mag/arcsec²)', '22');
    expect(find.textContaining('Integration budget:'), findsOneWidget);
    expect(find.text('Sky model calibration'), findsOneWidget);
    expect(find.text('not fitted — spec-sheet model'), findsOneWidget);

    // An impossible input is a sentence, not a crash.
    await _type(tester, 'Aperture (mm)', '0');
    expect(find.text('Needs a positive aperture.'), findsOneWidget);
    expect(find.textContaining('Suggested exposure:'), findsNothing);
    await _type(tester, 'Aperture (mm)', '91');
    await _type(tester, 'e⁻/ADU (0 = unknown)', '-0.8');
    expect(find.text('Needs e⁻/ADU of 0 or more.'), findsOneWidget);
    expect(find.byType(ErrorWidget), findsNothing);
  });

  testWidgets(
    '#1140 picking a filter kind swaps in its passband and fitted k',
    (tester) async {
      await _pump(tester, k: const {'ha': 0.7}, bandwidth: const {'ha': 3});
      expect(
        find.textContaining('(k 0.70 applied)'),
        findsNothing,
        reason: 'L has no fitted k',
      );
      await tester.tap(find.byType(DropdownButton<FilterKind>));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Hα').last);
      await tester.pumpAndSettle();
      expect(find.textContaining('(k 0.70 applied)'), findsOneWidget);
      final passband = tester.widget<EditableNumberRow>(
        find.widgetWithText(EditableNumberRow, 'Filter passband (nm)'),
      );
      expect(
        passband.currentValue,
        '3',
        reason: "the profile's declared 3 nm beats the kind's 7 nm default",
      );
    },
  );
}
