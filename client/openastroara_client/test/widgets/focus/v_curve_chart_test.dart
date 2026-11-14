import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/widgets/focus/v_curve_chart.dart';

void main() {
  // #1898: the padded axis ends got a label of their own, a fraction of a step from the next tick.
  testWidgets('in Simple Mode the position axis labels only its even steps, never the padded ends (#1898)', (tester) async {
    final run = AutofocusRun(state: AutofocusRunStates.running, phase: 'sweep', probes: [
      for (final (i, p, h) in [(0, 10100, 3.4), (1, 10200, 2.1), (2, 10300, 3.8)])
        AutofocusProbe(index: i, phase: 'fine', position: p, hfr: h, stars: 50, kept: true),
    ]);
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: SizedBox(width: 400, height: 200, child: VCurveChart(run: run, simple: true))),
    ));
    await tester.pump();
    final positions = [
      for (final t in tester.widgetList<Text>(find.byType(Text)))
        if (int.tryParse(t.data ?? '') case final v? when v >= 1000) v,
    ];
    expect(positions, isNotEmpty);
    // 10088..10312 after the 6 % pad; the step is 50.
    expect(positions.where((v) => v % 50 != 0), isEmpty, reason: '$positions');
  });
}
