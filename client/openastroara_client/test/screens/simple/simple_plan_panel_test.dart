import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:dio/dio.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/stop_mount_overlay.dart' show SlewWsEvents;
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/state/library/live_library_state.dart';

const _m31 = PlanetariumSelection(
    name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27, altDeg: 62.4, maxAltDeg: 74.2);

const _lrgb = [
  PlanningFilter(name: 'L', kind: FilterKind.l),
  PlanningFilter(name: 'R', kind: FilterKind.r),
  PlanningFilter(name: 'G', kind: FilterKind.g),
  PlanningFilter(name: 'B', kind: FilterKind.b),
  PlanningFilter(name: 'Ha', kind: FilterKind.ha),
];

/// The daemon as the Run mode sees it: create, start and the lifecycle calls
/// are recorded; the run state is whatever the test sets.
class _Seq implements SequenceClient {
  final calls = <String>[];
  SequenceRunStateInfo? runState;
  var _n = 0;
  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    calls.add('create $name');
    return 'seq-${++_n}';
  }

  @override
  Future<bool> deleteSequence(String id) async {
    calls.add('delete $id');
    return true;
  }

  /// What start throws instead, as the daemon refuses.
  Object? startError;
  @override
  Future<String> start(String id) async {
    if (startError case final e?) throw e;
    return _op('start $id');
  }
  @override
  Future<String> stop(String id) async => _op('stop $id');
  @override
  Future<String> pause(String id) async => _op('pause $id');
  @override
  Future<String> resume(String id, {bool recenter = true, bool refocus = false}) async =>
      _op('resume $id${recenter ? '' : ' no-recenter'}${refocus ? ' refocus' : ''}');
  String _op(String c) {
    calls.add(c);
    return 'op';
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async => const SequencePage(items: []);
  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) => throw UnimplementedError('${invocation.memberName}');
}

SequenceRunStateInfo _imaging(int frames, {SequenceRunState state = SequenceRunState.running}) =>
    SequenceRunStateInfo(sequenceId: 'seq-1', state: state, framesCaptured: frames);

class _Sel extends PlanetariumSelectionNotifier {
  _Sel(this.value);
  final PlanetariumSelection? value;
  @override
  PlanetariumSelection? build() => value;
}

class _Filters extends FilterSetNotifier {
  _Filters([this.filters = _lrgb]);
  final List<PlanningFilter> filters;
  @override
  FilterSetSettings build() => FilterSetSettings(filters: filters);
}

/// A colour camera's dual-band filters: long names.
const _duo = [
  PlanningFilter(name: 'L-enhance', kind: FilterKind.duo),
  PlanningFilter(name: 'L-eXtreme', kind: FilterKind.duo),
];

/// The text is drawn whole on one line: none of it cut or wrapped.
void _whole(WidgetTester tester, Finder text) {
  final p = tester.renderObject<RenderParagraph>(text);
  expect(p.didExceedMaxLines, isFalse, reason: '$text cut');
  expect(p.size.width, greaterThanOrEqualTo(p.getMaxIntrinsicWidth(double.infinity) - 0.5), reason: '$text cut');
}

// A configured rig, so the suggested sub lengths compute (as in
// target_plan_dialog_test).
class _Optics extends OpticsSettingsNotifier {
  @override
  OpticsSettings build() => const OpticsSettings(
      focalLengthMm: 447, reducerFactor: 1.0, sensorWidthPx: 6248, sensorHeightPx: 4176, pixelSizeUm: 3.76, apertureMm: 91);
}

class _Electronics extends CameraElectronicsNotifier {
  @override
  CameraElectronics build() =>
      const CameraElectronics(readNoiseE: 0.7, fullWellE: 50000, electronsPerAdu: 0.8, quantumEfficiencyPeak: 0.8);
}

class _Site extends SiteSettingsNotifier {
  @override
  SiteSettings build() => SiteSettings(bortleClass: 6);
}

class _NoSkyModel extends SkyModelCalibrationNotifier {
  @override
  Future<SkyModelCalibration?> build() async => null;
}

class _Prefs extends SimpleModePrefsService {
  _Prefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

class _Sessions extends LiveLibrarySessionsNotifier {
  @override
  Future<List<LibrarySession>?> build() async => [
        LibrarySession(
            id: 's1', targetName: 'M31', sessionStartUtc: DateTime.utc(2026, 11, 2, 20), sessionEndUtc: null,
            totalFrames: 21, lightFrames: 21, calibrationFrames: 0, filtersUsed: const ['L', 'R', 'G']),
      ];
}

final _rig = <Object>[
  opticsSettingsProvider.overrideWith(_Optics.new),
  cameraElectronicsProvider.overrideWith(_Electronics.new),
  siteSettingsProvider.overrideWith(_Site.new),
  skyModelCalibrationProvider.overrideWith(_NoSkyModel.new),
];

Future<ProviderContainer> _pump(WidgetTester tester, _Seq seq, Widget child,
    {PlanetariumSelection? sel = _m31,
    List<Object> overrides = const [],
    List<PlanningFilter> filters = _lrgb,
    List<TonightSkyObject> tonight = const []}) async {
  final container = ProviderContainer(overrides: [
    ...overrides.cast(),
    tonightSkyProvider.overrideWith((ref) async => tonight),
    sequenceApiProvider.overrideWith((ref) => seq),
    planetariumSelectionProvider.overrideWith(() => _Sel(sel)),
    filterSetProvider.overrideWith(() => _Filters(filters)),
  ]);
  addTearDown(container.dispose);
  // Tall enough for the sheet, tracker and shutter stacked in one column.
  tester.view.physicalSize = const Size(1024, 1100);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pumpAndSettle();
  return container;
}

void main() {
  test('the first plan: LRGB from the set, one unnamed row without a choice', () {
    final lrgb = simpleRunDefaultPlan(_lrgb, 300);
    expect(lrgb.map((s) => s.filterName), ['L', 'R', 'G', 'B']);
    expect(lrgb.map((s) => s.frameCount), [15, 15, 15, 15]);
    expect(lrgb.first.exposureSeconds, 300);

    final narrow = simpleRunDefaultPlan(const [
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
      PlanningFilter(name: 'OIII', kind: FilterKind.oiii),
    ], 180);
    expect(narrow.map((s) => s.filterName), ['Ha', 'OIII']);
    expect(narrow.first.frameCount, 30);

    final osc = simpleRunDefaultPlan(const [PlanningFilter(name: 'OSC', kind: FilterKind.osc)], 60);
    expect(osc, hasLength(1));
    expect(osc.single.filterName, '');
    expect(osc.single.frameCount, 60);
  });

  group('frames fill the dark window', () {
    TonightSkyObject m31(double hours) => TonightSkyObject(
        id: 'm31', name: 'M31', type: 'Galaxy', magnitude: 3.4, raDeg: 10.7, decDeg: 41.3,
        altitudeDeg: 62, maxAltitudeDeg: 74, remainingHours: hours);

    Future<ProviderContainer> plan(List<TonightSkyObject> tonight) async {
      final c = ProviderContainer(overrides: [
        tonightSkyProvider.overrideWith((ref) async => tonight),
        planetariumSelectionProvider.overrideWith(() => _Sel(_m31)),
        filterSetProvider.overrideWith(() => _Filters()),
      ]);
      addTearDown(c.dispose);
      c.listen(simpleRunPlanProvider, (_, _) {});
      await c.read(tonightSkyProvider.future);
      await Future<void>.delayed(Duration.zero);
      return c;
    }

    test('the picked target\'s window splits across the rows; a new sub refits its row', () async {
      final c = await plan([m31(2)]);
      // 2 h over four rows = 30 min each; 5 s subs fit 360, capped at 300.
      expect(c.read(simpleRunPlanProvider).map((s) => s.frameCount), [300, 300, 300, 300]);
      c.read(simpleRunPlanProvider.notifier).setExposure(0, 120);
      expect(c.read(simpleRunPlanProvider).first.frameCount, 15);
    });

    test('no window for the target (not ranked tonight): 60 frames split, a new sub keeps the count', () async {
      final c = await plan(const []);
      expect(c.read(simpleRunPlanProvider).map((s) => s.frameCount), [15, 15, 15, 15]);
      c.read(simpleRunPlanProvider.notifier).setExposure(0, 120);
      expect(c.read(simpleRunPlanProvider).first.frameCount, 15);
    });
  });

  test('progress fills the plan rows in order', () {
    const steps = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 40),
      FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 20),
    ];
    expect(simpleRunProgress(steps, 0), [0, 0]);
    expect(simpleRunProgress(steps, 23), [23, 0]);
    expect(simpleRunProgress(steps, 45), [40, 5]);
    expect(simpleRunProgress(steps, 99), [40, 20]);
  });

  test('the phase: a run in progress wins; a started plan ends only on a terminal state', () {
    const none = SimpleRun();
    const ready = SimpleRun(sequenceId: 'seq-1');
    const started = SimpleRun(sequenceId: 'seq-1', started: true);
    expect(simpleRunPhase(none, null), SimpleRunPhase.none);
    expect(simpleRunPhase(ready, null), SimpleRunPhase.ready);
    expect(simpleRunPhase(started, _imaging(3)), SimpleRunPhase.imaging);
    expect(simpleRunPhase(started.copyWith(pausing: true), _imaging(3)), SimpleRunPhase.pausing);
    expect(simpleRunPhase(started, _imaging(3, state: SequenceRunState.paused)), SimpleRunPhase.paused);
    expect(simpleRunPhase(started, _imaging(3, state: SequenceRunState.idle)), SimpleRunPhase.ready,
        reason: 'a just-started run still reads idle');
    expect(simpleRunPhase(started, _imaging(60, state: SequenceRunState.completed)), SimpleRunPhase.done);
    expect(simpleRunPhase(started, _imaging(9, state: SequenceRunState.stopped)), SimpleRunPhase.done);
    // A run adopted from the daemon, with no plan here.
    expect(simpleRunPhase(none, _imaging(7)), SimpleRunPhase.imaging);
  });

  test('simpleTargetStatus: up, below with its rise, never rises', () {
    expect(simpleTargetStatus(_m31), '62° up · max 74°');
    expect(
        simpleTargetStatus(PlanetariumSelection(
            name: 'M33', kind: 'Galaxy', raHours: 1.6, decDeg: 30.8, raJ2000Hours: 1.56, decJ2000Deg: 30.66,
            altDeg: -12, maxAltDeg: 60, rise: DateTime(2026, 11, 2, 22, 40))),
        'Below your horizon · rises 22:40');
    expect(
        simpleTargetStatus(const PlanetariumSelection(
            name: 'NGC 104', kind: 'Cluster', raHours: 0.4, decDeg: -72, raJ2000Hours: 0.4, decJ2000Deg: -72,
            altDeg: -40, maxAltDeg: -20)),
        'Never rises here');
  });

  test('the hint: rows that differ, grouped by length; null when all match', () {
    const lrgb = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 5, frameCount: 15),
      FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 15),
      FilterPlanStep(filterName: 'G', exposureSeconds: 5, frameCount: 15),
      FilterPlanStep(filterName: 'B', exposureSeconds: 5, frameCount: 15),
    ];
    expect(simpleRunSuggestion(lrgb, [120, 180, 180, 180]), 'L 2 min · GB 3 min');
    expect(simpleRunSuggestion(lrgb, [5, 180, 5, 5]), isNull);
    const narrow = [
      FilterPlanStep(filterName: 'Ha', exposureSeconds: 60, frameCount: 30),
      FilterPlanStep(filterName: 'OIII', exposureSeconds: 60, frameCount: 30),
    ];
    expect(simpleRunSuggestion(narrow, [300, 300]), 'Ha/OIII 5 min');
    const osc = [FilterPlanStep(filterName: '', exposureSeconds: 5, frameCount: 60)];
    expect(simpleRunSuggestion(osc, [90]), '90 s');
  });

  test('the chip reads the plan\'s total imaging time: the created plan, else the sheet\'s rows', () {
    const rows = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 15),
      FilterPlanStep(filterName: 'R', exposureSeconds: 180, frameCount: 15),
    ];
    expect(simpleRunChip(const SimpleRun(), rows), '1 h\n15 min');
    const created = SimpleRun(sequenceId: 'seq-1', steps: [
      FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 12),
    ]);
    expect(simpleRunChip(created, rows), '1 h\n0 min');
    expect(simpleRunChip(const SimpleRun(), const [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 45)]), '45 min');
  });

  test('a suggestion reads type and altitude, or when it rises; it becomes the selection', () {
    final up = TonightSkyObject(
        id: 'ngc7000', name: 'NGC 7000', type: 'Nebula', magnitude: 4, raDeg: 314.7, decDeg: 44.3,
        altitudeDeg: 71.4, maxAltitudeDeg: 80, windowEndUtc: DateTime.utc(2026, 11, 3, 1, 40));
    expect(simpleSuggestionLine(up), 'Nebula · 71° up');
    final later = TonightSkyObject(
        id: 'm45', name: 'M45', type: 'Cluster', magnitude: 1.6, raDeg: 56.9, decDeg: 24.1,
        altitudeDeg: -5, maxAltitudeDeg: 70, windowStartUtc: DateTime(2026, 11, 2, 21, 50));
    expect(simpleSuggestionLine(later), 'Cluster · rises 21:50');
    final sel = simpleSelectionOf(up);
    expect(sel.name, 'NGC 7000');
    expect(sel.raJ2000Hours, closeTo(314.7 / 15, 1e-9));
    expect(sel.decJ2000Deg, 44.3);
    expect(sel.altDeg, 71.4);
    expect(sel.belowHorizon, isFalse);
    expect(simpleSelectionOf(later).belowHorizon, isTrue);
  });

  test('durations read in hours and minutes', () {
    expect(simpleRunDuration(45 * 60), '45 min');
    expect(simpleRunDuration(6 * 3600 + 20 * 60), '6 h 20 min');
  });

  // The sheet, the tracker and the shutter together, as on the rail.
  Future<(ProviderContainer, _Seq)> run(WidgetTester tester,
      {PlanetariumSelection? sel = _m31, List<Object> overrides = const []}) async {
    final seq = _Seq();
    final c = await _pump(
        tester,
        seq,
        SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
            SimpleRunTracker(onTap: () {}),
            const SimpleRunShutter(),
          ]),
        ),
        sel: sel,
        overrides: overrides);
    return (c, seq);
  }

  // Add to queue: a plan on the rig, not started.
  Future<void> create(WidgetTester tester) async {
    await tester.tap(find.byKey(const Key('simple-run-queue')));
    await tester.pumpAndSettle();
  }

  Future<void> imaging(WidgetTester tester, ProviderContainer c, _Seq seq, SequenceRunStateInfo state) async {
    seq.runState = state;
    await c.read(sequenceRunStateProvider.notifier).refresh();
    await tester.pumpAndSettle();
  }

  group('the sheet', () {
    testWidgets('with nothing picked: search or Pick in Sky; both buttons are off', (tester) async {
      var showSky = 0, pick = 0;
      final c = await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () => showSky++, onPickInSky: () => pick++, onCreated: () {}, onLibrary: () {}),
          sel: null);
      expect(find.text('Search, or pick in the sky.'), findsOneWidget);
      expect(tester.widget<OutlinedButton>(find.byKey(const Key('simple-run-queue'))).onPressed, isNull);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNull);
      await tester.tap(find.byKey(const Key('simple-run-pick-in-sky')));
      expect(pick, 1);
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'M42');
      await tester.testTextInput.receiveAction(TextInputAction.search);
      await tester.pump();
      expect(showSky, 1, reason: 'the search runs in the planetarium');
      expect(c.read(planetariumSearchProvider), 'M42');
    });

    testWidgets('Add to queue creates the run without starting it; again it replaces it', (tester) async {
      var created = 0;
      final seq = _Seq();
      await _pump(
          tester,
          seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () => created++, onLibrary: () {}));
      expect(find.text('M31'), findsOneWidget);
      expect(find.text('Add to queue'), findsOneWidget);
      expect(find.text('Create and start'), findsOneWidget);
      await create(tester);
      expect(seq.calls, ['create M31']);
      expect(created, 1);
      // shortcut in the app: one queued plan for now, so a new one replaces it.
      await create(tester);
      expect(seq.calls, ['create M31', 'create M31', 'delete seq-1']);
    });

    testWidgets('Create and start creates the run and starts it, then closes the sheet', (tester) async {
      var created = 0;
      final seq = _Seq();
      await _pump(
          tester,
          seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () => created++, onLibrary: () {}));
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31', 'start seq-1']);
      expect(created, 1);
    });

    testWidgets('tonight\'s best: up to four suggestions, one tap picks the target', (tester) async {
      TonightSkyObject o(String name, double alt) => TonightSkyObject(
          id: name, name: name, type: 'Galaxy', magnitude: 5, raDeg: 10, decDeg: 40, altitudeDeg: alt, maxAltitudeDeg: 70);
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
          sel: null,
          tonight: [o('M31', 62), o('NGC 7000', 71), o('M33', 48), o('M45', 30), o('M42', 10)]);
      expect(find.text("TONIGHT'S BEST"), findsOneWidget);
      for (final n in ['M31', 'NGC 7000', 'M33', 'M45']) {
        expect(find.byKey(Key('simple-run-suggestion-$n')), findsOneWidget, reason: n);
      }
      expect(find.byKey(const Key('simple-run-suggestion-M42')), findsNothing, reason: 'four at most');
      await tester.tap(find.byKey(const Key('simple-run-suggestion-NGC 7000')));
      await tester.pump();
      expect(c.read(planetariumSelectionProvider)?.name, 'NGC 7000');
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create NGC 7000', 'start seq-1']);
    });

    testWidgets('no ranking (no site yet): no suggestions, search stays', (tester) async {
      await _pump(tester, _Seq(), SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}));
      expect(find.text("TONIGHT'S BEST"), findsNothing);
      expect(find.byKey(const Key('simple-run-search')), findsOneWidget);
    });

    testWidgets('a configured rig gets the suggested subs in one line; Use all takes them', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
          overrides: _rig);
      expect(find.textContaining('Suggested: '), findsOneWidget);
      final before = c.read(simpleRunPlanProvider).map((s) => s.exposureSeconds).toList();
      expect(before.toSet(), {5.0}, reason: 'rows start at the profile default');
      await tester.tap(find.byKey(const Key('simple-run-use-suggested')));
      await tester.pumpAndSettle();
      final after = c.read(simpleRunPlanProvider).map((s) => s.exposureSeconds).toList();
      expect(after.every((s) => s >= 30 && s <= 600), isTrue, reason: 'the plan clamp: $after');
      expect(find.textContaining('Suggested: '), findsNothing, reason: 'every row matches now');
    });

    for (final (aligned, permanent, shown) in [
      (SetupStepState.pending, false, true),
      (SetupStepState.done, false, false),
      (SetupStepState.pending, true, false),
    ]) {
      testWidgets('polar align $aligned, permanent pier $permanent: amber line ${shown ? 'shows' : 'hidden'}',
          (tester) async {
        await _pump(tester, _Seq(),
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
            overrides: [
              setupPolarAlignStateProvider.overrideWithValue(aligned),
              simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(permanent)),
            ]);
        expect(find.text('Not polar aligned tonight'), shown ? findsOneWidget : findsNothing);
        // A warning, not a gate.
        expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNotNull);
      });
    }

    testWidgets('an unconfigured rig has no hint; the last row stays', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}));
      expect(find.byKey(const Key('simple-run-use-suggested')), findsNothing);
      expect(find.text('60 frames'), findsOneWidget);
      final plan = c.read(simpleRunPlanProvider.notifier);
      for (var i = 0; i < 5; i++) {
        plan.remove(0);
      }
      expect(c.read(simpleRunPlanProvider).map((s) => s.filterName), ['B']);
    });
  });

  testWidgets('long filter names show whole in the sheet\'s rows', (tester) async {
    await _pump(tester, _Seq(),
        SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
        filters: _duo);
    _whole(tester, find.text('L-enhance'));
    _whole(tester, find.text('L-eXtreme'));
  });

  testWidgets('a long object name shows whole in the tracker, clear of its border', (tester) async {
    const andromeda = PlanetariumSelection(
        name: 'Andromeda Galaxy', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71,
        decJ2000Deg: 41.27, altDeg: 62.4, maxAltDeg: 74.2);
    final (_, _) = await run(tester, sel: andromeda);
    await create(tester);
    final name = find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('Andromeda Galaxy'));
    _whole(tester, name);
    final box = tester.getRect(find.byKey(const Key('simple-run-tracker')));
    final text = tester.getRect(name);
    expect(text.left, greaterThanOrEqualTo(box.left + 4), reason: 'clear of the left border');
    expect(text.right, lessThanOrEqualTo(box.right - 4), reason: 'clear of the right border');
  });

  testWidgets('no storage disk: the daemon\'s words and Open Storage, which leaves for the full UI\'s page', (tester) async {
    final seq = _Seq()
      ..startError = DioException(
        requestOptions: RequestOptions(),
        response: Response(requestOptions: RequestOptions(), statusCode: 409, data: {
          'title': 'store_unavailable',
          'detail': 'There is no disk to save frames to.',
        }),
      );
    final prefs = _Prefs(true);
    final c = await _pump(tester, seq,
        SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}, onLibrary: () {}),
        overrides: [simpleModePrefsProvider.overrideWithValue(prefs)]);
    await tester.tap(find.byKey(const Key('simple-run-start')));
    await tester.pumpAndSettle();
    expect(find.text('There is no disk to save frames to.'), findsOneWidget);
    await tester.tap(find.text('Open Storage'));
    await tester.pumpAndSettle();
    expect(c.read(selectedSettingsPanelProvider), 'session.storage');
    expect(c.read(selectedTabIndexProvider), kOptionsTabIndex);
    expect(prefs.value, isFalse, reason: 'Simple Mode left');
  });

  testWidgets('Night done: how long, how it ended, where it stopped, and the frames the library filed per filter',
      (tester) async {
    final start = DateTime.utc(2026, 11, 2, 21);
    LibraryFrameItem frame(String filter, DateTime at) => LibraryFrameItem(
        id: '$filter$at', frameType: 'light', filterName: filter, exposureSeconds: 5, capturedUtc: at,
        hfr: null, starCount: null, rating: 0);
    final frames = [
      frame('L', start.subtract(const Duration(hours: 1))), // an earlier run's
      for (var i = 0; i < 12; i++) frame('L', start.add(Duration(minutes: i))),
      for (var i = 0; i < 6; i++) frame('R', start.add(Duration(minutes: 20 + i))),
      for (var i = 0; i < 2; i++) frame('G', start.add(Duration(minutes: 30 + i))),
    ];
    final (c, seq) = await run(tester, overrides: [
      liveLibrarySessionsProvider.overrideWith(_Sessions.new),
      sessionFramesProvider.overrideWith((ref, id) async => frames),
    ]);
    await create(tester);
    await c.read(simpleRunProvider.notifier).start();
    // The plan-order estimate would read L 15, R 5.
    await imaging(
        tester,
        c,
        seq,
        SequenceRunStateInfo(
          sequenceId: 'seq-1',
          state: SequenceRunState.stopped,
          framesCaptured: 20,
          startedUtc: start,
          completedUtc: start.add(const Duration(hours: 2, minutes: 30)),
          currentInstructionDescription: 'Take exposure (G)',
        ));
    expect(find.text('Night done'), findsOneWidget);
    expect(find.textContaining('2 h 30 min · stopped'), findsOneWidget);
    expect(find.text('Stopped at: Take exposure (G)'), findsOneWidget);
    expect(find.text('12 × 5 s'), findsOneWidget);
    expect(find.text('6 × 5 s'), findsOneWidget);
    expect(find.text('2 × 5 s'), findsOneWidget);
    expect(find.text('0 × 5 s'), findsOneWidget);
  });

  group('the progress panel', () {
    testWidgets('a panic stop reads Mount stopped while paused; resuming clears it', (tester) async {
      final ws = StreamController<WsEvent>.broadcast();
      addTearDown(ws.close);
      final seq = _Seq();
      final c = await _pump(tester, seq, const SimpleRunPanel(),
          overrides: [wsEventsProvider.overrideWith((ref) => ws.stream)]);
      c.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await imaging(tester, c, seq, _imaging(24));
      ws.add(WsEvent(type: SlewWsEvents.aborted, ts: DateTime.utc(2026), seq: 1));
      await tester.pumpAndSettle();
      // The daemon's pause lands after the abort.
      await imaging(tester, c, seq, _imaging(24, state: SequenceRunState.paused));
      expect(find.text('Mount stopped'), findsOneWidget);
      await imaging(tester, c, seq, _imaging(24));
      expect(find.text('Mount stopped'), findsNothing);
      // A plain pause says nothing.
      await imaging(tester, c, seq, _imaging(25, state: SequenceRunState.paused));
      expect(find.text('Mount stopped'), findsNothing);
    });

    testWidgets('paused: resume without re-centering, or re-center and refocus; imaging: neither', (tester) async {
      final seq = _Seq();
      final c = await _pump(tester, seq, const SimpleRunPanel());
      c.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await imaging(tester, c, seq, _imaging(24));
      expect(find.textContaining('pause after this frame'), findsOneWidget);
      expect(find.text('Resume without re-centering'), findsNothing);
      expect(find.text('Re-center and refocus'), findsNothing);

      await imaging(tester, c, seq, _imaging(24, state: SequenceRunState.paused));
      expect(find.textContaining('Tap the shutter to resume'), findsOneWidget);
      await tester.tap(find.text('Resume without re-centering'));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'resume seq-1 no-recenter');
      await tester.tap(find.text('Re-center and refocus'));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'resume seq-1 refocus');
    });
  });

  group('the tracker and the shutter', () {
    testWidgets('nothing picked: no tracker, and the shutter does nothing', (tester) async {
      final (_, seq) = await run(tester, sel: null);
      expect(find.byKey(const Key('simple-run-tracker')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(seq.calls, isEmpty);
    });

    testWidgets('a target picked, no plan yet: the shutter creates and starts it', (tester) async {
      final (_, seq) = await run(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31', 'start seq-1']);
    });

    testWidgets('plan ready: 0 of the total, the first filter; the shutter starts it', (tester) async {
      final (_, seq) = await run(tester);
      await create(tester);
      expect(find.byKey(const Key('simple-run-tracker')), findsOneWidget);
      expect(find.text('0 / 60'), findsOneWidget);
      expect(find.text('L · 5 s'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'start seq-1');
    });

    testWidgets('below the horizon: Add to queue only; the plan waits for the rise', (tester) async {
      final rise = DateTime.now().add(const Duration(hours: 2));
      final low = PlanetariumSelection(
          name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27,
          altDeg: -8, maxAltDeg: 74.2, rise: rise);
      final (_, seq) = await run(tester, sel: low);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNull,
          reason: 'no Create and start below the horizon');
      await create(tester);
      expect(seq.calls, ['create M31'], reason: 'queued');
      final hhmm = '${rise.hour.toString().padLeft(2, '0')}:${rise.minute.toString().padLeft(2, '0')}';
      expect(find.text('rises $hhmm'), findsOneWidget, reason: 'the plan square says when');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31'], reason: 'the shutter waits for the rise');
    });

    testWidgets('a target that never rises here can\'t be queued', (tester) async {
      const never = PlanetariumSelection(
          name: 'Omega Cen', kind: 'Globular', raHours: 13.4, decDeg: -47.5, raJ2000Hours: 13.4,
          decJ2000Deg: -47.5, altDeg: -30, maxAltDeg: -5);
      await run(tester, sel: never);
      expect(tester.widget<OutlinedButton>(find.byKey(const Key('simple-run-queue'))).onPressed, isNull);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNull);
    });

    testWidgets('imaging: a tap pauses (Pausing… until it lands), paused a tap resumes', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await imaging(tester, c, seq, _imaging(23));
      expect(find.text('23 / 60'), findsOneWidget);
      expect(find.text('R · 5 s'), findsOneWidget, reason: '15 per filter: frame 23 is on R');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'pause seq-1');
      expect(find.text('Pausing…'), findsOneWidget);
      await imaging(tester, c, seq, _imaging(24, state: SequenceRunState.paused));
      expect(find.text('Paused'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'resume seq-1');
    });

    testWidgets('holding 2 s stops the run; letting go early does nothing', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await imaging(tester, c, seq, _imaging(23));
      final before = seq.calls.length;

      var g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-shutter'))));
      await tester.pump(); // the hold's first frame
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('Hold to stop'), findsOneWidget);
      await g.up();
      await tester.pumpAndSettle();
      expect(seq.calls.length, before, reason: 'released at 1 s: neither a pause nor a stop');

      g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-shutter'))));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 2100));
      await g.up();
      await tester.pumpAndSettle();
      expect(seq.calls.sublist(before), ['stop seq-1']);
    });
  });
}
