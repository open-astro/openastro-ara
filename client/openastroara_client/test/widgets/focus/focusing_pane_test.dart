import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/guide_focus.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/guide_focus_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/focus/focusing_pane.dart';
import 'package:openastroara/widgets/focus/guide_focus_card.dart';
import 'package:openastroara/widgets/focus/v_curve_chart.dart';
import 'package:openastroara/widgets/focus/focus_section.dart';

class _StubAutofocus extends AutofocusLiveNotifier {
  final AutofocusLive initial;
  _StubAutofocus(this.initial);
  @override
  AutofocusLive build() => initial;
  @override
  Future<void> refresh() async {}
}

class _StubGuideFocus extends GuideFocusNotifier {
  final GuideFocusLive initial;
  _StubGuideFocus(this.initial);
  @override
  GuideFocusLive build() => initial;
  @override
  Future<void> refresh() async {}
}

class _OagPhd2 extends Phd2SettingsNotifier {
  @override
  Phd2Settings build() => const Phd2Settings(guiderSetupType: 'oag', guiderCamera: 'Alpaca Camera [rc91.lan:6800/1]');
}

Widget _harness({
  AutofocusLive autofocus = AutofocusLive.idle,
  GuideFocusLive guide = GuideFocusLive.idle,
  bool oag = false,
}) =>
    ProviderScope(
      overrides: [
        autofocusLiveProvider.overrideWith(() => _StubAutofocus(autofocus)),
        guideFocusProvider.overrideWith(() => _StubGuideFocus(guide)),
        if (oag) phd2SettingsProvider.overrideWith(_OagPhd2.new),
      ],
      child: const MaterialApp(home: Scaffold(body: SizedBox(width: 1100, height: 3200, child: FocusingPane()))),
    );

const _completed = AutofocusRun(
  state: AutofocusRunStates.complete,
  mode: 'classic',
  trigger: 'manual',
  startPosition: 10000,
  finalPosition: 9850,
  finalHfr: 1.42,
  finalStars: 41,
  filter: 'L',
  focuserTemperatureC: 12.5,
  totalSteps: 9,
  completedSteps: 9,
  sweepAttempt: 1,
  durationSeconds: 95,
  probes: [
    AutofocusProbe(index: 1, phase: 'coarse', position: 10000, hfr: 6.1, stars: 0, kept: true),
    AutofocusProbe(index: 2, phase: 'fine', position: 10250, hfr: 3.1, stars: 38, kept: true),
    AutofocusProbe(index: 3, phase: 'fine', position: 10050, hfr: 2.0, stars: 40, kept: true),
    AutofocusProbe(index: 4, phase: 'fine', position: 9850, hfr: 1.45, stars: 41, kept: true),
    AutofocusProbe(index: 5, phase: 'fine', position: 9650, hfr: 2.1, stars: 39, kept: true),
    AutofocusProbe(index: 6, phase: 'fine', position: 9450, hfr: 0, stars: 1, kept: false),
  ],
  fit: AutofocusFit(
    algorithm: 'parabolic',
    rSquared: 0.98,
    bestPosition: 9851,
    predictedHfr: 1.44,
    withinSampledRange: true,
    curve: [AutofocusCurvePoint(9450, 3.0), AutofocusCurvePoint(9850, 1.44), AutofocusCurvePoint(10250, 3.0)],
  ),
);

void main() {
  testWidgets('idle pane shows both cards with the run button disabled without a focuser', (t) async {
    await t.pumpWidget(_harness());
    await t.pump();
    expect(find.text('Smart Focus'), findsOneWidget);
    expect(find.text('Main telescope'), findsOneWidget); // tab
    expect(find.text('Guide camera'), findsOneWidget); // tab
    expect(find.text('MAIN TELESCOPE'), findsOneWidget);
    expect(find.text('Not focused yet'), findsOneWidget);
    expect(find.text('No autofocus yet'), findsOneWidget);
    final run = t.widget<FilledButton>(find.widgetWithText(FilledButton, 'Run autofocus'));
    expect(run.onPressed, isNull, reason: 'no focuser connected');
    expect(find.byType(VCurveChart), findsNothing, reason: 'the empty state hides the chart');
    expect(find.text('GUIDE CAMERA'), findsNothing, reason: 'the guide card lives on its own tab');
    await t.tap(find.text('Guide camera'));
    await t.pumpAndSettle();
    expect(find.text('GUIDE CAMERA'), findsOneWidget);
    expect(find.text('Connect the guider first — the frames come through it.'), findsOneWidget);
  });

  testWidgets('a completed run shows the headline, tiles and details', (t) async {
    await t.pumpWidget(_harness(autofocus: const AutofocusLive(run: _completed, focusedThisSession: true)));
    await t.pump();
    expect(find.text('In focus · HFR 1.42'), findsOneWidget);
    expect(find.text('HFR at focus'), findsOneWidget);
    expect(find.text('1.42'), findsOneWidget);
    expect(find.text('9850'), findsOneWidget);
    expect(find.text('Fit R²'), findsOneWidget);
    expect(find.text('0.980'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Run again'), findsOneWidget);
    // Secondary facts live behind Details.
    expect(find.text('4 of 5 (+1 coarse)'), findsNothing);
    await t.ensureVisible(find.text('Details'));
    await t.tap(find.text('Details'));
    await t.pumpAndSettle();
    expect(find.text('4 of 5 (+1 coarse)'), findsOneWidget);
    expect(find.text('10000 → 9850 (-150)'), findsOneWidget);
    expect(find.text('12.5 °C'), findsOneWidget);
    expect(find.text('classic · manual'), findsOneWidget);
  });

  testWidgets('a running run offers Cancel and reports the sweep phase', (t) async {
    const running = AutofocusRun(state: AutofocusRunStates.running, phase: 'sweep', totalSteps: 9, completedSteps: 3, sweepAttempt: 1);
    await t.pumpWidget(_harness(autofocus: const AutofocusLive(run: running)));
    await t.pump();
    expect(find.widgetWithText(FilledButton, 'Cancel'), findsOneWidget);
    expect(find.text('Sweeping — probe 3 of 9'), findsOneWidget);
    expect(find.byType(LinearProgressIndicator), findsOneWidget);
  });

  testWidgets('a running Smart Focus run counts its shots in Progress', (t) async {
    const running = AutofocusRun(
      state: AutofocusRunStates.running,
      mode: 'smart',
      phase: 'smart',
      totalSteps: 5,
      completedSteps: 2,
      probes: [
        AutofocusProbe(index: 1, phase: 'smart', position: 10150, hfr: 1.95, stars: 42, kept: true),
        AutofocusProbe(index: 2, phase: 'smart', position: 10000, hfr: 1.7, stars: 42, kept: true),
      ],
    );
    await t.pumpWidget(_harness(autofocus: const AutofocusLive(run: running)));
    await t.pump();
    expect(find.text('Smart Focus — shot 2 of 5'), findsOneWidget, reason: 'the shot budget comes from the daemon, never a hard-coded 3');
    expect(find.text('2 / 5'), findsOneWidget);
  });

  testWidgets('a failed run reads as a failure with its reason', (t) async {
    const failed = AutofocusRun(state: AutofocusRunStates.failed, reason: 'only 2 of 9 probes had measurable stars', restoredPosition: 10000);
    await t.pumpWidget(_harness(autofocus: const AutofocusLive(run: failed)));
    await t.pump();
    expect(find.text('Autofocus failed'), findsOneWidget);
    expect(find.text('Only 2 of 9 probes had measurable stars. The focuser is back at 10000.'), findsOneWidget);
    expect(find.text('Nothing measured'), findsOneWidget);
  });

  testWidgets('an OAG waits for the main telescope until told otherwise', (t) async {
    await t.pumpWidget(_harness(oag: true));
    await t.pump();
    await t.tap(find.text('Guide camera'));
    await t.pumpAndSettle();
    expect(find.textContaining('Focus the main telescope first'), findsOneWidget);
    expect(find.text('Already in focus'), findsOneWidget);
  });

  testWidgets('an OAG with the main scope focused this session is not gated', (t) async {
    await t.pumpWidget(_harness(oag: true, autofocus: const AutofocusLive(run: _completed, focusedThisSession: true)));
    await t.pump();
    expect(find.byIcon(Icons.check_circle), findsOneWidget, reason: 'the main tab shows its check');
    await t.tap(find.text('Guide camera'));
    await t.pumpAndSettle();
    expect(find.textContaining('Focus the main telescope first'), findsNothing);
  });

  testWidgets('a live guide loop shows Stop and the readout', (t) async {
    const status = GuideFocusStatus(
      active: true,
      state: GuideFocusStates.running,
      exposureSec: 2,
      seq: 14,
      latest: GuideFocusSample(seq: 14, hfr: 2.31, stars: 6, peakAdu: 21000, fwhm: 3.9),
      bestHfr: 2.1,
      bestSeq: 9,
      recent: [GuideFocusSample(seq: 13, hfr: 2.4, stars: 6, peakAdu: 20000, fwhm: 4.0), GuideFocusSample(seq: 14, hfr: 2.31, stars: 6, peakAdu: 21000, fwhm: 3.9)],
    );
    await t.pumpWidget(_harness(guide: const GuideFocusLive(status: status)));
    await t.pump();
    await t.tap(find.text('Guide camera'));
    // Not pumpAndSettle: the live tab's spinner animates forever.
    for (var i = 0; i < 10; i++) {
      await t.pump(const Duration(milliseconds: 100));
    }
    expect(find.widgetWithText(FilledButton, 'Stop'), findsOneWidget);
    expect(find.text('2.31'), findsOneWidget);
    expect(find.text('px · best so far 2.10'), findsOneWidget);
    expect(find.text('21.0k'), findsOneWidget); // peak ADU
    expect(find.text('Make a move'), findsOneWidget); // only two frames so far
    expect(find.text('Live · HFR 2.31'), findsOneWidget);
  });

  test('phaseText covers every phase', () {
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'running', phase: 'coarse')), 'Finding rough focus…');
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'running', phase: 'sweep', totalSteps: 9, completedSteps: 2, sweepAttempt: 2)), contains('pass 2'));
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'running', phase: 'fitting')), 'Fitting the curve…');
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'running', phase: 'moving')), 'Moving to best focus…');
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'running', phase: 'confirming')), 'Confirming focus…');
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'complete', finalPosition: 1, finalHfr: 1.5, durationSeconds: 120)), 'In focus at 1 — HFR 1.50 in 2m 00s');
    expect(MainFocusCard.phaseText(const AutofocusRun(state: 'cancelled', restoredPosition: 5)), 'Cancelled — focuser restored to 5');
  });

  testWidgets('a bracket-confirmed Smart run shows the calibration curve without an R²', (t) async {
    const run = AutofocusRun(
      state: 'complete', mode: 'smart', finalPosition: 29463, finalHfr: 0.98, finalStars: 285,
      fit: AutofocusFit(algorithm: 'calibration', rSquared: 1, bestPosition: 29463, predictedHfr: 0.96, withinSampledRange: true, curve: []),
    );
    await t.pumpWidget(MaterialApp(home: Scaffold(body: StatRow(tiles: MainFocusCard.tilesFor(run)))));
    final r2 = find.ancestor(of: find.text('Fit R²'), matching: find.byType(StatTile));
    expect(find.descendant(of: r2, matching: find.text('—')), findsOneWidget);
    expect(MainFocusCard.weakFitText(run), isNull);
  });

  test('headlineFor says the state in words', () {
    expect(MainFocusCard.headlineFor(const AutofocusRun()).$1, 'Not focused yet');
    expect(MainFocusCard.headlineFor(const AutofocusRun(state: 'complete', finalHfr: 1.5)).$1, 'In focus · HFR 1.50');
    expect(MainFocusCard.headlineFor(const AutofocusRun(state: 'complete')).$1, 'In focus');
    expect(MainFocusCard.headlineFor(const AutofocusRun(state: 'failed')).$1, 'Autofocus failed');
    expect(MainFocusCard.headlineFor(const AutofocusRun(state: 'cancelled')).$1, 'Cancelled');
    expect(MainFocusCard.headlineFor(const AutofocusRun(state: 'running', phase: 'moving')).$1, 'Moving to best focus…');
  });

  test('guide headlineFor turns green against the expected HFR, live and after a stop', () {
    const sample = GuideFocusSample(seq: 9, hfr: 0.8, stars: 10, peakAdu: 60000, fwhm: 1.5);
    const live = GuideFocusStatus(active: true, state: 'running', bestHfr: 0.68, expectedHfr: 0.7, latest: sample);
    final liveLine = GuideFocusCard.headlineFor(live, gated: false, blocked: false);
    expect(liveLine.$1, 'Live · HFR 0.80 — in focus');
    expect(liveLine.$2, AraColors.accentConnected);
    const stopped = GuideFocusStatus(active: false, state: 'stopped', bestHfr: 0.68, expectedHfr: 0.7, latest: sample);
    final stoppedLine = GuideFocusCard.headlineFor(stopped, gated: false, blocked: false);
    expect(stoppedLine.$1, 'In focus · best HFR 0.68');
    expect(stoppedLine.$2, AraColors.accentConnected);
    expect(stopped.focusedThisSession, isTrue);
    const selfStopped = GuideFocusStatus(active: false, state: 'stopped', bestHfr: 0.68, latest: sample, stopReason: 'in_focus');
    final selfLine = GuideFocusCard.headlineFor(selfStopped, gated: false, blocked: false);
    expect(selfLine.$1, 'In focus — held, stopped · best HFR 0.68');
    expect(selfLine.$2, AraColors.accentConnected);
    expect(selfStopped.focusedThisSession, isTrue, reason: 'the daemon\'s own stop is the verdict even without a target');
    expect(const GuideFocusStatus(active: false, state: 'stopped', bestHfr: 1.2, expectedHfr: 0.7).focusedThisSession, isFalse);
    expect(const GuideFocusStatus(active: false, state: 'stopped', bestHfr: 0.68).focusedThisSession, isFalse, reason: 'no target, no verdict');
  });

  test('guide headlineFor covers idle, gated, blocked, live and stopped', () {
    expect(GuideFocusCard.headlineFor(GuideFocusStatus.idle, gated: false, blocked: false).$1, 'Ready to focus');
    expect(GuideFocusCard.headlineFor(GuideFocusStatus.idle, gated: true, blocked: false).$1, 'Waiting for the main telescope');
    expect(GuideFocusCard.headlineFor(GuideFocusStatus.idle, gated: false, blocked: true).$1, 'Not ready');
    expect(GuideFocusCard.headlineFor(const GuideFocusStatus(active: true, state: 'running'), gated: false, blocked: false).$1, 'Starting…');
    const live = GuideFocusStatus(active: true, state: 'running', bestHfr: 2.0, latest: GuideFocusSample(seq: 3, hfr: 2.0, stars: 5, peakAdu: 1, fwhm: 3));
    expect(GuideFocusCard.headlineFor(live, gated: false, blocked: false).$1, 'Live · HFR 2.00 — best so far');
    const stopped = GuideFocusStatus(state: 'stopped', bestHfr: 2.0, latest: GuideFocusSample(seq: 3, hfr: 2.4, stars: 5, peakAdu: 1, fwhm: 3));
    expect(GuideFocusCard.headlineFor(stopped, gated: false, blocked: false).$1, 'Stopped · best HFR 2.00');
    expect(GuideFocusCard.headlineFor(const GuideFocusStatus(state: 'error'), gated: false, blocked: false).$1, 'Stopped on an error');
  });

  test('failureText reads as sentences', () {
    expect(MainFocusCard.failureText(const AutofocusRun(state: 'failed', reason: 'curve fit unusable', restoredPosition: 7)),
        'Curve fit unusable. The focuser is back at 7.');
    expect(MainFocusCard.failureText(const AutofocusRun(state: 'failed', reason: 'focuser is not connected')),
        'Focuser is not connected.');
    expect(MainFocusCard.failureText(const AutofocusRun(state: 'failed')), 'Autofocus failed — see Support → Logs.');
  });

  group('guideFocusHint', () {
    GuideFocusSample sample(int seq, double hfr, {int stars = 5}) =>
        GuideFocusSample(seq: seq, hfr: hfr, stars: stars, peakAdu: 1, fwhm: 3);
    GuideFocusStatus status(List<double> hfrs, {double? best}) => GuideFocusStatus(
          active: true,
          state: 'running',
          latest: sample(hfrs.length, hfrs.last),
          bestHfr: best ?? hfrs.reduce((a, b) => a < b ? a : b),
          recent: [for (var i = 0; i < hfrs.length; i++) sample(i + 1, hfrs[i])],
        );

    test('waits before the first frame', () {
      expect(guideFocusHint(const GuideFocusStatus(active: true, state: 'running')).advice, TurnAdvice.wait);
    });
    test('no stars is its own advice', () {
      final s = GuideFocusStatus(active: true, state: 'running', latest: sample(1, 0, stars: 0));
      expect(guideFocusHint(s).advice, TurnAdvice.noStars);
    });
    test('a falling HFR says keep turning the same way', () {
      final h = guideFocusHint(status([4.0, 3.6, 3.2, 2.9, 2.6, 2.4], best: 1.5));
      expect(h.advice, TurnAdvice.keepGoing);
      expect(h.title, 'Keep going');
      expect(h.detail, contains('Same way you just moved'));
      expect(h.detail, contains('big moves'), reason: '2.4 is far above the best of 1.5');
    });
    test('a rising HFR says turn back', () {
      final h = guideFocusHint(status([1.6, 1.7, 1.8, 1.9, 2.0, 2.1], best: 1.55));
      expect(h.advice, TurnAdvice.turnBack);
      expect(h.title, 'Go back');
      expect(h.detail, contains('other way'));
      expect(h.detail, contains('small moves'));
    });
    test('within a few percent of the best is "sharpest so far"', () {
      expect(guideFocusHint(status([2.0, 1.8, 1.6, 1.52, 1.5, 1.51])).advice, TurnAdvice.atBest);
    });
    test('a flat trend holds', () {
      expect(guideFocusHint(status([2.0, 2.01, 1.99, 2.0, 2.0, 2.01], best: 1.9)).advice, TurnAdvice.hold);
    });
    test('too few frames asks for a first turn', () {
      expect(guideFocusHint(status([2.0, 2.2], best: 1.5)).advice, TurnAdvice.hold);
    });
    test('at or under the expected in-focus HFR is "In focus", whatever the trend', () {
      // A guide scope at 6.4"/px sits at the detector floor (0.70): 0.76 with ±0.05 jitter
      // used to flip between Keep going and Go back while the focuser never moved.
      final s = GuideFocusStatus(
        active: true,
        state: 'running',
        latest: sample(6, 0.81),
        bestHfr: 0.75,
        expectedHfr: 0.7,
        recent: [for (final (i, h) in [0.76, 0.75, 0.8, 0.74, 0.79, 0.81].indexed) sample(i + 1, h)],
      );
      final h = guideFocusHint(s);
      expect(h.advice, TurnAdvice.atBest);
      expect(h.title, 'In focus');
      expect(h.detail, contains('0.70 px'));
    });
    test('well above the expected HFR still reads the trend', () {
      final s = GuideFocusStatus(
        active: true,
        state: 'running',
        latest: sample(6, 2.4),
        bestHfr: 1.5,
        expectedHfr: 0.7,
        recent: [for (final (i, h) in [4.0, 3.6, 3.2, 2.9, 2.6, 2.4].indexed) sample(i + 1, h)],
      );
      expect(guideFocusHint(s).advice, TurnAdvice.keepGoing);
    });
    test('single-frame jitter on a flat run does not become advice', () {
      expect(guideFocusHint(status([2.0, 2.08, 1.96, 2.02, 2.1, 1.95, 2.07], best: 1.9)).advice, TurnAdvice.hold);
    });
  });

  group('stepSizeText + Details', () {
    test('names the source in words', () {
      expect(MainFocusCard.stepSizeText(const AutofocusRun(stepSize: 23, stepSizeSource: 'measured')), '23 · auto, from the last V-curve');
      expect(MainFocusCard.stepSizeText(const AutofocusRun(stepSize: 23, stepSizeSource: 'cfz')), contains('focuser step size'));
      expect(MainFocusCard.stepSizeText(const AutofocusRun(stepSize: 50, stepSizeSource: 'default')), contains('first sweep'));
      expect(MainFocusCard.stepSizeText(const AutofocusRun(stepSize: 50, stepSizeSource: 'manual')), '50 · manual');
      expect(MainFocusCard.stepSizeText(const AutofocusRun(stepSize: 50)), '50');
      expect(MainFocusCard.stepSizeText(const AutofocusRun()), '—');
    });
    test('Details carries the step size only once a sweep resolved one', () {
      expect(MainFocusCard.detailsFor(_completed).map((r) => r.$1), isNot(contains('Step size')));
      final sized = AutofocusRun(state: 'complete', stepSize: 23, stepSizeSource: 'measured', probes: _completed.probes);
      final row = MainFocusCard.detailsFor(sized).firstWhere((r) => r.$1 == 'Step size');
      expect(row.$2, '23 · auto, from the last V-curve');
    });
  });

  group('weakFitText', () {
    AutofocusProbe fine(int i, int pos, double hfr) =>
        AutofocusProbe(index: i, phase: 'fine', position: pos, hfr: hfr, stars: 30, kept: true);
    // The rig's 04:45 sweep: a narrow V with flat wings around HFR 4, bottom 1.21.
    final rig = AutofocusRun(
      state: 'complete',
      finalPosition: 29372,
      finalHfr: 1.21,
      fit: const AutofocusFit(algorithm: 'hyperbolic', rSquared: 0.568, bestPosition: 29372, predictedHfr: 1.94, withinSampledRange: true, curve: []),
      probes: [
        fine(1, 29660, 3.2), fine(2, 29610, 3.6), fine(3, 29560, 4.07), fine(4, 29510, 3.07), fine(5, 29460, 1.74),
        fine(6, 29410, 1.40), fine(7, 29360, 1.21), fine(8, 29310, 1.98), fine(9, 29260, 3.48), fine(10, 29210, 4.06),
        fine(11, 29160, 3.08), fine(12, 29110, 3.66), fine(13, 29060, 4.02), fine(14, 29010, 3.97), fine(15, 28960, 3.75),
      ],
    );
    test('a plateau-wing sweep with a manual step size suggests a smaller one', () {
      final text = MainFocusCard.weakFitText(rig)!;
      expect(text, contains('R² 0.57'));
      expect(text, contains('A smaller step size would'));
    });
    test('a plateau-wing sweep with an automatic step size says the next run shrinks it', () {
      final auto = AutofocusRun(
        state: 'complete',
        finalPosition: rig.finalPosition,
        finalHfr: rig.finalHfr,
        fit: rig.fit,
        probes: rig.probes,
        stepSize: 50,
        stepSizeSource: 'default',
      );
      final text = MainFocusCard.weakFitText(auto)!;
      expect(text, contains('R² 0.57'));
      expect(text, contains('sizes the next sweep'));
      expect(text, isNot(contains('will use a smaller step size')), reason: 'no promise: the width may not be measurable');
      expect(text, isNot(contains('A smaller step size would')));
    });
    test('a good fit says nothing', () {
      expect(MainFocusCard.weakFitText(_completed), isNull, reason: 'R² 0.98');
    });
    test('a weak fit without a plateau blames the sky', () {
      final noisy = AutofocusRun(
        state: 'complete',
        fit: const AutofocusFit(algorithm: 'parabolic', rSquared: 0.6, bestPosition: 100, predictedHfr: 2, withinSampledRange: true, curve: []),
        probes: [fine(1, 0, 4.0), fine(2, 50, 2.5), fine(3, 100, 3.0), fine(4, 150, 2.2), fine(5, 200, 1.5), fine(6, 250, 2.8)],
      );
      expect(MainFocusCard.weakFitText(noisy), contains('cloud'));
    });
    test('a failed run says nothing here', () {
      expect(MainFocusCard.weakFitText(const AutofocusRun(state: 'failed')), isNull);
    });
  });
}
