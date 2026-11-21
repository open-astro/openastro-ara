import 'dart:typed_data';

import 'package:fl_chart/fl_chart.dart' show LineChart;
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/filter_offset_calibration.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/focus/filter_offset_calibration_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_preflight.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_preflight_state.dart'
    show nightFocusStateProvider, nightPolarAlignStateProvider;
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/screens/simple/simple_shell.dart'
    show SimpleSegment;
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/widgets/focus/v_curve_chart.dart';

import '../../support/fake_section_rig.dart';
import '../../support/imaging_sections.dart';
import '../../support/phd2_section.dart';

FocuserStatus _focuser({
  int? position = 1000,
  bool absolute = true,
  int max = 1100,
  String runtime = 'idle',
}) => FocuserStatus(
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

AutofocusProbe _probe(int i, double hfr, {bool kept = true}) => AutofocusProbe(
  index: i,
  phase: AutofocusProbePhases.fine,
  position: 900 + i * 50,
  hfr: hfr,
  stars: 40,
  kept: kept,
);

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

/// A Bahtinov readout found running: another client started it, or a reconnect.
class _Reading extends BahtinovFocusNotifier {
  @override
  BahtinovFocusLive build() => const BahtinovFocusLive(
    status: BahtinovFocusStatus(
      active: true,
      state: BahtinovFocusStates.running,
      exposureSec: 2,
      seq: 0,
      recent: [],
      zonePx: 0.3,
      zoneFromOptics: false,
      hasFrame: false,
    ),
  );
  @override
  Future<void> refresh() async {}
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

/// Tall enough for Autofocus and its Tools rows: the shell scrolls the panel, this harness does not.
void _tall(WidgetTester tester) {
  if (tester.view.physicalSize != const Size(2400, 1800)) return;
  tester.view.physicalSize = const Size(800, 1000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
}

/// The profile's autofocus section the last pump loaded (no HTTP).
late FakeAutofocusSection _af;

Future<(_Focuser, _Live)> _pump(
  WidgetTester tester, {
  FocuserStatus? focuser,
  AutofocusRun run = AutofocusRun.idle,
  Uint8List? frame,
  List<Override> extra = const [],
  bool afLoaded = true,
}) async {
  final f = _Focuser(focuser);
  final l = _Live(AutofocusLive(run: run, frame: frame));
  final af = _af = FakeAutofocusSection(
    initial: afLoaded ? const AutofocusSettings() : null,
  );
  _c = ProviderContainer(
    overrides: [
      profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
      autofocusSettingsProvider.overrideWith(() => af),
      // The guider settings as the rig's copy (ADR 0015), with no HTTP.
      phd2FromRig(),
      focuserProvider.overrideWith(() => f),
      autofocusLiveProvider.overrideWith(() => l),
      ...extra,
    ],
  );
  addTearDown(_c.dispose);
  _tall(tester);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: _c,
      child: const MaterialApp(
        home: Scaffold(body: Center(child: SimpleFocusPanel())),
      ),
    ),
  );
  await tester.pump();
  // The profile's autofocus section loads itself (SelfLoadingSection).
  await tester.pump();
  return (f, l);
}

void main() {
  group('simpleFocusHfr', () {
    test(
      'nothing before a run',
      () => expect(simpleFocusHfr(AutofocusRun.idle), isNull),
    );
    test('the latest kept probe while running', () {
      final run = AutofocusRun(
        state: AutofocusRunStates.running,
        probes: [_probe(0, 3.1), _probe(1, 2.4), _probe(2, 9, kept: false)],
      );
      expect(simpleFocusHfr(run), 2.4);
    });
    test('the confirmed HFR once complete', () {
      final run = AutofocusRun(
        state: AutofocusRunStates.complete,
        finalHfr: 1.98,
        probes: [_probe(0, 2.4)],
      );
      expect(simpleFocusHfr(run), 1.98);
    });
  });

  testWidgets(
    'Main / Guide on top, the Tools rows under Autofocus; the guide camera has no Tools',
    (tester) async {
      await _pump(tester, focuser: _focuser());
      final main = tester.getRect(
        find.byKey(const Key('simple-focus-scope-main')),
      );
      final guide = tester.getRect(
        find.byKey(const Key('simple-focus-scope-guide')),
      );
      final manual = tester.getRect(
        find.byKey(const Key('simple-focus-tool-manual')),
      );
      expect(guide.top, main.top, reason: 'one row');
      expect(
        manual.top,
        greaterThan(main.bottom),
        reason: 'Tools under the camera row',
      );
      expect(
        find.byKey(const Key('simple-focus-tool-offsets')),
        findsOneWidget,
      );
      expect(
        find.byKey(const Key('simple-info-on')),
        findsNothing,
        reason: 'Focus has no Info toggle',
      );
      _c.read(simpleFocusScopeProvider.notifier).set(SimpleFocusScope.guide);
      await tester.pump();
      expect(find.byKey(const Key('simple-focus-tool-manual')), findsNothing);
      expect(find.byKey(const Key('simple-focus-tool-offsets')), findsNothing);
    },
  );

  testWidgets(
    'the Tools rows: a heading, then each tool with its status pill and button, no one-line description',
    (tester) async {
      await _pump(tester, focuser: _focuser());
      expect(find.text('Tools'), findsOneWidget);
      expect(
        find.descendant(
          of: find.byKey(const Key('simple-focus-tool-manual-status')),
          matching: find.text('Not started'),
        ),
        findsOneWidget,
      );
      // No filter wheel: nothing was measured, so not green "All measured".
      final offsets = find.byKey(const Key('simple-focus-tool-offsets-status'));
      expect(
        find.descendant(of: offsets, matching: find.text('All measured')),
        findsNothing,
      );
      expect(
        find.descendant(of: offsets, matching: find.text('No filter wheel')),
        findsOneWidget,
      );
      // Board ruling (#1875 slice 4): no room for the descriptions on an iPad.
      expect(
        find.text('Bahtinov mask readout while you turn the focuser.'),
        findsNothing,
      );
      expect(find.textContaining('Focus shift per filter'), findsNothing);
    },
  );

  testWidgets('a screen reader hears each Tools row with its status', (
    tester,
  ) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, focuser: _focuser());
    expect(find.bySemanticsLabel('Focus by hand. Not started'), findsOneWidget);
    expect(
      find.bySemanticsLabel('Filter offsets. No filter wheel'),
      findsOneWidget,
    );
    semantics.dispose();
  });

  testWidgets('no focuser: Manual, the Bahtinov helper; no Autofocus button', (
    tester,
  ) async {
    await _pump(tester);
    expect(find.text('Fit the mask, then Start'), findsOneWidget);
    expect(find.text('Autofocus'), findsNothing);
  });

  testWidgets(
    'a readout already running shows Manual over a connected focuser, as the full UI does',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            focuserProvider.overrideWith(() => _Focuser(_focuser())),
            bahtinovFocusProvider.overrideWith(_Reading.new),
          ],
          child: const MaterialApp(
            home: Scaffold(body: Center(child: SimpleFocusPanel())),
          ),
        ),
      );
      await tester.pump();
      expect(
        find.byKey(const Key('simple-bahtinov-finish')),
        findsOneWidget,
        reason: 'the Bahtinov panel',
      );
      expect(find.text('Autofocus'), findsNothing);
    },
  );

  testWidgets(
    'a connected focuser: the HFR, no position or nudges (the Focuser window\'s dial moves it)',
    (tester) async {
      await _pump(tester, focuser: _focuser());
      expect(find.text('HFR —'), findsOneWidget);
      expect(find.textContaining('Position'), findsNothing);
      for (final icon in [
        Icons.chevron_left,
        Icons.chevron_right,
        Icons.keyboard_double_arrow_left,
      ]) {
        expect(find.byIcon(icon), findsNothing);
      }
    },
  );

  testWidgets(
    'while a run goes: the curve, no Cancel (the shutter\'s square)',
    (tester) async {
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
    },
  );

  testWidgets(
    'the V-curve is tall enough to read (at 80 pt its axes left the plot 18 pt)',
    (tester) async {
      final run = AutofocusRun(
        state: AutofocusRunStates.running,
        phase: 'sweep',
        totalSteps: 7,
        completedSteps: 2,
        stepSize: 80,
        probes: [_probe(0, 3.1), _probe(1, 2.4)],
      );
      await _pump(tester, focuser: _focuser(), run: run);
      expect(
        tester.getSize(find.byType(VCurveChart)).height,
        greaterThanOrEqualTo(86),
      );
    },
  );

  // A finished Smart Focus whose fit was weak (R² 0.55), like the rig's.
  final weak = AutofocusRun(
    state: AutofocusRunStates.complete,
    finalHfr: 1.01,
    finalPosition: 29463,
    finalStars: 299,
    probes: [
      for (var i = 0; i < 7; i++)
        _probe(i, [4.2, 3.4, 2.8, 1.4, 2.6, 3.3, 4.8][i]),
    ],
    fit: const AutofocusFit(
      algorithm: 'hyperbolic',
      rSquared: 0.554,
      bestPosition: 29463,
      predictedHfr: 1.7,
      withinSampledRange: true,
      curve: [],
    ),
  );

  testWidgets(
    'every run state keeps the Tools heading, pills and buttons, without the one-line descriptions',
    (tester) async {
      // Measured 2026-11-16 at 1024x768: with the descriptions an iPad scrolled
      // by 133 px idle, 146 px running and 41 px finished; the board ruled they
      // go in every state.
      final (_, live) = await _pump(
        tester,
        focuser: _focuser(),
        run: const AutofocusRun(),
      );
      for (final (name, run) in [
        (
          'running',
          AutofocusRun(
            state: AutofocusRunStates.running,
            phase: 'sweep',
            totalSteps: 7,
            stepSize: 80,
          ),
        ),
        ('finished', weak),
        (
          'failed',
          AutofocusRun(
            state: AutofocusRunStates.failed,
            totalSteps: 7,
            stepSize: 80,
          ),
        ),
      ]) {
        live.state = AutofocusLive(run: run);
        await tester.pump();
        expect(find.text('Tools'), findsOneWidget, reason: name);
        expect(
          find.byKey(const Key('simple-focus-tool-manual-status')),
          findsOneWidget,
          reason: name,
        );
        expect(
          find.byKey(const Key('simple-focus-tool-offsets-status')),
          findsOneWidget,
          reason: name,
        );
        expect(
          find.byKey(const Key('simple-focus-tool-manual')),
          findsOneWidget,
          reason: name,
        );
        expect(
          find.byKey(const Key('simple-focus-tool-offsets')),
          findsOneWidget,
          reason: name,
        );
        expect(
          find.text('Bahtinov mask readout while you turn the focuser.'),
          findsNothing,
          reason: name,
        );
        expect(
          find.textContaining('Focus shift per filter'),
          findsNothing,
          reason: name,
        );
      }
    },
  );

  testWidgets('a finished run: best position, stars and fit R², all on view', (
    tester,
  ) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    for (final t in [
      'Best position',
      '29463',
      'Stars',
      '299',
      'Fit R²',
      '0.55',
    ]) {
      expect(find.text(t), findsOneWidget, reason: t);
    }
    expect(
      find.textContaining('Details'),
      findsNothing,
      reason: 'nothing folded away',
    );
  });

  testWidgets(
    'Auto keeps one height from start to the last probe; a finished run gives chart height to the Tools rows (#1717)',
    (tester) async {
      final (_, live) = await _pump(
        tester,
        focuser: _focuser(),
        run: const AutofocusRun(),
      );
      double height() => tester.getSize(find.byType(SimpleFocusPanel)).height;
      final start = height();
      expect(
        tester.getSize(find.byType(VCurveChart)).height,
        86,
        reason: 'idle chart',
      );
      for (final run in [
        AutofocusRun(
          state: AutofocusRunStates.running,
          phase: 'sweep',
          totalSteps: 7,
          stepSize: 80,
        ),
        AutofocusRun(
          state: AutofocusRunStates.running,
          phase: 'sweep',
          totalSteps: 7,
          completedSteps: 2,
          stepSize: 80,
          probes: [_probe(0, 3.1), _probe(1, 2.4)],
        ),
      ]) {
        live.state = AutofocusLive(run: run);
        await tester.pump();
        expect(
          height(),
          start,
          reason: '${run.state} ${run.probes.length} probes',
        );
      }
      // Board ruling (#1875 slice 4): a finished run's chart and note are
      // smaller still, so an iPad needs no scroll.
      live.state = AutofocusLive(run: weak);
      await tester.pump();
      expect(height(), lessThan(start), reason: 'finished');
    },
  );

  // #1898: the plot resized between steps (axes appearing, the banner and the error taking its room).
  testWidgets(
    'the HFR plot keeps one size and place from before the first probe to the end; a finished run and its error share a smaller one (#1898)',
    (tester) async {
      final (_, live) = await _pump(
        tester,
        focuser: _focuser(),
        run: const AutofocusRun(state: AutofocusRunStates.running),
      );
      Rect plot() => tester
          .getRect(find.byType(LineChart))
          .shift(-tester.getTopLeft(find.byType(SimpleFocusPanel)));
      final start = plot();
      expect(
        tester.getSize(find.byType(VCurveChart)).height,
        86,
        reason: 'running chart',
      );
      live.state = AutofocusLive(
        run: AutofocusRun(
          state: AutofocusRunStates.running,
          phase: 'sweep',
          totalSteps: 7,
          completedSteps: 2,
          stepSize: 80,
          probes: [_probe(0, 3.1), _probe(1, 2.4)],
        ),
      );
      await tester.pump();
      expect(plot(), start, reason: 'probes');
      // Finished: the chart is shorter (room for the Tools rows) and stays put
      // when the error takes its slot.
      live.state = AutofocusLive(run: weak);
      await tester.pump();
      final done = plot();
      expect(done.height, lessThan(start.height), reason: 'weak fit');
      live.state = AutofocusLive(
        run: weak,
        error: StateError(List.filled(60, 'the star field drifted').join(' ')),
      );
      await tester.pump();
      expect(plot(), done, reason: 'error');
    },
  );

  // #1898 review: the warning's slot scales with the text; at 2x the one-line warning still fits.
  testWidgets(
    'at 2x text the weak-fit warning fits its slot under the chart (#1898)',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      // Only overflow down counts here: the panel's rows overflow sideways at 2x on master too.
      final down = <String>[];
      final previous = FlutterError.onError;
      FlutterError.onError = (d) {
        if (d.exceptionAsString().contains('on the bottom'))
          down.add(d.exceptionAsString());
      };
      await _pump(tester, focuser: _focuser(), run: weak);
      FlutterError.onError = previous;
      expect(down, isEmpty);
      expect(find.byKey(const Key('simple-focus-weak-fit')), findsOneWidget);
    },
  );

  testWidgets(
    'Measured stars, hidden for its room at large text before a frame, is not read out',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1600);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      tester.platformDispatcher.textScaleFactorTestValue = 1.3;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      final handle = tester.ensureSemantics();
      await _pump(tester, focuser: _focuser());
      expect(find.bySemanticsLabel('Measured stars'), findsNothing);
      handle.dispose();
    },
  );

  testWidgets('the V-curve chart speaks pt-BR, empty and with probes', (
    tester,
  ) async {
    const locale = Locale('pt', 'BR');
    final pt = lookupAppLocalizations(locale);
    Future<void> show(AutofocusRun run) => tester.pumpWidget(
      MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: SizedBox(width: 400, height: 200, child: VCurveChart(run: run)),
        ),
      ),
    );
    await show(AutofocusRun.idle);
    await tester.pump();
    expect(find.text(pt.focusChartEmpty), findsOneWidget);
    await show(
      AutofocusRun(
        state: AutofocusRunStates.running,
        phase: 'sweep',
        probes: [_probe(0, 3.1), _probe(1, 2.4)],
      ),
    );
    await tester.pump();
    expect(find.text(pt.focusChartPosition), findsOneWidget);
  });

  group('a long focus error', () {
    final long = List.filled(60, 'the star field drifted').join(' ');

    testWidgets('is never cut, and the panel keeps its height', (tester) async {
      final (_, live) = await _pump(tester, focuser: _focuser());
      final start = tester.getSize(find.byType(SimpleFocusPanel)).height;
      live.state = AutofocusLive(
        run: AutofocusRun.idle,
        error: StateError(long),
      );
      await tester.pump();
      expect(
        tester.renderObject<RenderParagraph>(find.text(long)).didExceedMaxLines,
        isFalse,
      );
      expect(tester.takeException(), isNull);
      expect(tester.getSize(find.byType(SimpleFocusPanel)).height, start);
      expect(find.byType(VCurveChart), findsOneWidget);
    });

    testWidgets(
      'over a weak fit takes the warning\'s room, so nothing overflows',
      (tester) async {
        final (_, live) = await _pump(tester, focuser: _focuser());
        live.state = AutofocusLive(run: weak, error: StateError(long));
        await tester.pump();
        expect(tester.takeException(), isNull);
        expect(find.byKey(const Key('simple-focus-weak-fit')), findsNothing);
      },
    );
  });

  testWidgets('a weak fit says so in the panel, in words', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    expect(find.textContaining('Weak curve fit (R² 0.55)'), findsOneWidget);
  });

  testWidgets(
    'the weak-fit warning is icon + colour + text, not a tint alone (night mode drops tints)',
    (tester) async {
      // DESIGN.md: pair every warning with icon + colour + text (impeccable audit, P2).
      await _pump(tester, focuser: _focuser(), run: weak);
      final box = find.byKey(const Key('simple-focus-weak-fit'));
      expect(
        find.descendant(
          of: box,
          matching: find.byIcon(Icons.warning_amber_rounded),
        ),
        findsOneWidget,
      );
      final icon = tester.widget<Icon>(
        find.descendant(
          of: box,
          matching: find.byIcon(Icons.warning_amber_rounded),
        ),
      );
      expect(icon.color, AraColors.accentWarning);
      final text = tester.widget<Text>(
        find.descendant(
          of: box,
          matching: find.textContaining('Weak curve fit'),
        ),
      );
      expect(text.style?.color, AraColors.accentWarning);
    },
  );

  testWidgets(
    'the focuser line: where it is and which (before a result; then Best position says it)',
    (tester) async {
      await _pump(tester, focuser: _focuser());
      expect(find.text('Focuser at 1000 · Focuser'), findsOneWidget);
    },
  );

  testWidgets(
    'the measured stars on the frame: off to start, a tap shows them',
    (tester) async {
      await _pump(
        tester,
        focuser: _focuser(),
        run: weak,
        frame: Uint8List.fromList([1, 2, 3]),
      );
      final off = find.byKey(const Key('simple-focus-stars-off'));
      expect(tester.widget<SimpleSegment>(off).selected, isTrue);
      await tester.tap(find.byKey(const Key('simple-focus-stars-on')));
      await tester.pump();
      expect(_c.read(simpleFocusStarsShownProvider), isTrue);
    },
  );

  testWidgets('no frame from the run: no stars switch', (tester) async {
    await _pump(tester, focuser: _focuser(), run: weak);
    expect(find.byKey(const Key('simple-focus-stars-on')), findsNothing);
  });

  testWidgets(
    'Focus by hand opens in place and Back returns to Autofocus (the chip follows)',
    (tester) async {
      await _pump(tester, focuser: _focuser());
      expect(
        find.byKey(const Key('simple-focus-back')),
        findsNothing,
        reason: 'a connected focuser: Autofocus to start',
      );
      await tester.tap(find.byKey(const Key('simple-focus-tool-manual')));
      await tester.pump();
      expect(_c.read(mainFocusMethodProvider), MainFocusMethod.bahtinov);
      expect(find.byKey(const Key('simple-focus-tool-manual')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-focus-back')));
      await tester.pump();
      expect(_c.read(mainFocusMethodProvider), MainFocusMethod.autofocus);
      expect(find.byKey(const Key('simple-focus-tool-manual')), findsOneWidget);
    },
  );

  testWidgets('the Tools rows are locked while autofocus runs', (tester) async {
    await _pump(
      tester,
      focuser: _focuser(),
      run: AutofocusRun(
        state: AutofocusRunStates.running,
        probes: [_probe(0, 3.1)],
      ),
    );
    expect(
      tester
          .widget<OutlinedButton>(
            find.byKey(const Key('simple-focus-tool-manual')),
          )
          .onPressed,
      isNull,
    );
    expect(
      tester
          .widget<OutlinedButton>(
            find.byKey(const Key('simple-focus-tool-offsets')),
          )
          .onPressed,
      isNull,
    );
  });

  group('Autofocus exposure in Simple Mode', () {
    Future<void> pumpWith(WidgetTester tester, {int? seconds}) async {
      await _pump(tester, focuser: _focuser());
      if (seconds != null)
        _c.read(autofocusSettingsProvider.notifier).setExposureSeconds(seconds);
      await tester.pump();
    }

    bool selected(WidgetTester tester, String key) => tester
        .widget<SimpleSegment>(find.byKey(Key('simple-focus-af-exposure-$key')))
        .selected;

    testWidgets(
      '2 / 5 / 10 / 15 s, 5 s to start; Steps and Step size are not offered',
      (tester) async {
        await pumpWith(tester);
        for (final s in ['2', '5', '10', '15']) {
          expect(
            find.byKey(Key('simple-focus-af-exposure-$s')),
            findsOneWidget,
          );
        }
        expect(selected(tester, '5'), isTrue);
        expect(find.textContaining('Step'), findsNothing);
      },
    );

    testWidgets('a pick writes the profile setting Advanced Mode edits', (
      tester,
    ) async {
      await pumpWith(tester);
      await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
      await tester.pump();
      expect(_c.read(autofocusSettingsProvider).value!.exposureSeconds, 10);
      expect(selected(tester, '10'), isTrue);
    });

    testWidgets(
      "a pick is saved to the profile, where the daemon reads the next run's exposure",
      (tester) async {
        await _pump(tester, focuser: _focuser());
        await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
        await tester.pump();
        expect(_af.puts.map((v) => v.exposureSeconds), [10]);
      },
    );

    testWidgets("a failed save says so in a pill", (tester) async {
      await _pump(
        tester,
        focuser: _focuser(),
        extra: [simpleModeActiveProvider.overrideWithValue(true)],
      );
      _af.refuse = StateError('daemon offline');
      await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
      await tester.pump();
      final pill = _c.read(simpleMessagesProvider).single;
      expect(
        (pill.kind, pill.title, pill.origin),
        (
          SimpleMessageKind.error,
          "Couldn't save that",
          SimpleStatusOrigin.focus,
        ),
      );
    });

    testWidgets(
      'a profile value outside the four shows as its own chip and is not rewritten',
      (tester) async {
        await pumpWith(tester, seconds: 3);
        expect(selected(tester, '3'), isTrue);
        expect(find.text('3 s'), findsOneWidget);
        expect(_c.read(autofocusSettingsProvider).value!.exposureSeconds, 3);
        await tester.tap(find.byKey(const Key('simple-focus-af-exposure-15')));
        await tester.pump();
        expect(
          find.byKey(const Key('simple-focus-af-exposure-3')),
          findsNothing,
        );
        expect(_c.read(autofocusSettingsProvider).value!.exposureSeconds, 15);
      },
    );

    testWidgets(
      'before the profile is in from the rig no chip is picked, and a pick sends nothing (ADR 0015)',
      (tester) async {
        await _pump(
          tester,
          focuser: _focuser(),
          afLoaded: false,
          extra: [simpleModeActiveProvider.overrideWithValue(true)],
        );
        for (final s in ['2', '5', '10', '15']) {
          expect(
            selected(tester, s),
            isFalse,
            reason: 'no client default shows as the profile\'s',
          );
        }
        await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
        await tester.pump();
        expect(_af.puts, isEmpty);
        final pill = _c.read(simpleMessagesProvider).single;
        expect(pill.title, "Couldn't save that");
        expect(pill.detail, contains('not loaded from the rig'));
      },
    );

    testWidgets(
      'after a failed load a pick loads once more, then saves (ADR 0015)',
      (tester) async {
        await _pump(tester, focuser: _focuser(), afLoaded: false);
        _af.initial = const AutofocusSettings();
        await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
        await tester.pump();
        expect(_af.puts.map((v) => v.exposureSeconds), [10]);
        expect(selected(tester, '10'), isTrue);
      },
    );

    testWidgets(
      'a pick refused as stale shows the rig\'s newer copy (ADR 0015)',
      (tester) async {
        await _pump(
          tester,
          focuser: _focuser(),
          extra: [simpleModeActiveProvider.overrideWithValue(true)],
        );
        _af
          ..refuse = staleSave()
          ..initial = const AutofocusSettings().copyWith(exposureSeconds: 15);
        await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
        await tester.pump();
        expect(_af.puts, isEmpty);
        expect(
          selected(tester, '15'),
          isTrue,
          reason: 'the copy another device saved',
        );
        expect(_c.read(autofocusSettingsProvider).conflict, isFalse);
        expect(
          _c.read(simpleMessagesProvider).single.title,
          "Couldn't save that",
        );
      },
    );

    testWidgets('locked while autofocus runs', (tester) async {
      await _pump(
        tester,
        focuser: _focuser(),
        run: AutofocusRun(
          state: AutofocusRunStates.running,
          probes: [_probe(0, 3.1)],
        ),
      );
      expect(
        tester
            .widget<SimpleSegment>(
              find.byKey(const Key('simple-focus-af-exposure-10')),
            )
            .onTap,
        isNull,
      );
    });
  });

  testWidgets(
    "Preflight on a permanent pier: focus is the last step, so its Done starts imaging",
    (tester) async {
      final f = _Focuser(_focuser());
      final pilot = _Pilot();
      _c = ProviderContainer(
        overrides: [
          focuserProvider.overrideWith(() => f),
          autofocusLiveProvider.overrideWith(
            () => _Live(AutofocusLive(run: weak)),
          ),
          nightFocusStateProvider.overrideWithValue(SetupStepState.done),
          nightPolarAlignStateProvider.overrideWithValue(
            SetupStepState.pending,
          ),
          simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
          simplePreflightProvider.overrideWith(() => pilot),
        ],
      );
      addTearDown(_c.dispose);
      await _c.read(simplePermanentPierProvider.future);
      _tall(tester);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: _c,
          child: const MaterialApp(
            home: Scaffold(body: Center(child: SimpleFocusPanel())),
          ),
        ),
      );
      await tester.pump();
      await tester.tap(find.text('Done · Start imaging'));
      expect(pilot.nexts, 1);
    },
  );

  for (final method in MainFocusMethod.values) {
    testWidgets(
      'Preflight with focus last: ${method.name} keeps one height when focus finishes',
      (tester) async {
        Future<double> height(SetupStepState focus) async {
          final c = ProviderContainer(
            overrides: [
              focuserProvider.overrideWith(() => _Focuser(_focuser())),
              mainFocusMethodProvider.overrideWith(() => _Pick(method)),
              nightFocusStateProvider.overrideWithValue(focus),
              nightPolarAlignStateProvider.overrideWithValue(
                SetupStepState.pending,
              ),
              simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
              simplePreflightProvider.overrideWith(_Pilot.new),
            ],
          );
          addTearDown(c.dispose);
          await c.read(simplePermanentPierProvider.future);
          // Loaded first: a derived provider refreshed after the next pumpWidget
          // drops this container's tree leaves Riverpod's refresh timer pending.
          await c.read(focuserProvider.future);
          _tall(tester);
          await tester.pumpWidget(
            UncontrolledProviderScope(
              container: c,
              child: const MaterialApp(
                home: Scaffold(body: Center(child: SimpleFocusPanel())),
              ),
            ),
          );
          await tester.pump();
          return tester.getSize(find.byType(SimpleFocusPanel)).height;
        }

        final running = await height(SetupStepState.pending);
        expect(await height(SetupStepState.done), running);
      },
    );
  }

  testWidgets(
    'Preflight on a permanent pier, focused by hand: Manual has the Done too',
    (tester) async {
      // #1486 review: the Done only showed in Auto, so a Bahtinov focus could only Skip.
      final pilot = _Pilot();
      _c = ProviderContainer(
        overrides: [
          focuserProvider.overrideWith(() => _Focuser(null)),
          mainFocusMethodProvider.overrideWith(
            () => _Pick(MainFocusMethod.bahtinov),
          ),
          nightFocusStateProvider.overrideWithValue(SetupStepState.done),
          nightPolarAlignStateProvider.overrideWithValue(
            SetupStepState.pending,
          ),
          simplePermanentPierPrefsProvider.overrideWithValue(_PierPrefs()),
          simplePreflightProvider.overrideWith(() => pilot),
        ],
      );
      addTearDown(_c.dispose);
      await _c.read(simplePermanentPierProvider.future);
      _tall(tester);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: _c,
          child: const MaterialApp(
            home: Scaffold(body: Center(child: SimpleFocusPanel())),
          ),
        ),
      );
      await tester.pump();
      expect(
        _c.read(mainFocusMethodProvider),
        MainFocusMethod.bahtinov,
        reason: 'precondition: Manual',
      );
      await tester.tap(find.text('Done · Start imaging'));
      expect(pilot.nexts, 1);
    },
  );

  group('the shutter follows the chip', () {
    Future<_Live> shutter(
      WidgetTester tester,
      MainFocusMethod pick, {
      FocuserStatus? focuser,
      AutofocusRun run = AutofocusRun.idle,
    }) async {
      final l = _Live(AutofocusLive(run: run));
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            focuserProvider.overrideWith(() => _Focuser(focuser)),
            autofocusLiveProvider.overrideWith(() => l),
            mainFocusMethodProvider.overrideWith(() => _Pick(pick)),
          ],
          child: const MaterialApp(
            home: Scaffold(body: Center(child: SimpleFocusShutter())),
          ),
        ),
      );
      await tester.pump();
      return l;
    }

    testWidgets('Auto: the shutter starts autofocus', (tester) async {
      final l = await shutter(
        tester,
        MainFocusMethod.autofocus,
        focuser: _focuser(),
      );
      expect(find.bySemanticsLabel('Autofocus'), findsOneWidget);
      // Only Preview's shutter is a camera button; a routine starts with play.
      expect(
        find.descendant(
          of: find.byKey(const Key('simple-shutter')),
          matching: find.byIcon(Icons.play_arrow),
        ),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('simple-shutter')));
      expect(l.starts, 1);
    });

    testWidgets('Auto while it runs: the red square cancels it', (
      tester,
    ) async {
      final l = await shutter(
        tester,
        MainFocusMethod.autofocus,
        focuser: _focuser(),
        run: const AutofocusRun(state: AutofocusRunStates.running),
      );
      expect(find.bySemanticsLabel('Cancel autofocus'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      expect(l.cancels, 1);
    });

    testWidgets(
      'Auto or Manual with the main camera busy: off, and a tap says why (user, 2026-11-04)',
      (tester) async {
        for (final pick in [
          MainFocusMethod.autofocus,
          MainFocusMethod.bahtinov,
        ]) {
          final l = await shutter(tester, pick, focuser: _focuser());
          final c = ProviderScope.containerOf(
            tester.element(find.byType(SimpleFocusShutter)),
          );
          c
              .read(captureProgressProvider.notifier)
              .beginExposing(const Duration(seconds: 30));
          await tester.pump();
          await tester.tap(find.byKey(const Key('simple-shutter')));
          await tester.pump();
          expect(l.starts, 0, reason: '$pick');
          expect(
            c.read(simpleMessagesProvider).single.title,
            'The main camera is busy: wait for its frame, or stop it.',
            reason: '$pick',
          );
          c.read(captureProgressProvider.notifier).reset();
          await tester.pump(const Duration(seconds: 11));
        }
      },
    );

    testWidgets(
      'Auto with no focuser: the shutter is off, and a tap says why (user, 2026-11-04)',
      (tester) async {
        final l = await shutter(tester, MainFocusMethod.autofocus);
        final c = ProviderScope.containerOf(
          tester.element(find.byType(SimpleFocusShutter)),
        );
        await tester.tap(find.byKey(const Key('simple-shutter')));
        await tester.pump();
        expect(l.starts, 0);
        expect(
          c.read(simpleMessagesProvider).single.title,
          'Connect a focuser to autofocus, or focus by hand on Manual.',
        );
        await tester.tap(find.byKey(const Key('simple-shutter')));
        await tester.pump();
        expect(
          c.read(simpleMessagesProvider),
          hasLength(1),
          reason: 'once, not a pill per tap',
        );
        expect(
          tester.getSemantics(find.byKey(const Key('simple-shutter'))),
          isSemantics(
            hint: 'Connect a focuser to autofocus, or focus by hand on Manual.',
          ),
        );
        await tester.pump(const Duration(seconds: 11));
      },
    );

    testWidgets(
      'Manual: the shutter starts the Bahtinov readout (no longer Preview\'s frames)',
      (tester) async {
        await shutter(tester, MainFocusMethod.bahtinov, focuser: _focuser());
        expect(
          find.bySemanticsLabel('Start the Bahtinov readout'),
          findsOneWidget,
        );
        expect(
          find.descendant(
            of: find.byKey(const Key('simple-shutter')),
            matching: find.byIcon(Icons.play_arrow),
          ),
          findsOneWidget,
          reason: 'Focus starts with play in Manual too',
        );
      },
    );
  });

  group('the rail chip reads the focus method', () {
    Future<void> chip(
      WidgetTester tester,
      FocuserStatus? focuser, {
      MainFocusMethod? pick,
    }) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            focuserProvider.overrideWith(() => _Focuser(focuser)),
            if (pick != null)
              mainFocusMethodProvider.overrideWith(() => _Pick(pick)),
          ],
          child: MaterialApp(
            home: Consumer(
              builder: (context, ref, _) => Text(simpleFocusSummary(ref)),
            ),
          ),
        ),
      );
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

  group('Offsets (#1675)', () {
    final en = lookupAppLocalizations(const Locale('en'));
    Future<_Offsets> offsets(
      WidgetTester tester, {
      FilterOffsetCalibration? calibration,
      bool saved = false,
      bool camera = true,
      bool plan = false,
      bool manualWheel = false,
      bool measuredWheel = false,
      AutofocusRun run = AutofocusRun.idle,
      bool reading = false,
      Widget child = const SimpleFocusPanel(),
    }) async {
      final o = _Offsets(
        FilterOffsetLive(calibration: calibration, saved: saved),
      );
      _c = ProviderContainer(
        overrides: [
          focuserProvider.overrideWith(() => _Focuser(_focuser())),
          autofocusLiveProvider.overrideWith(
            () => _Live(AutofocusLive(run: run)),
          ),
          filterOffsetCalibrationProvider.overrideWith(() => o),
          filterWheelProvider.overrideWith(
            () => _Wheel(manual: manualWheel, measured: measuredWheel),
          ),
          if (camera) cameraStatusProvider.overrideWith(_Camera.new),
          if (plan) sequenceRunStateProvider.overrideWith(_PlanRunning.new),
          if (reading) bahtinovFocusProvider.overrideWith(_Reading.new),
        ],
      );
      addTearDown(_c.dispose);
      await _c.read(focuserProvider.future);
      await _c.read(filterWheelProvider.future);
      if (camera) await _c.read(cameraStatusProvider.future);
      _c.read(simpleFocusOffsetsProvider.notifier).set(true);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: _c,
          child: MaterialApp(
            home: Scaffold(
              body: Center(child: SingleChildScrollView(child: child)),
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
      return o;
    }

    testWidgets(
      'the Filter offsets pill counts the filters still at offset 0',
      (tester) async {
        await offsets(tester);
        _c.read(simpleFocusOffsetsProvider.notifier).set(false);
        await tester.pump();
        expect(
          find.descendant(
            of: find.byKey(const Key('simple-focus-tool-offsets-status')),
            matching: find.textContaining(' to measure'),
          ),
          findsOneWidget,
        );
      },
    );

    testWidgets(
      'every named filter measured: All measured, whatever a Dark or an unnamed slot holds',
      (tester) async {
        await offsets(tester, measuredWheel: true);
        _c.read(simpleFocusOffsetsProvider.notifier).set(false);
        await tester.pump();
        expect(
          find.descendant(
            of: find.byKey(const Key('simple-focus-tool-offsets-status')),
            matching: find.text('All measured'),
          ),
          findsOneWidget,
        );
      },
    );

    testWidgets('Offsets is locked while a plan runs, and says why', (
      tester,
    ) async {
      await offsets(tester, plan: true);
      expect(find.text(en.focusOffsetsPlanRunning), findsOneWidget);
    });

    testWidgets(
      'Offsets is locked while another focus routine runs, and says why',
      (tester) async {
        await offsets(tester, reading: true);
        expect(find.text(en.focusOffsetsFocusBusy), findsOneWidget);
      },
    );

    testWidgets('Offsets names the missing device', (tester) async {
      await offsets(tester, camera: false);
      expect(find.text(en.focusOffsetsNeedsCamera), findsOneWidget);
    });

    testWidgets('Offsets says it needs a motorised wheel', (tester) async {
      await offsets(tester, manualWheel: true);
      expect(find.text(en.focusOffsetsNeedsMotorisedWheel), findsOneWidget);
    });

    testWidgets(
      "the picker ticks every named filter but a dark; the reference is shown and can't be unticked",
      (tester) async {
        await offsets(tester);
        FilterChip chip(int p) => tester.widget<FilterChip>(
          find.byKey(Key('simple-offsets-pick-$p')),
        );
        expect(
          [for (var p = 0; p < 6; p++) chip(p).selected],
          [true, true, true, true, false, false],
          reason: 'L R G OIII ticked; Dark and the unnamed slot not',
        );
        expect(find.text(en.focusOffsetsReference('L')), findsOneWidget);
        expect(find.text('L · reference'), findsOneWidget);
        expect(
          chip(0).onSelected,
          isNull,
          reason: 'the reference is always measured',
        );
        expect(find.byKey(const Key('simple-offsets-blocker')), findsNothing);
        await tester.tap(find.byKey(const Key('simple-offsets-pick-4')));
        await tester.pump();
        expect(chip(4).selected, isTrue);
      },
    );

    test('the reference: the autofocus filter, then L or Lum, then the lowest pick', () {
      const slots = [
        FilterSlot(position: 0, name: 'Ha', focusOffset: 0),
        FilterSlot(position: 1, name: 'Lum', focusOffset: 0),
        FilterSlot(position: 2, name: 'oiii', focusOffset: 0),
      ];
      expect(offsetReference(slots, {0, 1, 2}, 'OIII'), 2);
      expect(offsetReference(slots, {0, 1, 2}, 'L'), 1);
      expect(
        offsetReference(slots.where((s) => s.position != 1).toList(), {
          2,
          0,
        }, 'L'),
        0,
      );
    });

    testWidgets(
      'the shutter starts the calibration with the picks, and its red square cancels it',
      (tester) async {
        final o = await offsets(tester, child: const SimpleFocusShutter());
        expect(find.bySemanticsLabel(en.focusOffsetsStart), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-shutter')));
        expect(o.starts, [
          [0, 1, 2, 3],
        ]);
        o.state = const FilterOffsetLive(calibration: _running);
        await tester.pump();
        expect(find.bySemanticsLabel(en.focusOffsetsCancel), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-shutter')));
        expect(o.cancels, 1);
      },
    );

    testWidgets(
      'while it runs: the filter being measured, which run of how many, and the live curve',
      (tester) async {
        await offsets(
          tester,
          calibration: _running,
          run: AutofocusRun(
            state: AutofocusRunStates.running,
            probes: [_probe(0, 3.1), _probe(1, 2.4)],
          ),
        );
        expect(
          find.text(en.focusOffsetsMeasuring('OIII', 4, 7)),
          findsOneWidget,
        );
        expect(find.byType(VCurveChart), findsOneWidget);
      },
    );

    testWidgets(
      'the results table: offsets, R², a weak fit and a failed row, and the drift warning',
      (tester) async {
        await offsets(tester, calibration: _done);
        final row = find.byKey(const Key('simple-offsets-row-3'));
        for (final t in ['OIII', '+42', '0.97', '+30']) {
          expect(
            find.descendant(of: row, matching: find.text(t)),
            findsOneWidget,
            reason: t,
          );
        }
        expect(
          find.descendant(
            of: find.byKey(const Key('simple-offsets-row-1')),
            matching: find.text('−12'),
          ),
          findsOneWidget,
          reason: 'a real minus',
        );
        expect(find.text(en.focusOffsetsWeakFit('0.62')), findsOneWidget);
        expect(
          find.descendant(
            of: find.byKey(const Key('simple-offsets-row-2')),
            matching: find.text('Failed'),
          ),
          findsOneWidget,
        );
        expect(find.text('no stars found'), findsOneWidget);
        expect(find.text(en.focusOffsetsDrift(30, 20)), findsOneWidget);
      },
    );

    testWidgets(
      'each result row is read out whole: filter, offset, fit and the one before',
      (tester) async {
        final handle = tester.ensureSemantics();
        await offsets(tester, calibration: _done);
        expect(
          tester.getSemantics(find.byKey(const Key('simple-offsets-row-3'))),
          matchesSemantics(label: 'OIII, Offset +42, Fit R² 0.97, Before +30'),
        );
        handle.dispose();
      },
    );

    testWidgets('Save and Discard call the notifier; after Save, the hint', (
      tester,
    ) async {
      final o = await offsets(tester, calibration: _done);
      await tester.tap(find.byKey(const Key('simple-offsets-save')));
      expect(o.saves, 1);
      await tester.tap(find.byKey(const Key('simple-offsets-discard')));
      expect(o.discards, 1);
      o.state = const FilterOffsetLive(saved: true);
      await tester.pump();
      expect(find.text(en.focusOffsetsSaved), findsOneWidget);
    });

    testWidgets(
      'a stopped run offers Discard only: the saved offsets stay as they were',
      (tester) async {
        await offsets(
          tester,
          calibration: FilterOffsetCalibration.fromJson({
            ..._doneJson,
            'state': 'cancelled',
          }),
        );
        expect(find.text(en.focusOffsetsStopped), findsOneWidget);
        expect(find.byKey(const Key('simple-offsets-save')), findsNothing);
        expect(find.byKey(const Key('simple-offsets-discard')), findsOneWidget);
      },
    );
  });
}

/// A calibration on its 4th autofocus run of 7, measuring OIII.
const _running = FilterOffsetCalibration(
  state: 'running',
  referencePosition: 0,
  referenceName: 'L',
  currentPosition: 3,
  runIndex: 4,
  runCount: 7,
  filters: [FilterOffsetRow(position: 3, name: 'OIII', status: 'pending')],
);

final _doneJson = <String, dynamic>{
  'state': 'complete',
  'job_id': 'j',
  'reference_position': 0,
  'reference_name': 'L',
  'reference_first_position': 1000,
  'reference_second_position': 1030,
  'cfz_steps': 20,
  'drift_checked': true,
  'drift_warning': true,
  'run_index': 5,
  'run_count': 5,
  'filters': [
    {
      'position': 0,
      'name': 'L',
      'status': 'measured',
      'best_position': 1015,
      'offset': 0,
      'r_squared': 0.98,
      'previous_offset': 0,
    },
    {
      'position': 1,
      'name': 'R',
      'status': 'measured',
      'best_position': 1003,
      'offset': -12,
      'r_squared': 0.62,
      'previous_offset': -10,
    },
    {
      'position': 2,
      'name': 'G',
      'status': 'failed',
      'previous_offset': 5,
      'failure_reason': 'no stars found',
    },
    {
      'position': 3,
      'name': 'OIII',
      'status': 'measured',
      'best_position': 1057,
      'offset': 42,
      'r_squared': 0.97,
      'previous_offset': 30,
    },
  ],
};
final _done = FilterOffsetCalibration.fromJson(_doneJson);

class _Offsets extends FilterOffsetCalibrationNotifier {
  _Offsets(this.initial);
  final FilterOffsetLive initial;
  final starts = <List<int>>[];
  int cancels = 0, saves = 0, discards = 0;
  @override
  FilterOffsetLive build() => initial;
  @override
  Future<void> refresh() async {}
  @override
  Future<void> start(List<int> positions) async => starts.add(positions);
  @override
  Future<void> cancel() async => cancels++;
  @override
  Future<void> save() async => saves++;
  @override
  Future<void> discard() async => discards++;
}

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async =>
      CameraStatus.fromJson({'name': 'ASI2600MM', 'state': 'connected'});
}

class _Wheel extends FilterWheelNotifier {
  _Wheel({this.manual = false, this.measured = false});
  final bool manual;

  /// R, G and OIII have offsets (L is the reference); Dark and the unnamed
  /// slot stay at 0, as the calibration never measures them.
  final bool measured;
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson({
    'device_id': 'w',
    'name': 'EFW',
    'state': 'connected',
    'manual': manual,
    'slots': [
      for (final (i, n) in ['L', 'R', 'G', 'OIII', 'Dark', ''].indexed)
        {
          'position': i,
          'name': n,
          'focus_offset': measured && i >= 1 && i <= 3 ? 40 * i : 0,
        },
    ],
    'runtime': {'state': 'idle', 'current_slot': 0},
  });
  @override
  Future<void> refresh([client]) async {}
}

class _PlanRunning extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
    sequenceId: 'seq-1',
    state: SequenceRunState.running,
  );
}
