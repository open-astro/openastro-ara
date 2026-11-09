import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_preflight.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/widgets/focus/v_curve_chart.dart';

FocuserStatus _focuser({int? position = 1000, bool absolute = true, int max = 1100, String runtime = 'idle'}) =>
    FocuserStatus(
      deviceId: 'f',
      name: 'Focuser',
      connectionState: EquipmentConnectionState.connected,
      capabilities: FocuserCapabilities(
        minPosition: 0,
        maxPosition: max,
        stepSizeUm: 1,
        canTempComp: false,
        absoluteFocuser: absolute,
      ),
      runtimeState: runtime,
      position: position,
      temperature: null,
      tempCompEnabled: false,
    );

AutofocusProbe _probe(int i, double hfr, {bool kept = true}) =>
    AutofocusProbe(index: i, phase: AutofocusProbePhases.fine, position: 900 + i * 50, hfr: hfr, stars: 40, kept: kept);

class _Focuser extends FocuserNotifier {
  _Focuser(this.status);
  final FocuserStatus? status;
  final moves = <int>[];
  @override
  Future<FocuserStatus?> build() async => status;
  @override
  Future<bool> move(int targetPosition, {bool useTempComp = false}) async {
    moves.add(targetPosition);
    return true;
  }
}

class _Pick extends MainFocusMethodNotifier {
  _Pick(this.method);
  final MainFocusMethod method;
  @override
  MainFocusMethod? build() => method;
}

class _Live extends AutofocusLiveNotifier {
  _Live(this.live);
  final AutofocusLive live;
  int starts = 0;
  int cancels = 0;
  @override
  AutofocusLive build() => live;
  @override
  Future<void> start() async => starts++;
  @override
  Future<void> cancel() async => cancels++;
}

late ProviderContainer _c;

class _Pilot extends SimplePreflightNotifier {
  int nexts = 0;
  @override
  SimplePreflightStep? build() => SimplePreflightStep.focus;
  @override
  void next() => nexts++;
}

class _PierPrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

Future<(_Focuser, _Live)> _pump(WidgetTester tester,
    {FocuserStatus? focuser, AutofocusRun run = AutofocusRun.idle, Uint8List? frame}) async {
  final f = _Focuser(focuser);
  final l = _Live(AutofocusLive(run: run, frame: frame));
  _c = ProviderContainer(overrides: [
    focuserProvider.overrideWith(() => f),
    autofocusLiveProvider.overrideWith(() => l),
  ]);
  addTearDown(_c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: _c,
    child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusPanel()))),
  ));
  await tester.pump();
  return (f, l);
}

void main() {
  group('simpleFocusHfr', () {
    test('nothing before a run', () => expect(simpleFocusHfr(AutofocusRun.idle), isNull));
    test('the latest kept probe while running', () {
      final run = AutofocusRun(state: AutofocusRunStates.running, probes: [_probe(0, 3.1), _probe(1, 2.4), _probe(2, 9, kept: false)]);
      expect(simpleFocusHfr(run), 2.4);
    });
    test('the confirmed HFR once complete', () {
      final run = AutofocusRun(state: AutofocusRunStates.complete, finalHfr: 1.98, probes: [_probe(0, 2.4)]);
      expect(simpleFocusHfr(run), 1.98);
    });
  });

  testWidgets('Main / Guide on top, Auto / Manual on a row under it; the guide camera has no Auto / Manual row',
      (tester) async {
    // User, 2026-11-04: Main, Guide, Auto, Manual on one line read as four of a kind.
    await _pump(tester);
    final main = tester.getRect(find.byKey(const Key('simple-focus-scope-main')));
    final guide = tester.getRect(find.byKey(const Key('simple-focus-scope-guide')));
    final auto = tester.getRect(find.byKey(const Key('simple-focus-method-auto')));
    expect(guide.top, main.top, reason: 'one row');
    expect(auto.top, greaterThan(main.bottom), reason: 'the method under the camera');
    expect(auto.left, main.left);
    _c.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.guide);
    await tester.pump();
    expect(find.byKey(const Key('simple-focus-method-auto')), findsNothing);
    expect(find.byKey(const Key('simple-focus-method-manual')), findsNothing);
  });

  testWidgets('no focuser: Manual, the Bahtinov helper; no Autofocus button', (tester) async {
    await _pump(tester);
    expect(find.text('Fit the mask, then Start'), findsOneWidget);
    expect(find.text('Autofocus'), findsNothing);
  });

  testWidgets('a connected focuser: the HFR, no position or nudges (the Focuser window\'s dial moves it)',
      (tester) async {
    await _pump(tester, focuser: _focuser());
    expect(find.text('HFR —'), findsOneWidget);
    expect(find.textContaining('Position'), findsNothing);
    for (final icon in [Icons.chevron_left, Icons.chevron_right, Icons.keyboard_double_arrow_left]) {
      expect(find.byIcon(icon), findsNothing);
    }
  });

  testWidgets('while a run goes: the curve, no Cancel (the shutter\'s square)', (tester) async {
    final run = AutofocusRun(
      state: AutofocusRunStates.running,
      phase: 'sweep',
      totalSteps: 7,
      completedSteps: 2,
      stepSize: 80,
      probes: [_probe(0, 3.1), _probe(1, 2.4)],
    );
    await _pump(tester, focuser: _focuser(), run: run);
    expect(find.text('HFR 2.40'), findsOneWidget);
    expect(find.textContaining('Sweeping'), findsOneWidget);
    expect(find.text('Cancel'), findsNothing);
  });

  testWidgets('the V-curve is tall enough to read (at 80 pt its axes left the plot 18 pt)', (tester) async {
    final run = AutofocusRun(
      state: AutofocusRunStates.running,
      phase: 'sweep',
      totalSteps: 7,
      completedSteps: 2,
      stepSize: 80,
      probes: [_probe(0, 3.1), _probe(1, 2.4)],
    );
    await _pump(tester, focuser: _focuser(), run: run);
    expect(tester.getSize(find.byType(VCurveChart)).height, greaterThanOrEqualTo(160));
  });

  // A finished Smart Focus whose fit was weak (R² 0.55), like the rig's.
  final weak = AutofocusRun(
    state: AutofocusRunStates.complete,
    finalHfr: 1.01,
    finalPosition: 29463,
    finalStars: 299,
    probes: [for (var i = 0; i < 7; i++) _probe(i, [4.2, 3.4, 2.8, 1.4, 2.6, 3.3, 4.8][i])],
    fit: const AutofocusFit(
        algorithm: 'hyperbolic', rSquared: 0.554, bestPosition: 29463, predictedHfr: 1.7, withinSampledRange: true, curve: []),
  );

  testWidgets('a finished run: best position, stars and fit R², all on view', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    for (final t in ['Best position', '29463', 'Stars', '299', 'Fit R²', '0.55']) {
      expect(find.text(t), findsOneWidget, reason: t);
    }
    expect(find.textContaining('Details'), findsNothing, reason: 'nothing folded away');
  });

  testWidgets('Auto keeps one height from start to finish, a weak fit included (#1717)', (tester) async {
    final (_, live) = await _pump(tester, focuser: _focuser(), run: const AutofocusRun());
    double height() => tester.getSize(find.byType(SimpleFocusPanel)).height;
    final start = height();
    for (final run in [
      AutofocusRun(state: AutofocusRunStates.running, phase: 'sweep', totalSteps: 7, stepSize: 80),
      AutofocusRun(
        state: AutofocusRunStates.running,
        phase: 'sweep',
        totalSteps: 7,
        completedSteps: 2,
        stepSize: 80,
        probes: [_probe(0, 3.1), _probe(1, 2.4)],
      ),
      weak,
    ]) {
      live.state = AutofocusLive(run: run);
      await tester.pump();
      expect(height(), start, reason: '${run.state} ${run.probes.length} probes');
    }
  });

  testWidgets('Measured stars, hidden for its room at large text before a frame, is not read out', (tester) async {
    tester.view.physicalSize = const Size(1600, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    tester.platformDispatcher.textScaleFactorTestValue = 1.3;
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final handle = tester.ensureSemantics();
    await _pump(tester, focuser: _focuser());
    expect(find.bySemanticsLabel('Measured stars'), findsNothing);
    handle.dispose();
  });

  group('a long focus error', () {
    final long = List.filled(60, 'the star field drifted').join(' ');

    testWidgets('is never cut, and the panel keeps its height', (tester) async {
      final (_, live) = await _pump(tester, focuser: _focuser());
      final start = tester.getSize(find.byType(SimpleFocusPanel)).height;
      live.state = AutofocusLive(run: AutofocusRun.idle, error: long);
      await tester.pump();
      expect(tester.renderObject<RenderParagraph>(find.text(long)).didExceedMaxLines, isFalse);
      expect(tester.takeException(), isNull);
      expect(tester.getSize(find.byType(SimpleFocusPanel)).height, start);
      expect(find.byType(VCurveChart), findsOneWidget);
    });

    testWidgets('over a weak fit takes the warning\'s room, so nothing overflows', (tester) async {
      final (_, live) = await _pump(tester, focuser: _focuser());
      live.state = AutofocusLive(run: weak, error: long);
      await tester.pump();
      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('simple-focus-weak-fit')), findsNothing);
    });
  });

  testWidgets('a weak fit says so in the panel, in words', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    expect(find.textContaining('Weak curve fit (R² 0.55)'), findsOneWidget);
  });

  testWidgets('the weak-fit warning is icon + colour + text, not a tint alone (night mode drops tints)', (tester) async {
    // DESIGN.md: pair every warning with icon + colour + text (impeccable audit, P2).
    await _pump(tester, focuser: _focuser(), run: weak);
    final box = find.byKey(const Key('simple-focus-weak-fit'));
    expect(find.descendant(of: box, matching: find.byIcon(Icons.warning_amber_rounded)), findsOneWidget);
    final icon = tester.widget<Icon>(find.descendant(of: box, matching: find.byIcon(Icons.warning_amber_rounded)));
    expect(icon.color, AraColors.accentWarning);
    final text = tester.widget<Text>(find.descendant(of: box, matching: find.textContaining('Weak curve fit')));
    expect(text.style?.color, AraColors.accentWarning);
  });

  testWidgets('the focuser line: where it is and which (before a result; then Best position says it)', (tester) async {
    await _pump(tester, focuser: _focuser());
    expect(find.text('Focuser at 1000 · Focuser'), findsOneWidget);
  });

  testWidgets('the measured stars on the frame: off to start, a tap shows them', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak, frame: Uint8List.fromList([1, 2, 3]));
    final off = find.byKey(const Key('simple-focus-stars-off'));
    expect(tester.widget<SimpleSegment>(off).selected, isTrue);
    await tester.tap(find.byKey(const Key('simple-focus-stars-on')));
    await tester.pump();
    expect(_c.read(simpleFocusStarsShownProvider), isTrue);
  });

  testWidgets('no frame from the run: no stars switch', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    expect(find.byKey(const Key('simple-focus-stars-on')), findsNothing);
  });

  testWidgets('Auto / Manual at the top of the panel picks the method (the chip follows)', (tester) async {
    await _pump(tester, focuser: _focuser());
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-focus-method-auto'))).selected, isTrue,
        reason: 'a connected focuser: Auto to start');
    await tester.tap(find.byKey(const Key('simple-focus-method-manual')));
    await tester.pump();
    expect(_c.read(mainFocusMethodProvider), MainFocusMethod.bahtinov);
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-focus-method-manual'))).selected, isTrue);
  });

  testWidgets('the method is locked while autofocus runs', (tester) async {
    await _pump(tester,
        focuser: _focuser(), run: AutofocusRun(state: AutofocusRunStates.running, probes: [_probe(0, 3.1)]));
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-focus-method-manual'))).onTap, isNull);
  });

  testWidgets("Preflight on a permanent pier: focus is the last step, so its Done starts imaging", (tester) async {
    final f = _Focuser(_focuser());
    final pilot = _Pilot();
    _c = ProviderContainer(overrides: [
      focuserProvider.overrideWith(() => f),
      autofocusLiveProvider.overrideWith(() => _Live(AutofocusLive(run: weak))),
      setupFocusStateProvider.overrideWithValue(SetupStepState.done),
      setupPolarAlignStateProvider.overrideWithValue(SetupStepState.pending),
      simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
      simplePreflightProvider.overrideWith(() => pilot),
    ]);
    addTearDown(_c.dispose);
    await _c.read(simplePermanentPierProvider.future);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: _c,
      child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusPanel()))),
    ));
    await tester.pump();
    await tester.tap(find.text('Done · Start imaging'));
    expect(pilot.nexts, 1);
  });

  for (final method in MainFocusMethod.values) {
    testWidgets('Preflight with focus last: ${method.name} keeps one height when focus finishes', (tester) async {
      Future<double> height(SetupStepState focus) async {
        final c = ProviderContainer(overrides: [
          focuserProvider.overrideWith(() => _Focuser(_focuser())),
          mainFocusMethodProvider.overrideWith(() => _Pick(method)),
          setupFocusStateProvider.overrideWithValue(focus),
          setupPolarAlignStateProvider.overrideWithValue(SetupStepState.pending),
          simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
          simplePreflightProvider.overrideWith(_Pilot.new),
        ]);
        addTearDown(c.dispose);
        await c.read(simplePermanentPierProvider.future);
        await tester.pumpWidget(UncontrolledProviderScope(
          container: c,
          child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusPanel()))),
        ));
        await tester.pump();
        return tester.getSize(find.byType(SimpleFocusPanel)).height;
      }

      final running = await height(SetupStepState.pending);
      expect(await height(SetupStepState.done), running);
    });
  }

  testWidgets('Preflight on a permanent pier, focused by hand: Manual has the Done too', (tester) async {
    // #1486 review: the Done only showed in Auto, so a Bahtinov focus could only Skip.
    final pilot = _Pilot();
    _c = ProviderContainer(overrides: [
      focuserProvider.overrideWith(() => _Focuser(null)),
      mainFocusMethodProvider.overrideWith(() => _Pick(MainFocusMethod.bahtinov)),
      setupFocusStateProvider.overrideWithValue(SetupStepState.done),
      setupPolarAlignStateProvider.overrideWithValue(SetupStepState.pending),
      simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
      simplePreflightProvider.overrideWith(() => pilot),
    ]);
    addTearDown(_c.dispose);
    await _c.read(simplePermanentPierProvider.future);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: _c,
      child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusPanel()))),
    ));
    await tester.pump();
    expect(_c.read(mainFocusMethodProvider), MainFocusMethod.bahtinov, reason: 'precondition: Manual');
    await tester.tap(find.text('Done · Start imaging'));
    expect(pilot.nexts, 1);
  });

  group('the shutter follows the chip', () {
    Future<_Live> shutter(WidgetTester tester, MainFocusMethod pick,
        {FocuserStatus? focuser, AutofocusRun run = AutofocusRun.idle}) async {
      final l = _Live(AutofocusLive(run: run));
      await tester.pumpWidget(ProviderScope(
        overrides: [
          focuserProvider.overrideWith(() => _Focuser(focuser)),
          autofocusLiveProvider.overrideWith(() => l),
          mainFocusMethodProvider.overrideWith(() => _Pick(pick)),
        ],
        child: const MaterialApp(home: Scaffold(body: Center(child: SimpleFocusShutter()))),
      ));
      await tester.pump();
      return l;
    }

    testWidgets('Auto: the shutter starts autofocus', (tester) async {
      final l = await shutter(tester, MainFocusMethod.autofocus, focuser: _focuser());
      expect(find.bySemanticsLabel('Autofocus'), findsOneWidget);
      // Only Preview's shutter is a camera button; a routine starts with play.
      expect(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byIcon(Icons.play_arrow)),
          findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      expect(l.starts, 1);
    });

    testWidgets('Auto while it runs: the red square cancels it', (tester) async {
      final l = await shutter(tester, MainFocusMethod.autofocus,
          focuser: _focuser(), run: const AutofocusRun(state: AutofocusRunStates.running));
      expect(find.bySemanticsLabel('Cancel autofocus'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      expect(l.cancels, 1);
    });

    testWidgets('Auto or Manual with the main camera busy: off, and a tap says why (user, 2026-11-04)', (tester) async {
      for (final pick in [MainFocusMethod.autofocus, MainFocusMethod.bahtinov]) {
        final l = await shutter(tester, pick, focuser: _focuser());
        final c = ProviderScope.containerOf(tester.element(find.byType(SimpleFocusShutter)));
        c.read(captureProgressProvider.notifier).beginExposing(const Duration(seconds: 30));
        await tester.pump();
        await tester.tap(find.byKey(const Key('simple-shutter')));
        await tester.pump();
        expect(l.starts, 0, reason: '$pick');
        expect(c.read(simpleMessagesProvider).single.title, 'The main camera is busy: wait for its frame, or stop it.',
            reason: '$pick');
        c.read(captureProgressProvider.notifier).reset();
        await tester.pump(const Duration(seconds: 11));
      }
    });

    testWidgets('Auto with no focuser: the shutter is off, and a tap says why (user, 2026-11-04)', (tester) async {
      final l = await shutter(tester, MainFocusMethod.autofocus);
      final c = ProviderScope.containerOf(tester.element(find.byType(SimpleFocusShutter)));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(l.starts, 0);
      expect(c.read(simpleMessagesProvider).single.title, 'Connect a focuser to autofocus, or focus by hand on Manual.');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(c.read(simpleMessagesProvider), hasLength(1), reason: 'once, not a pill per tap');
      expect(tester.getSemantics(find.byKey(const Key('simple-shutter'))),
          isSemantics(hint: 'Connect a focuser to autofocus, or focus by hand on Manual.'));
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('Manual: the shutter starts the Bahtinov readout (no longer Preview\'s frames)', (tester) async {
      await shutter(tester, MainFocusMethod.bahtinov, focuser: _focuser());
      expect(find.bySemanticsLabel('Start the Bahtinov readout'), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byIcon(Icons.play_arrow)),
          findsOneWidget, reason: 'Focus starts with play in Manual too');
    });
  });

  group('the rail chip reads the focus method', () {
    Future<void> chip(WidgetTester tester, FocuserStatus? focuser, {MainFocusMethod? pick}) async {
      await tester.pumpWidget(ProviderScope(
        overrides: [
          focuserProvider.overrideWith(() => _Focuser(focuser)),
          if (pick != null) mainFocusMethodProvider.overrideWith(() => _Pick(pick)),
        ],
        child: MaterialApp(home: Consumer(builder: (context, ref, _) => Text(simpleFocusSummary(ref)))),
      ));
      await tester.pump();
    }

    testWidgets('Auto with a focuser to drive', (tester) async {
      await chip(tester, _focuser());
      expect(find.text('Auto'), findsOneWidget);
    });
    testWidgets('Manual without one', (tester) async {
      await chip(tester, null);
      expect(find.text('Manual'), findsOneWidget);
    });
    testWidgets('Manual when the Bahtinov mask was picked', (tester) async {
      await chip(tester, _focuser(), pick: MainFocusMethod.bahtinov);
      expect(find.text('Manual'), findsOneWidget);
    });
  });
}
