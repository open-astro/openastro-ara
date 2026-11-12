import 'dart:async';

import 'package:flutter/gestures.dart' show PointerDeviceKind, kSecondaryMouseButton;
import 'dart:convert';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:flutter/cupertino.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter/rendering.dart' show RenderBox, RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:dio/dio.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/nina_dom.dart' show childrenOf, conditionsOf;
import 'package:openastroara/models/sequence/instruction_catalog.dart' show parkScopeType, waitForTimeType, flatPanelFlatsType, waitForUserType, switchFilterType;
import 'package:openastroara/models/sequence/condition_catalog.dart' show timeConditionType, timeProviders;
import 'package:openastroara/util/tonight_sky_local.dart' show darkStart, nightEnd;
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/equipment_device_status.dart' show EquipmentConnectionState;
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/setup/setup_readiness.dart';
import 'package:openastroara/state/simple_preflight_state.dart'
    show nightPolarAlignStateProvider;
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/app_locale_state.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/stop_mount_overlay.dart' show SlewWsEvents;
import 'package:openastroara/models/library/live_library.dart';
import 'package:openastroara/state/library/live_library_state.dart';

const _m42 = PlanetariumSelection(
    name: 'M42', kind: 'Nebula', raHours: 5.59, decDeg: -5.39, raJ2000Hours: 5.59, decJ2000Deg: -5.39, altDeg: 30, maxAltDeg: 45);

const _m45 = PlanetariumSelection(
    name: 'M45', kind: 'Cluster', raHours: 3.79, decDeg: 24.1, raJ2000Hours: 3.79, decJ2000Deg: 24.1, altDeg: 40, maxAltDeg: 60);

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
class _Gain150 extends ExposureController {
  @override
  ExposureParams build() => super.build().copyWith(gain: 150);
}

/// The offline-draft store, in memory: a transport failure on create lands here.
class _MemDraftService extends DraftSequenceService {
  final Map<String, DraftSequence> store = {};
  int _n = 0;
  @override
  String newId() => '${draftIdPrefix}mem-${_n++}';
  @override
  Future<List<DraftSequence>> loadAll() async => store.values.toList();
  @override
  Future<void> save(DraftSequence draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

class _Locale extends AppLocaleController {
  _Locale(this.picked);
  final Locale picked;
  @override
  Locale? build() => picked;
}

class _Seq implements SequenceClient {
  final calls = <String>[];
  SequenceRunStateInfo? runState;
  var _n = 0;
  /// The bodies the daemon keeps, by id.
  final bodies = <String, Map<String, dynamic>>{};
  /// The rig is unreachable for the next create (the link drops).
  bool createFails = false;
  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    if (createFails) throw DioException(requestOptions: RequestOptions(), message: 'link dropped');
    calls.add('create $name');
    final id = 'seq-${++_n}';
    bodies[id] = body;
    return id;
  }

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async => SequenceDetail(id: id, body: bodies[id] ?? const {});

  /// Updates that go through before the rest fail (the link drops); -1: none fail.
  int updatesBeforeFail = -1;
  /// Holds every update until completed (a slow link).
  Completer<void>? updateGate;
  @override
  Future<SequenceDetail> updateSequence(String id, {String? name, String? description, Map<String, dynamic>? body}) async {
    await updateGate?.future;
    if (updatesBeforeFail == 0) throw DioException(requestOptions: RequestOptions(), message: 'link dropped');
    if (updatesBeforeFail > 0) updatesBeforeFail--;
    calls.add('update $id');
    if (body != null) bodies[id] = body;
    return SequenceDetail(id: id, body: bodies[id] ?? const {});
  }

  @override
  Future<SequenceDetail> removeRunItem(String id, List<int> path) async {
    calls.add('remove-live $id $path');
    return SequenceDetail(id: id, body: bodies[id] ?? const {});
  }

  /// The target blocks' names in the stored body, in order.
  List<String> targets(String id) => [
        for (final c in childrenOf(bodies[id]!))
          if (indexOfTargetBlock(bodies[id]!, c['Name'] as String? ?? '') >= 0 &&
              childrenOf(c).isNotEmpty)
            c['Name'] as String,
      ];

  @override
  Future<bool> deleteSequence(String id) async {
    calls.add('delete $id');
    return true;
  }

  /// What start throws instead, as the daemon refuses.
  Object? startError;
  /// Holds start until completed (a slow link).
  Completer<void>? startGate;
  @override
  Future<String> start(String id) async {
    if (startError case final e?) throw e;
    await startGate?.future;
    return _op('start $id');
  }
  /// What stop throws instead.
  Object? stopError;
  @override
  Future<String> stop(String id) async {
    if (stopError case final e?) throw e;
    return _op('stop $id');
  }
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

/// A filter wheel the daemon retains, or none.
class _Wheel extends FilterWheelNotifier {
  _Wheel(this.present);
  final bool present;
  @override
  Future<FilterWheelStatus?> build() async => present
      ? FilterWheelStatus(
          deviceId: 'w', name: 'Wheel', connectionState: EquipmentConnectionState.connected,
          runtimeState: 'idle', currentSlot: 0, slots: const [])
      : null;
}

class _SiteAt extends SiteSettingsNotifier {
  _SiteAt(this.site);
  final SiteSettings site;
  @override
  SiteSettings build() => site;
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
    filterWheelProvider.overrideWith(() => _Wheel(true)),
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

final _en = lookupAppLocalizations(const Locale('en'));



/// Tabs (at most [max] times) until keyboard focus is inside the widget keyed [k].
Future<bool> _tabTo(WidgetTester tester, Key k, {int max = 40}) async {
  bool inside() {
    var found = false;
    final ctx = FocusManager.instance.primaryFocus?.context;
    if (ctx == null) return false;
    if (ctx.widget.key == k) return true;
    ctx.visitAncestorElements((e) {
      if (e.widget.key == k) found = true;
      return !found;
    });
    return found;
  }
  for (var i = 0; i < max && !inside(); i++) {
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
  }
  return inside();
}


/// Tabs (at most [max] times) until the focused node is the one labelled [label].
Future<bool> _tabToLabel(WidgetTester tester, String label, {int max = 60}) async {
  for (var i = 0; i < max && FocusManager.instance.primaryFocus?.debugLabel != label; i++) {
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
  }
  return FocusManager.instance.primaryFocus?.debugLabel == label;
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

  test('a plan for a rig that cannot change filters is one unnamed row (#1737)', () {
    final rows = simpleRunDefaultPlan(_lrgb, 300, canChangeFilters: false);
    expect(rows, hasLength(1));
    expect(rows.single.filterName, '');
    expect(rows.single.frameCount, 60);
  });

  testWidgets('picking an object with no site set (0, 0) gives it no computed altitude (#1737)', (tester) async {
    const dso = PlanningDso(id: 'X1', name: 'Overhead', type: 'G', magnitude: 9, raDeg: 280.46, decDeg: -33);
    Future<PlanetariumSelection> pick(SiteSettings site) async {
      late PlanetariumSelection sel;
      await tester.pumpWidget(ProviderScope(
        key: UniqueKey(),
        overrides: [
          siteSettingsProvider.overrideWith(() => _SiteAt(site)),
          tonightSkyProvider.overrideWith((ref) async => const []),
          clockProvider.overrideWithValue(() => DateTime.utc(2000, 1, 1, 12)),
        ],
        child: Consumer(builder: (context, ref, _) {
          sel = simplePickDso(ref, dso);
          return const SizedBox();
        }),
      ));
      return sel;
    }

    expect((await pick(const SiteSettings())).altDeg, isNull);
    expect((await pick(const SiteSettings(latitudeDeg: -33, longitudeDeg: 10))).altDeg, isNotNull);
  });

  group('frames fill the dark window', () {
    TonightSkyObject m31(double hours) => TonightSkyObject(
        id: 'm31', name: 'M31', type: 'Galaxy', magnitude: 3.4, raDeg: 10.7, decDeg: 41.3,
        altitudeDeg: 62, maxAltitudeDeg: 74, remainingHours: hours);

    Future<ProviderContainer> plan(List<TonightSkyObject> tonight, {bool wheel = true}) async {
      final c = ProviderContainer(overrides: [
        filterWheelProvider.overrideWith(() => _Wheel(wheel)),
        tonightSkyProvider.overrideWith((ref) async => tonight),
        planetariumSelectionProvider.overrideWith(() => _Sel(_m31)),
        filterSetProvider.overrideWith(() => _Filters()),
      ]);
      addTearDown(c.dispose);
      c.listen(simpleRunPlanProvider, (_, _) {});
      await c.read(tonightSkyProvider.future);
      await c.read(filterWheelProvider.future);
      await Future<void>.delayed(Duration.zero);
      return c;
    }

    test('a rig that cannot change filters gets one row; with a wheel the rows are the filters\' (#1737)', () async {
      final bare = await plan([m31(2)], wheel: false);
      expect(bare.read(simpleRunPlanProvider).map((s) => s.filterName), [''], reason: 'one row, no filter implied');
      expect((await plan([m31(2)])).read(simpleRunPlanProvider).map((s) => s.filterName), ['L', 'R', 'G', 'B']);
    });

    test('a settings re-read that changes nothing keeps the rows; a queued target\'s rows stay while editing',
        () async {
      // Review: hydrate always assigns a new object and the settings have no ==, so the rows reset to the
      // defaults about one round trip after opening an edit, and Save wrote the defaults.
      final c = await plan([m31(2)]);
      final rows = c.read(simpleRunPlanProvider.notifier);
      rows.setExposure(0, 120);
      final edited = c.read(simpleRunPlanProvider);
      await c.read(filterSetProvider.notifier).hydrateGuarded(() async => FilterSetSettings(filters: [..._lrgb]));
      expect(c.read(simpleRunPlanProvider), edited, reason: 'the same filters read again');
      const queued = [FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 12)];
      rows.load(queued);
      await c.read(filterSetProvider.notifier).hydrateGuarded(() async => const FilterSetSettings(filters: _duo));
      expect(c.read(simpleRunPlanProvider), queued, reason: "editing a queued target: its own rows");
      c.invalidate(simpleRunPlanProvider);
      expect(c.read(simpleRunPlanProvider).map((s) => s.filterName), ['L-enhance', 'L-eXtreme'],
          reason: 'a new plan follows the filters');
    });

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

  test('the plan square\'s catalog tag: a region\'s own, else M > NGC > IC > Sh2 > B > LBN, else none', () {
    expect(simpleTargetTag('Horsehead + Flame region (IC 434)', ['IC0434']), 'IC 434');
    expect(simpleTargetTag('Question Mark region (NGC 7822 / Ced 214)', ['NGC7822']), 'NGC 7822');
    expect(simpleTargetTag('Spaghetti Nebula (Simeis 147)', ['REGION-SIMEIS-147']), 'Simeis 147');
    expect(simpleTargetTag('Orion Nebula + Running Man (M42)', ['NGC1976']), 'M 42');
    expect(simpleTargetTag('Rho Ophiuchi cloud complex', ['REGION-RHO-OPH']), isNull);
    // A sky pick: the page's designations.
    expect(simpleTargetTag('Horsehead Nebula', ['NAME Horsehead Nebula', 'B 33', 'LDN 1630']), 'B 33');
    expect(simpleTargetTag('Orion Nebula', ['NGC 1976', 'M 42']), 'M 42');
    expect(simpleTargetTag('Andromeda Galaxy', ['NGC0224', 'M031']), 'M 31');
    expect(simpleTargetTag('Flame Nebula', ['NGC 2024', 'Sh2-277']), 'NGC 2024');
    expect(simpleTargetTag('Sh2-129', ['SH 2-129']), 'Sh2-129');
    expect(simpleTargetTag('Twin Jet', ['M 2-9']), isNull, reason: 'Minkowski, not Messier');
  });

  test('a tonight\'s-best pick carries its id and aliases; the page\'s pick its designations', () {
    final o = TonightSkyObject(
        id: 'IC0434', name: 'Horsehead + Flame region (IC 434)', type: 'HII', magnitude: null, raDeg: 85.25,
        decDeg: -2.45, altitudeDeg: 30, maxAltitudeDeg: 50, aliases: const ['LBN 953']);
    expect(simpleSelectionOf(o).designations, ['IC0434', 'LBN 953']);
    final page = PlanetariumSelection.tryFromJson(const {
      'name': 'Horsehead Nebula', 'raHours': 5.68, 'decDeg': -2.46, 'maxAltDeg': 50,
      'designations': ['NAME Horsehead Nebula', 'B 33'],
    });
    expect(page!.designations, ['NAME Horsehead Nebula', 'B 33']);
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
    expect(simpleTargetStatus(_m31, _en), '62° up · max 74°');
    expect(
        simpleTargetStatus(PlanetariumSelection(
            name: 'M33', kind: 'Galaxy', raHours: 1.6, decDeg: 30.8, raJ2000Hours: 1.56, decJ2000Deg: 30.66,
            altDeg: -12, maxAltDeg: 60, rise: DateTime(2026, 11, 2, 22, 40)), _en),
        'Below your horizon · rises 22:40');
    expect(
        simpleTargetStatus(const PlanetariumSelection(
            name: 'NGC 104', kind: 'Cluster', raHours: 0.4, decDeg: -72, raJ2000Hours: 0.4, decJ2000Deg: -72,
            altDeg: -40, maxAltDeg: -20), _en),
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
    const created = SimpleRun(sequenceId: 'seq-1', targets: [
      SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 12)]),
    ]);
    expect(simpleRunChip(created, rows), '1 h\n0 min');
    expect(simpleRunChip(const SimpleRun(), const [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 45)]), '45 min');
  });

  test('a suggestion reads type and altitude, or when it rises; it becomes the selection', () {
    final up = TonightSkyObject(
        id: 'ngc7000', name: 'NGC 7000', type: 'Nebula', magnitude: 4, raDeg: 314.7, decDeg: 44.3,
        altitudeDeg: 71.4, maxAltitudeDeg: 80, windowEndUtc: DateTime.utc(2026, 11, 3, 1, 40));
    expect(simpleSuggestionLine(_en, up), 'Nebula · 71° up');
    final later = TonightSkyObject(
        id: 'm45', name: 'M45', type: 'Cluster', magnitude: 1.6, raDeg: 56.9, decDeg: 24.1,
        altitudeDeg: -5, maxAltitudeDeg: 70, windowStartUtc: DateTime(2026, 11, 2, 21, 50));
    expect(simpleSuggestionLine(_en, later), 'Cluster · rises 21:50');
    final sel = simpleSelectionOf(up);
    expect(sel.name, 'NGC 7000');
    expect(sel.raJ2000Hours, closeTo(314.7 / 15, 1e-9));
    expect(sel.decJ2000Deg, 44.3);
    expect(sel.altDeg, 71.4);
    expect(sel.belowHorizon, isFalse);
    expect(simpleSelectionOf(later).belowHorizon, isTrue);
  });

  test('the altitude of a position from the site at a moment', () {
    // At J2000.0 the sidereal time at longitude 0 is 280.46°: a star there with the site's declination is overhead.
    final utc = DateTime.utc(2000, 1, 1, 12);
    expect(simpleAltitudeDeg(280.46, -33, latitudeDeg: -33, longitudeDeg: 0, utc: utc), closeTo(90, 0.1));
    expect(simpleAltitudeDeg(100.46, -33, latitudeDeg: -33, longitudeDeg: 0, utc: utc), closeTo(-24, 0.1),
        reason: 'twelve hours from the meridian');
    // Away from the epoch and off the prime meridian: the time and the site both count (review, #1708).
    // The expected value is from the IAU 1982 sidereal-time polynomial, not this formula.
    expect(simpleAltitudeDeg(10.68, 41.27, latitudeDeg: 40, longitudeDeg: -75, utc: DateTime.utc(2026, 11, 9, 3)),
        closeTo(84.08, 0.05));
  });

  test('an object outside the ranking still gets its real altitude, so it is not called below the horizon', () {
    const up = PlanningDso(id: 'X1', name: 'Overhead', type: 'G', magnitude: 9, raDeg: 280.46, decDeg: -33);
    const down = PlanningDso(id: 'X2', name: 'Under', type: 'G', magnitude: 9, raDeg: 100.46, decDeg: -33);
    final utc = DateTime.utc(2000, 1, 1, 12);
    final high = simpleSelectionOfDso(up, const [], latitudeDeg: -33, longitudeDeg: 0, utc: utc);
    expect(high.altDeg, closeTo(90, 0.1));
    expect(high.belowHorizon, isFalse);
    expect(simpleSelectionOfDso(down, const [], latitudeDeg: -33, longitudeDeg: 0, utc: utc).belowHorizon, isTrue);
  });

  test('an album card becomes the selection: the ranker row when listed, else the catalog position', () {
    const m31 = PlanningDso(
        id: 'NGC0224', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.68, decDeg: 41.27,
        aliases: ['M 31']);
    final ranked = [
      TonightSkyObject(
          id: 'M31', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.68, decDeg: 41.27,
          altitudeDeg: 55, maxAltitudeDeg: 70, windowStartUtc: DateTime.utc(2026, 11, 2, 8)),
    ];
    final listed = simpleSelectionOfDso(m31, ranked, latitudeDeg: -33.856784);
    expect(listed.altDeg, 55, reason: 'found under its alias, with the ranker altitude');
    expect(listed.maxAltDeg, 70);

    final unlisted = simpleSelectionOfDso(m31, const [], latitudeDeg: -33.856784);
    expect(unlisted.altDeg, isNull, reason: 'no altitude from the catalog position alone');
    expect(unlisted.rise, isNull);
    expect(unlisted.raJ2000Hours, closeTo(10.68 / 15, 1e-9));
    expect(unlisted.decJ2000Deg, 41.27);
    expect(unlisted.maxAltDeg, closeTo(90 - (41.27 + 33.856784), 1e-9));
    expect(unlisted.designations, ['NGC0224', 'M 31']);
    // Picked as "M 31" (a search row or a Card): that designation leads, once, so the header names it.
    expect(simpleSelectionOfDso(m31, const [], latitudeDeg: -33.856784, designation: 'M 31').designations,
        ['M 31', 'NGC0224']);
    expect(simpleSelectionOfDso(m31, ranked, latitudeDeg: -33.856784, designation: 'M 31').designations, ['M 31'],
        reason: "the ranker row's M31 is the same designation");
    final southern = PlanningDso(
        id: 'NGC5128', name: 'Centaurus A', type: 'G', magnitude: 6.8, raDeg: 201.37, decDeg: -47.48);
    expect(simpleSelectionOfDso(southern, const [], latitudeDeg: 51.477833).neverRises, isTrue);
  });

  test('durations read in hours and minutes', () {
    expect(simpleRunDuration(45 * 60), '45 min');
    expect(simpleRunDuration(6 * 3600 + 20 * 60), '6 h 20 min');
  });

  // The sheet, the tracker and the shutter together, as on the rail.
  Future<(ProviderContainer, _Seq)> run(WidgetTester tester,
      {PlanetariumSelection? sel = _m31, List<Object> overrides = const [], VoidCallback? onTracker}) async {
    final seq = _Seq();
    final c = await _pump(
        tester,
        seq,
        SingleChildScrollView(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            SimpleRunTracker(onTap: onTracker ?? () {}),
            const SimpleRunShutter(),
          ]),
        ),
        sel: sel,
        overrides: overrides);
    return (c, seq);
  }

  // Add to queue: a plan on the rig, not started.
  /// Tabs until the keyboard focus holds [target].
  Future<void> tabTo(WidgetTester tester, Finder target) async {
    for (var i = 0; i < 40; i++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
      final ctx = FocusManager.instance.primaryFocus?.context;
      if (ctx != null &&
          find.descendant(of: find.byElementPredicate((e) => e == ctx), matching: target).evaluate().isNotEmpty) {
        return;
      }
    }
    fail('Tab never reached $target');
  }

  Future<void> create(WidgetTester tester) async {
    await tester.tap(find.byKey(const Key('simple-run-queue')));
    await tester.pumpAndSettle();
  }

  // The queue's root children's names, in order.
  List<String?> blocks(_Seq seq) =>
      [for (final c in childrenOf(seq.bodies.values.first)) c['Name'] as String?];

  testWidgets("#1757 a target set to After target shoots its filters' flats right after its frames, asking for a screen with no panel", (tester) async {
    final (c, seq) = await run(tester);
    expect(c.read(simpleRunFlatsProvider), SimpleTargetFlats.none, reason: 'no flats unless asked');
    await tester.ensureVisible(find.byKey(const Key('simple-run-flats-after')));
    await tester.tap(find.byKey(const Key('simple-run-flats-after')));
    await tester.pumpAndSettle();
    await create(tester);

    final target = childrenOf(seq.bodies.values.single).firstWhere((b) => b['Name'] == 'M31');
    final kids = childrenOf(target);
    final types = [for (final k in kids) k[r'$type']];
    final lastLight = kids.lastIndexWhere((k) => '${k['Name']}'.endsWith(' Imaging'));
    final flats = [for (var i = 0; i < types.length; i++) if (types[i] == flatPanelFlatsType) i];
    expect(flats, hasLength(c.read(simpleRunPlanProvider).length), reason: 'one set per filter');
    expect(flats.first, greaterThan(lastLight), reason: 'after its last frame, at its rotator angle');
    expect(types.indexOf(waitForUserType), greaterThan(lastLight), reason: 'no panel: a screen is asked for first');
    expect(c.read(simpleRunProvider).targets.single.flats, SimpleTargetFlats.afterTarget);
  });

  testWidgets('#1757 End of night: one Flats block after the targets, for every filter set to it', (tester) async {
    final (c, seq) = await run(tester);
    c.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
    await create(tester);
    expect(blocks(seq), containsAllInOrder(['M31', kEndFlatsBlockName]));

    c.read(planetariumSelectionProvider.notifier).set(_m42);
    c.read(simpleRunPlanProvider.notifier).load(const [FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 20)]);
    await tester.pump();
    await create(tester);

    expect(blocks(seq).where((n) => n == kEndFlatsBlockName), hasLength(1), reason: 'rebuilt, not doubled');
    expect(blocks(seq).indexOf('M42'), lessThan(blocks(seq).indexOf(kEndFlatsBlockName)), reason: 'targets first');
    final end = childrenOf(seq.bodies.values.first).firstWhere((b) => b['Name'] == kEndFlatsBlockName);
    final filters = [for (final k in childrenOf(end)) if (k[r'$type'] == switchFilterType) (k['Filter'] as Map)['_name']];
    expect(filters, containsAll(['L', 'Ha']));
    expect(c.read(simpleRunProvider).targets.last.name, kEndFlatsBlockName);
  });

  testWidgets('#1757 the Flats row reads as Flats to screen readers', (tester) async {
    final handle = tester.ensureSemantics();
    await run(tester);
    final node = tester.getSemantics(find.byKey(const Key('simple-run-flats-end')));
    expect(node.label, allOf(contains('Flats'), contains('End of night')));
    handle.dispose();
  });

  testWidgets('a plan on a rig with no filter wheel never stops to ask for a filter change', (tester) async {
    final (_, seq) = await run(tester);
    await create(tester);
    expect(seq.bodies, isNotEmpty);
    expect(seq.bodies.toString(), isNot(contains('WaitForUser')));
  });

  Future<void> imaging(WidgetTester tester, ProviderContainer c, _Seq seq, SequenceRunStateInfo state) async {
    seq.runState = state;
    await c.read(sequenceRunStateProvider.notifier).refresh();
    await tester.pumpAndSettle();
  }

  group('the Queue, through its own interface (no sheet)', () {
    Future<(ProviderContainer, _Seq)> queue({bool createFails = false, bool api = true, Locale? locale}) async {
      final seq = _Seq()..createFails = createFails;
      final c = ProviderContainer(overrides: [
        ..._rig.cast(),
        if (locale != null) appLocaleProvider.overrideWith(() => _Locale(locale)),
        draftSequenceServiceProvider.overrideWithValue(_MemDraftService()),
        tonightSkyProvider.overrideWith((ref) async => const <TonightSkyObject>[]),
        sequenceApiProvider.overrideWith((ref) => api ? seq : null),
        planetariumSelectionProvider.overrideWith(() => _Sel(_m31)),
        filterSetProvider.overrideWith(() => _Filters()),
        filterWheelProvider.overrideWith(() => _Wheel(true)),
      ]);
      addTearDown(c.dispose);
      c.listen(simpleRunProvider, (_, _) {});
      await c.read(tonightSkyProvider.future);
      await Future<void>.delayed(Duration.zero);
      return (c, seq);
    }

    test('every path that writes a Simple Mode target asks the Guider to calibrate (#1716)', () async {
      final (c, seq) = await queue();
      c.read(phd2SettingsProvider.notifier).setGuiderCamera('ASI120MM Mini');
      final n = c.read(simpleRunProvider.notifier);
      int asked(String field) => RegExp('"$field":true').allMatches(jsonEncode(seq.bodies.values.single)).length;
      void expectBlocks(int count, String path) {
        expect(asked('CalibrateEachNight'), count, reason: '$path: Start Guiding');
        expect(asked('RecalibrateGuider'), count, reason: '$path: the flip');
      }

      expect(await n.create(), isTrue);
      expectBlocks(1, 'create');
      final t = c.read(simpleRunProvider).targets.single;
      expect(await n.edit(0, t.steps), isTrue);
      expectBlocks(1, 'edit');
      expect(await n.restore(t, 0), isTrue);
      expectBlocks(2, 'restore');
    });

    test('editing a queued target keeps its After target flats on the rig and in the queue', () async {
      final (c, seq) = await queue();
      c.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.afterTarget);
      final n = c.read(simpleRunProvider.notifier);
      expect(await n.create(), isTrue);
      const l20 = [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 20)];
      expect(await n.edit(0, l20), isTrue);
      final block = childrenOf(seq.bodies.values.single).firstWhere((b) => b['Name'] == 'M31');
      expect([for (final k in childrenOf(block)) k[r'$type']], contains(flatPanelFlatsType));
      final t = c.read(simpleRunProvider).targets.single;
      expect(t.flats, SimpleTargetFlats.afterTarget);
      expect(t.steps.single.frameCount, 20);
    });

    test('editing an End of night target moves its filters in the Flats block at once', () async {
      final (c, seq) = await queue();
      c.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
      c.read(simpleRunPlanProvider.notifier).load(const [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)]);
      final n = c.read(simpleRunProvider.notifier);
      expect(await n.create(), isTrue);
      List<Object?> endFilters() {
        final end = childrenOf(seq.bodies.values.single).firstWhere((b) => b['Name'] == kEndFlatsBlockName);
        return [for (final k in childrenOf(end)) if (k[r'$type'] == switchFilterType) (k['Filter'] as Map)['_name']];
      }

      expect(endFilters(), ['L']);
      const ha = [FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 20)];
      expect(await n.edit(0, ha), isTrue);
      expect(endFilters(), ['Ha'], reason: 'no other target joined: the edit alone re-syncs the block');
    });

    test('removing the last End of night target takes its Flats block and the empty queue with it', () async {
      final (c, seq) = await queue();
      c.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
      c.read(simpleRunPlanProvider.notifier).load(const [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)]);
      final n = c.read(simpleRunProvider.notifier);
      expect(await n.create(), isTrue);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', kEndFlatsBlockName]);
      await n.remove(0);
      expect(c.read(simpleRunProvider).sequenceId, isNull, reason: 'no empty sequence left selected');
      expect(c.read(simpleRunProvider).targets, isEmpty);
      expect(seq.calls, contains('delete seq-1'));
    });

    test('Add to queue reads the picked target itself: one block on the rig, one Target queued', () async {
      final (c, seq) = await queue();
      expect(await c.read(simpleRunProvider.notifier).create(), isTrue);
      expect(seq.calls, ['create M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31']);
      expect(c.read(simpleRunProvider).error, isNull);
      expect(c.read(simpleMessagesProvider), isEmpty);
    });

    test('the link drops on Add: the create falls back to a local draft, refused with one pill', () async {
      final (c, _) = await queue(createFails: true);
      expect(await c.read(simpleRunProvider.notifier).create(), isFalse);
      expect(c.read(simpleMessagesProvider).map((m) => (m.kind, m.title)),
          [(SimpleMessageKind.error, _en.planRigUnreachable)]);
      expect(c.read(simpleRunProvider).error, isNull, reason: 'the pill is the one surface');
      expect(c.read(simpleRunProvider).targets, isEmpty);
    });

    test('no daemon at all: the create is only a local draft, refused with the same one pill', () async {
      final (c, _) = await queue(api: false);
      expect(await c.read(simpleRunProvider.notifier).create(), isFalse);
      expect(c.read(simpleMessagesProvider).map((m) => (m.kind, m.title)),
          [(SimpleMessageKind.error, _en.planRigUnreachable)]);
      expect(c.read(simpleRunProvider).error, isNull);
      expect(c.read(simpleRunProvider).targets, isEmpty);
    });

    test('Start now on a queued target whose move to the front fails: refused, nothing starts', () async {
      // Review of #1494: create(first:) returned state.error == null after reorder, and _fail
      // clears error, so a failed move reported success and started the run with the wrong
      // target first.
      final (c, seq) = await queue();
      final run = c.read(simpleRunProvider.notifier);
      await run.create();
      c.read(planetariumSelectionProvider.notifier).set(_m42);
      await run.create();
      c.read(planetariumSelectionProvider.notifier).set(_m31);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42']);
      c.read(planetariumSelectionProvider.notifier).set(_m42);
      seq.updatesBeforeFail = 0; // the move is a body update; the link drops
      expect(await run.createAndStart(), isFalse);
      expect(c.read(simpleMessagesProvider).single.title, startsWith(_en.errorCouldnt(_en.planActionReorder)));
      expect(seq.calls.where((call) => call.startsWith('start ')), isEmpty, reason: 'nothing starts');
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42'], reason: 'as the rig has it');
    });

    test('a failure reads whole in the app locale: pt-BR prefix and pt-BR action', () async {
      final (c, seq) = await queue(locale: const Locale('pt', 'BR'));
      await c.read(simpleRunProvider.notifier).create();
      seq.updatesBeforeFail = 0; // writing the start time fails, with no words from the daemon
      expect(
          await c.read(simpleRunProvider.notifier).startQueue(startAt: DateTime.now().add(const Duration(hours: 2))),
          isFalse);
      final pt = lookupAppLocalizations(const Locale('pt', 'BR'));
      expect(c.read(simpleMessagesProvider).single.title, startsWith(pt.errorCouldnt(pt.planActionSchedule)));
    });

    test('a pre-flight refusal (already queued) stays inline, no pill', () async {
      final (c, _) = await queue();
      await c.read(simpleRunProvider.notifier).create();
      expect(await c.read(simpleRunProvider.notifier).create(), isFalse);
      expect(c.read(simpleRunProvider).error, _en.planAlreadyQueued('M31'));
      expect(c.read(simpleMessagesProvider), isEmpty);
    });
  });

  group('the sheet', () {
    testWidgets('with nothing picked: search or Pick in Sky; both buttons are off', (tester) async {
      var showSky = 0, pick = 0;
      final c = await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () => showSky++, onPickInSky: () => pick++, onCreated: () {}),
          sel: null);
      expect(find.text('Search, or pick in the sky.'), findsOneWidget);
      expect(tester.widget<ButtonStyleButton>(find.byKey(const Key('simple-run-queue'))).onPressed, isNull);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNull);
      await tester.tap(find.byKey(const Key('simple-run-pick-in-sky')));
      expect(pick, 1);
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'M42');
      await tester.testTextInput.receiveAction(TextInputAction.search);
      await tester.pump();
      expect(showSky, 1, reason: 'the search runs in the planetarium');
      expect(c.read(planetariumSearchProvider), 'M42');
    });

    testWidgets('Add to queue is a solid button (button-primary, white words), not an outline', (tester) async {
      // User, 2026-11-04: the outline read too faintly beside the green Create and start.
      await _pump(tester, _Seq(), SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      final button = tester.widget<FilledButton>(find.byKey(const Key('simple-run-queue')));
      expect(button.style?.backgroundColor?.resolve({}), AraColors.buttonPrimary);
      expect(button.style?.foregroundColor?.resolve({}), AraColors.selectionFg);
    });

    testWidgets("the queue panel's Add target is solid too (button-primary, white words)", (tester) async {
      await _pump(tester, _Seq(), SimpleRunPanel(onAdd: () {}, onEdit: (_) {}));
      final button = tester.widget<FilledButton>(find.byKey(const Key('simple-run-add')));
      expect(button.style?.backgroundColor?.resolve({}), AraColors.buttonPrimary);
      expect(button.style?.foregroundColor?.resolve({}), AraColors.selectionFg);
    });

    testWidgets('a picked filter chip reads white on the solid selection blue, not dark on a dim tint', (tester) async {
      await _pump(tester, _Seq(), SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      final chip = tester.widget<FilterChip>(find.byKey(const Key('simple-run-filter-L')));
      expect(chip.selected, isTrue, reason: 'precondition: L is in the plan');
      expect(chip.selectedColor, AraColors.selectionBg);
      expect((chip.label as Text).style?.color, AraColors.selectionFg);
    });

    testWidgets('the filters are chips: a tap adds a row, another tap takes it out; the last one stays',
        (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      List<String> rows() => [for (final s in c.read(simpleRunPlanProvider)) s.filterName];
      final before = rows();
      final f = before.last;
      await tester.tap(find.byKey(Key('simple-run-filter-$f')));
      await tester.pump();
      expect(rows(), [...before]..remove(f));
      await tester.tap(find.byKey(Key('simple-run-filter-$f')));
      await tester.pump();
      expect(rows(), before);
      expect(find.text('Add filter'), findsNothing, reason: 'the chips replace the menu');
      for (final f in [...before]) {
        await tester.tap(find.byKey(Key('simple-run-filter-$f')));
        await tester.pump();
      }
      expect(rows(), hasLength(1), reason: 'a run needs something to take');
    });

    testWidgets('Frames opens a wheel; spinning it and Done sets the count', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      await tester.tap(find.byKey(const Key('simple-run-frames-0')));
      await tester.pumpAndSettle();
      final wheel = find.byType(CupertinoPicker);
      expect(wheel, findsOneWidget);
      tester.widget<CupertinoPicker>(wheel).onSelectedItemChanged!(0); // 5 frames
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();
      expect(c.read(simpleRunPlanProvider).first.frameCount, 5);
      expect(wheel, findsNothing);
    });

    testWidgets('a keyboard reaches Frames and Enter opens its wheel (WCAG 2.1.1)', (tester) async {
      await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      expect(await _tabTo(tester, const Key('simple-run-frames-0')), isTrue, reason: 'Tab reaches it like the other tiles');
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(find.byType(CupertinoPicker), findsOneWidget);
    });

    testWidgets('the Frames wheel turns with the arrow keys: Enter opens it focused, Down is the next count', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      final before = c.read(simpleRunPlanProvider).first.frameCount;
      expect(await _tabTo(tester, const Key('simple-run-frames-0')), isTrue);
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(FocusManager.instance.primaryFocus?.debugLabel, 'simple-wheel-Frames', reason: 'focus lands on the wheel');
      await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Done'));
      await tester.pumpAndSettle();
      expect(c.read(simpleRunPlanProvider).first.frameCount, greaterThan(before));
    });

    testWidgets('a plan starts from the camera\'s gain and keeps its own; the run shoots at the plan\'s', (tester) async {
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          overrides: [exposureControllerProvider.overrideWith(_Gain150.new)]);
      final slider = find.byKey(const Key('simple-run-gain'));
      expect(tester.widget<Slider>(slider).value, 150, reason: 'seeded from the camera when the sheet opens');
      tester.widget<Slider>(slider).onChanged!(200);
      tester.widget<Slider>(slider).onChangeEnd!(200);
      await tester.pump();
      expect(c.read(exposureControllerProvider).gain, 150, reason: 'the camera keeps its gain');
      await tester.tap(find.byKey(const Key('simple-run-queue')));
      await tester.pumpAndSettle();
      expect(jsonEncode(seq.bodies['seq-1']), contains('"Gain":200'));
      expect(jsonEncode(seq.bodies['seq-1']), isNot(contains('"Gain":150')));
      expect(c.read(simpleRunProvider).targets.single.gain, 200, reason: 'kept with the target, for an edit');
    });

    testWidgets('typing offers matching objects as you go; tapping one picks it and the sheet stays', (tester) async {
      var showSky = 0;
      final c = await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () => showSky++, onPickInSky: () {}, onCreated: () {}),
          sel: null,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => const [
                  PlanningDso(id: 'NGC0224', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.7, decDeg: 41.3, aliases: ['M 31']),
                  PlanningDso(id: 'NGC0598', name: 'Triangulum Galaxy', type: 'G', magnitude: 5.7, raDeg: 23.5, decDeg: 30.7, aliases: ['M 33']),
                ]),
          ]);
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'm3');
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('simple-run-suggestion-0')), findsOneWidget);
      expect(find.textContaining('M 31'), findsOneWidget);
      expect(find.textContaining('Andromeda Galaxy'), findsOneWidget, reason: 'the common name beside the designation');
      expect(tester.getSize(find.byKey(const Key('simple-run-suggestion-0'))).height, greaterThanOrEqualTo(44));
      FocusNode field() => tester
          .widget<EditableText>(find.descendant(of: find.byKey(const Key('simple-run-search')), matching: find.byType(EditableText)))
          .focusNode;
      expect(field().hasFocus, isTrue, reason: 'precondition: typing in it');
      await tester.tap(find.textContaining('M 31'));
      await tester.pump();
      expect(field().hasFocus, isFalse, reason: 'on an iPad the keyboard would stay over the sheet');
      expect(c.read(planetariumSelectionProvider)?.name, 'Andromeda Galaxy', reason: 'the pick is the target at once');
      expect(c.read(planetariumSearchProvider), isNull, reason: 'nothing to look up');
      expect(showSky, 0, reason: 'the Plan sheet stays up');
      expect(find.byKey(const Key('simple-run-suggestion-0')), findsNothing, reason: 'the list closes on a pick');
    });

    testWidgets('the Skull Nebula shows as "NGC 246 (Skull Nebula)" in the suggestion row and the plan header', (tester) async {
      await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          sel: null,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => const [
                  PlanningDso(id: 'NGC0246', name: 'Skull Nebula', type: 'PN', magnitude: 8, raDeg: 11.76, decDeg: -11.87, aliases: ['C 056']),
                ]),
          ]);
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'skull');
      await tester.pump();
      await tester.pump();
      expect(find.descendant(of: find.byKey(const Key('simple-run-suggestion-0')), matching: find.text('NGC 246 (Skull Nebula)')),
          findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-run-suggestion-0')));
      await tester.pump();
      expect(find.text('NGC 246 (Skull Nebula)'), findsOneWidget, reason: 'the plan header, the list is closed');
    });

    testWidgets('the plan header keeps the designation the row was picked by: "M 31", not the row id NGC 224',
        (tester) async {
      await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          sel: null,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => const [
                  PlanningDso(id: 'NGC0224', name: 'Andromeda Galaxy', type: 'G', magnitude: 3.4, raDeg: 10.7, decDeg: 41.3, aliases: ['M 31']),
                  PlanningDso(id: 'NGC6334', name: "Cat's Paw Nebula", type: 'HII', magnitude: null, raDeg: 260.1, decDeg: -35.8, aliases: ['RCW 127']),
                ]),
          ]);
      for (final (query, title) in [('M 31', 'M 31 (Andromeda Galaxy)'), ('RCW 127', "RCW 127 (Cat's Paw Nebula)")]) {
        await tester.enterText(find.byKey(const Key('simple-run-search')), query);
        await tester.pump();
        await tester.pump();
        expect(find.descendant(of: find.byKey(const Key('simple-run-suggestion-0')), matching: find.text(title)), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-run-suggestion-0')));
        await tester.pump();
        expect(find.text(title), findsOneWidget, reason: 'the plan header reads as the row did');
      }
    });

    testWidgets('an Abell planetary heads the plan as "Abell 21 (Medusa Nebula)", not by its short alias A 21',
        (tester) async {
      const medusa = PlanningDso(
          id: 'Abell 21', name: 'Medusa Nebula', type: 'PN', magnitude: null, raDeg: 112.26, decDeg: 13.24,
          aliases: ['PN G205.1+14.2', 'A 21', 'PK 205+14 1', 'Sh 2-274']);
      await _pump(tester, _Seq(), SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          sel: simpleSelectionOfDso(medusa, const [], latitudeDeg: 51.477833));
      expect(find.text('Abell 21 (Medusa Nebula)'), findsOneWidget);
    });

    testWidgets('a nickname that matched stays in the row: "NGC 598 (Triangulum Pinwheel)"', (tester) async {
      await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          sel: null,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => const [
                  PlanningDso(
                      id: 'NGC0598', name: 'Triangulum Galaxy', type: 'G', magnitude: 5.7, raDeg: 23.46, decDeg: 30.66,
                      aliases: ['M 33', 'Triangulum Pinwheel']),
                ]),
          ]);
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'triangulum pinwheel');
      await tester.pump();
      await tester.pump();
      expect(
          find.descendant(
              of: find.byKey(const Key('simple-run-suggestion-0')), matching: find.text('NGC 598 (Triangulum Pinwheel)')),
          findsOneWidget,
          reason: 'what matched, so the row says why it is listed');
    });

    testWidgets('at twice the text size a search row and the plan header show their whole designation (name)',
        (tester) async {
      tester.platformDispatcher.textScaleFactorTestValue = 2;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      await _pump(
          tester,
          _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          sel: null,
          overrides: [
            bundledCatalogProvider.overrideWith((ref) async => const [
                  PlanningDso(
                      id: 'NGC0598', name: 'Triangulum Galaxy', type: 'G', magnitude: 5.7, raDeg: 23.46, decDeg: 30.66,
                      aliases: ['M 33', 'Triangulum Pinwheel']),
                ]),
          ]);
      void whole(String text) =>
          expect(tester.renderObject<RenderParagraph>(find.text(text)).didExceedMaxLines, isFalse, reason: '"$text" is cut off');
      await tester.enterText(find.byKey(const Key('simple-run-search')), 'triangulum pinwheel');
      await tester.pump();
      await tester.pump();
      whole('NGC 598 (Triangulum Pinwheel)');
      whole('mag 5.7');
      await tester.tap(find.byKey(const Key('simple-run-suggestion-0')));
      await tester.pump();
      whole('NGC 598 (Triangulum Galaxy)');
    });

    testWidgets('Add to queue creates the run without starting it and keeps the sheet up for the next pick; again it appends',
        (tester) async {
      var created = 0;
      final seq = _Seq();
      final c = await _pump(
          tester,
          seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () => created++));
      expect(find.text('M31'), findsOneWidget);
      expect(find.text('Add to queue'), findsOneWidget);
      expect(find.text('Create and start'), findsOneWidget);
      await create(tester);
      expect(seq.calls, ['create M31']);
      expect(created, 0, reason: 'the sheet stays up while planning');
      expect(find.text('M31 added · 1 in the queue'), findsOneWidget);
      expect(c.read(planetariumSelectionProvider), isNull, reason: 'cleared for the next pick');
      c.read(planetariumSelectionProvider.notifier).set(_m42);
      await tester.pumpAndSettle();
      expect(find.text('M31 added · 1 in the queue'), findsNothing, reason: 'a new pick clears the line');
      await create(tester);
      expect(seq.calls, ['create M31', 'update seq-1'], reason: 'the queue grows; nothing is replaced');
      // Start now begins imaging: that closes it.
      c.read(planetariumSelectionProvider.notifier).set(_m31);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(c.read(simpleRunProvider).busy, isFalse, reason: 'an append ends the busy state');
      expect(created, 1);
    });

    testWidgets('a target already in the queue is not queued twice; Start now moves it to the front instead',
        (tester) async {
      // The rig finds a target's block by its name: a second M31 made removing or editing either hit the first.
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      await create(tester);
      c.read(planetariumSelectionProvider.notifier).set(_m42);
      await tester.pumpAndSettle();
      await create(tester);
      c.read(planetariumSelectionProvider.notifier).set(_m31);
      await tester.pumpAndSettle();
      await create(tester);
      expect(seq.calls, ['create M31', 'update seq-1'], reason: 'no second M31 on the rig');
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42']);
      expect(find.text('M31 is already in the queue.'), findsOneWidget);
      c.read(planetariumSelectionProvider.notifier).set(_m42);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M42', 'M31'], reason: 'moved, not copied');
      expect(seq.targets('seq-1'), ['M42', 'M31']);
      expect(seq.calls.last, 'start seq-1');
    });

    testWidgets('Create and start creates the run and starts it, then closes the sheet', (tester) async {
      var created = 0;
      final seq = _Seq();
      await _pump(
          tester,
          seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () => created++));
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31', 'start seq-1']);
      expect(created, 1);
    });

    group('a scheduled start', () {
      // 20:00 UTC on Jan 14 at the Sydney site (07:00 on Jan 15 there): daylight, dark falls that evening.
      final nowUtc = DateTime.utc(2026, 1, 14, 20);
      const site = SiteSettings(latitudeDeg: -33.856784, longitudeDeg: 151.215297, twilightDefinition: TwilightDefinition.astronomical);
      final dark = darkStart(site, nowUtc)!.toLocal();
      String hhmm(DateTime t) => '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';
      final overrides = <Object>[
        siteSettingsProvider.overrideWith(() => _SiteAt(site)),
        clockProvider.overrideWithValue(() => nowUtc.toLocal()),
      ];
      String go(WidgetTester tester) =>
          (tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).child! as Text).data!;

      testWidgets('before dark the green button starts at dark; the plan waits on the rig, cooling 15 min early',
          (tester) async {
        final seq = _Seq();
        await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        expect(go(tester), 'Start at ${hhmm(dark)}');
        expect(find.text('Starts at dark (${hhmm(dark)})'), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-run-start')));
        await tester.pumpAndSettle();
        expect(seq.calls, ['create M31', 'update seq-1', 'start seq-1']);
        final first = childrenOf(seq.bodies['seq-1']!).first;
        expect(first[r'$type'], waitForTimeType);
        final lead = dark.subtract(const Duration(minutes: 15));
        expect((first['Hours'], first['Minutes']), (lead.hour, lead.minute), reason: 'cooling starts 15 min early');
      });

      testWidgets('Start now instead: today\'s Create and start; Wait for dark brings it back; Pick a time shows '
          'the wheel', (tester) async {
        final seq = _Seq();
        await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        await tester.tap(find.byKey(const Key('simple-run-start-now')));
        await tester.pump();
        expect(go(tester), 'Create and start');
        await tester.tap(find.byKey(const Key('simple-run-wait-dark')));
        await tester.pump();
        expect(go(tester), 'Start at ${hhmm(dark)}');
        await tester.tap(find.byKey(const Key('simple-run-pick-time')));
        await tester.pump();
        expect(find.byKey(const Key('simple-run-time-picker')), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-run-start-now')));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const Key('simple-run-start')));
        await tester.pumpAndSettle();
        expect(seq.calls, ['create M31', 'start seq-1'], reason: 'starting now leaves the plan as it was');
      });

      // #1560 — what the rig gets: the dawn conditions and the park.
      List<String> dawns(Map<String, dynamic> body) => [
            for (final c in childrenOf(body))
              for (final n in [c, ...childrenOf(c)])
                for (final k in conditionsOf(n))
                  if (k[r'$type'] == timeConditionType) (k['SelectedProvider'] as Map)[r'$type'] as String,
          ];
      bool parks(Map<String, dynamic> body) => childrenOf(body).any((c) => c[r'$type'] == parkScopeType);

      testWidgets('waiting for dark: the plan stops at dawn and parks; the sheet says when', (tester) async {
        final dawn = nightEnd(site, dark.toUtc())!.toLocal();
        final seq = _Seq();
        await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        expect(find.text('Stops at dawn · ${hhmm(dawn)}'), findsOneWidget);
        await tester.tap(find.byKey(const Key('simple-run-start')));
        await tester.pumpAndSettle();
        final body = seq.bodies['seq-1']!;
        expect(parks(body), isTrue);
        // The target block and its four LRGB loops.
        expect(dawns(body), List.filled(5, timeProviders['Astronomical Dawn']));
      });

      testWidgets('Start now in daylight: it parks at the end but has no dawn stop (an indoor test runs on)',
          (tester) async {
        final seq = _Seq();
        await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        await tester.tap(find.byKey(const Key('simple-run-start-now')));
        await tester.pump();
        expect(find.textContaining('Stops at dawn'), findsNothing);
        await tester.tap(find.byKey(const Key('simple-run-start')));
        await tester.pumpAndSettle();
        final body = seq.bodies['seq-1']!;
        expect(parks(body), isTrue);
        expect(dawns(body), isEmpty);
      });

      testWidgets('Add to queue before dark: the queue stops at dawn (Preflight starts it at dark)',
          (tester) async {
        final seq = _Seq();
        await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        await tester.tap(find.byKey(const Key('simple-run-queue')));
        await tester.pumpAndSettle();
        final body = seq.bodies['seq-1']!;
        expect(parks(body), isTrue);
        expect(dawns(body), hasLength(5));
      });

      // Review of #1654: the daemon resolves a dawn condition against the dusk
      // rollover, so a block started after dawn and before sunset reads this
      // morning's dawn as passed and skips every target. The dawn stop is
      // therefore settled when the queue starts, and the sheet line says what
      // that start will do.
      testWidgets('a queue made before dark, started now in daylight: no dawn stop, and the sheet says none',
          (tester) async {
        final seq = _Seq();
        final c = await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        await tester.tap(find.byKey(const Key('simple-run-queue')));
        await tester.pumpAndSettle();
        expect(dawns(seq.bodies['seq-1']!), isNotEmpty, reason: 'queued for Preflight, which starts at dark');
        await tester.tap(find.byKey(const Key('simple-run-start-now')));
        await tester.pump();
        expect(find.textContaining('Stops at dawn'), findsNothing);
        expect(await c.read(simpleRunProvider.notifier).start(), isTrue);
        expect(dawns(seq.bodies['seq-1']!), isEmpty, reason: 'started in daylight: every target would be skipped');
        expect(parks(seq.bodies['seq-1']!), isTrue);
        expect(c.read(simpleRunProvider).dawnStop, isNull);
        expect(seq.calls.last, 'start seq-1');
      });

      testWidgets('a queue made after dawn, started in the dark: it stops at dawn, as the sheet says', (tester) async {
        // Queued at 05:00 in Sydney (after dawn, before sunrise): no dawn stop. Then night falls.
        var at = DateTime.utc(2026, 1, 14, 18);
        final seq = _Seq();
        final c = await _pump(tester, seq,
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: [siteSettingsProvider.overrideWith(() => _SiteAt(site)), clockProvider.overrideWithValue(() => at.toLocal())]);
        await tester.tap(find.byKey(const Key('simple-run-queue')));
        await tester.pumpAndSettle();
        expect(c.read(simpleRunProvider).dawnStop, isNull);
        at = DateTime.utc(2026, 1, 15, 11); // 22:00, dark
        c.read(planetariumSelectionProvider.notifier).set(_m42);
        await tester.pumpAndSettle();
        final dawn = nightEnd(site, at)!.toLocal();
        expect(find.text('Stops at dawn · ${hhmm(dawn)}'), findsOneWidget);
        expect(await c.read(simpleRunProvider.notifier).start(), isTrue);
        expect(dawns(seq.bodies['seq-1']!), List.filled(5, timeProviders['Astronomical Dawn']));
        expect(c.read(simpleRunProvider).dawnStop, 'Astronomical Dawn');
      });

      testWidgets('the start-time wheel takes the arrow keys: Down is 5 minutes later', (tester) async {
        await _pump(tester, _Seq(),
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: overrides);
        await tester.tap(find.byKey(const Key('simple-run-pick-time')));
        await tester.pump();
        expect(await _tabToLabel(tester, 'simple-run-time-wheel'), isTrue);
        // The wheel starts at dark on its 5-minute grid.
        final grid = dark.subtract(Duration(minutes: dark.minute % 5));
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
        await tester.pumpAndSettle();
        expect(go(tester), 'Start at ${hhmm(grid.add(const Duration(minutes: 5)))}');
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
        await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
        await tester.pumpAndSettle();
        expect(go(tester), 'Start at ${hhmm(grid.subtract(const Duration(minutes: 5)))}');
      });
    });

    testWidgets('from the night\'s end until sunrise: Start now only, no start time to pick', (tester) async {
      // 18:00 UTC Jan 14: 05:00 in Sydney, morning twilight before sunrise.
      final morning = DateTime.utc(2026, 1, 14, 18);
      final seq = _Seq();
      await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
          overrides: [
            siteSettingsProvider.overrideWith(() => _SiteAt(
                const SiteSettings(latitudeDeg: -33.856784, longitudeDeg: 151.215297, twilightDefinition: TwilightDefinition.astronomical))),
            clockProvider.overrideWithValue(() => morning.toLocal()),
          ]);
      expect((tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).child! as Text).data, 'Create and start');
      expect(find.byKey(const Key('simple-run-pick-time')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31', 'start seq-1'], reason: 'no waits the daemon would read as yesterday');
    });

    testWidgets('tonight\'s best: up to four suggestions, one tap picks the target', (tester) async {
      TonightSkyObject o(String name, double alt) => TonightSkyObject(
          id: name, name: name, type: 'Galaxy', magnitude: 5, raDeg: 10, decDeg: 40, altitudeDeg: alt, maxAltitudeDeg: 70);
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
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
      // The picked tile is blue: its words are white (4.6:1), and screen readers hear it's selected.
      final picked = find.byKey(const Key('simple-run-suggestion-NGC 7000'));
      for (final t in tester.widgetList<Text>(find.descendant(of: picked, matching: find.byType(Text)))) {
        expect(t.style?.color, AraColors.selectionFg, reason: t.data);
      }
      expect(tester.getSemantics(picked), isSemantics(isSelected: true));
      expect(tester.getSemantics(find.byKey(const Key('simple-run-suggestion-M31'))), isSemantics(isSelected: false));
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.calls, ['create NGC 7000', 'start seq-1']);
    });

    testWidgets('no ranking (no site yet): no suggestions, search stays', (tester) async {
      await _pump(tester, _Seq(), SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      expect(find.text("TONIGHT'S BEST"), findsNothing);
      expect(find.byKey(const Key('simple-run-search')), findsOneWidget);
    });

    testWidgets('a configured rig gets the suggested subs in one line; Use all takes them', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
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
            SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
            overrides: [
              nightPolarAlignStateProvider.overrideWithValue(aligned),
              simplePermanentPierPrefsProvider.overrideWithValue(_Prefs(permanent)),
            ]);
        expect(find.text('Not polar aligned tonight'), shown ? findsOneWidget : findsNothing);
        // A warning, not a gate.
        expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNotNull);
      });
    }

    testWidgets('an unconfigured rig has no hint; the last row stays', (tester) async {
      final c = await _pump(tester, _Seq(),
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
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
        SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
        filters: _duo);
    // .last: the row (the chips above name the filters too).
    _whole(tester, find.text('L-enhance').last);
    _whole(tester, find.text('L-eXtreme').last);
  });

  testWidgets('the plan square shows the catalog tag; the sheet keeps the whole name', (tester) async {
    const region = PlanetariumSelection(
        name: 'Horsehead + Flame region (IC 434)', kind: 'HII', raHours: 5.68, decDeg: -2.45, raJ2000Hours: 5.68,
        decJ2000Deg: -2.45, altDeg: 30, maxAltDeg: 50, designations: ['IC0434']);
    final (_, _) = await run(tester, sel: region);
    expect(find.text('Horsehead + Flame region (IC 434)'), findsWidgets, reason: 'the sheet');
    await create(tester);
    expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('IC 434')),
        findsOneWidget);
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

  testWidgets('a failed Stop says so in a pill, so it shows with the sheet and queue panel closed', (tester) async {
    final (c, seq) = await run(tester);
    await create(tester);
    seq.stopError = DioException(
      requestOptions: RequestOptions(),
      response: Response(requestOptions: RequestOptions(), statusCode: 409, data: {'detail': 'The run is between targets.'}),
    );
    await c.read(simpleRunProvider.notifier).stop();
    expect(c.read(simpleMessagesProvider).map((m) => (m.kind, m.title)),
        [(SimpleMessageKind.error, 'The run is between targets.')]);
  });

  testWidgets("a scheduled start the rig refuses says so in a pill too (Preflight's last Done has no sheet open)",
      (tester) async {
    final (c, seq) = await run(tester);
    await create(tester);
    seq.updatesBeforeFail = 0; // writing the start time fails
    final ok = await c.read(simpleRunProvider.notifier).startQueue(startAt: DateTime.now().add(const Duration(hours: 2)));
    expect(ok, isFalse);
    expect(c.read(simpleMessagesProvider).map((m) => m.kind), [SimpleMessageKind.error]);
    expect(seq.calls.where((call) => call.startsWith('start ')), isEmpty);
  });

  testWidgets("Preflight's last Done while the queue is still updating says so instead of doing nothing",
      (tester) async {
    // Review of #1486: _op returned quietly while busy, so startQueue reported true and nothing started.
    final (c, seq) = await run(tester);
    await create(tester);
    c.read(planetariumSelectionProvider.notifier).set(_m42);
    await tester.pumpAndSettle();
    await create(tester);
    seq.updateGate = Completer<void>();
    final n = c.read(simpleRunProvider.notifier);
    final moving = n.reorder(0, 1);
    final ok = await n.startQueue();
    expect(ok, isFalse);
    expect(c.read(simpleMessagesProvider).single.title, 'The queue is still updating on the rig. Try again in a moment.');
    expect(seq.calls.where((call) => call.startsWith('start ')), isEmpty);
    seq.updateGate!.complete();
    await moving;
  });

  testWidgets('a second Start while the first is on its way is not sent', (tester) async {
    final (c, seq) = await run(tester);
    await create(tester);
    seq.startGate = Completer<void>();
    final n = c.read(simpleRunProvider.notifier);
    final first = n.start();
    final second = n.start();
    seq.startGate!.complete();
    await Future.wait([first, second]);
    expect(seq.calls.where((call) => call.startsWith('start ')), hasLength(1));
    expect(c.read(simpleRunProvider).busy, isFalse);
  });

  testWidgets('no storage disk: the daemon\'s words and Open Storage, which asks the shell for Storage', (tester) async {
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
        SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
        overrides: [simpleModePrefsProvider.overrideWithValue(prefs)]);
    await tester.tap(find.byKey(const Key('simple-run-start')));
    await tester.pumpAndSettle();
    expect(c.read(simpleMessagesProvider).single.title, 'There is no disk to save frames to.',
        reason: "the daemon's words, as a pill (the sheet may be closed)");
    expect(c.read(simpleRunProvider).error, isNull);
    await tester.tap(find.text('Open Storage'));
    await tester.pumpAndSettle();
    expect(c.read(selectedSettingsPanelProvider), 'session.storage');
    expect(c.read(selectedTabIndexProvider), kOptionsTabIndex);
    expect(prefs.value, isTrue, reason: 'Settings opens over Simple Mode (the shell shows it)');
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
    expect(find.text('Image Library'), findsNothing, reason: 'the rail already has Library');
  });

  testWidgets('Night done: a plan that dawn ended says so (#1560)', (tester) async {
    const sydney = SiteSettings(
        latitudeDeg: -33.856784, longitudeDeg: 151.215297, twilightDefinition: TwilightDefinition.astronomical);
    // 07:00 in Sydney: queued before dark, so the queue stops at dawn.
    final morning = DateTime.utc(2026, 1, 14, 20);
    final dawn = nightEnd(sydney, darkStart(sydney, morning)!)!;
    final (c, seq) = await run(tester, overrides: [
      siteSettingsProvider.overrideWith(() => _SiteAt(sydney)),
      clockProvider.overrideWithValue(() => morning.toLocal()),
    ]);
    // Five-minute subs, more than the night holds.
    c.read(simpleRunPlanProvider.notifier).load(const [FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 200)]);
    await tester.pump();
    await create(tester);
    expect(c.read(simpleRunProvider).dawnStop, 'Astronomical Dawn');
    // Started at once, in daylight, waiting on the rig for dark.
    expect(await c.read(simpleRunProvider.notifier).startQueue(startAt: darkStart(sydney, morning)!.toLocal()), isTrue);
    expect(c.read(simpleRunProvider).dawnStop, 'Astronomical Dawn', reason: 'it images from dark');
    SequenceRunStateInfo ended(DateTime end) => SequenceRunStateInfo(
        sequenceId: 'seq-1',
        state: SequenceRunState.completed,
        framesCaptured: 20,
        startedUtc: morning,
        completedUtc: end);
    // The daemon starts no frame that would end after dawn, so a run dawn cut
    // short completes before it: up to one sub early (plus the park).
    await imaging(tester, c, seq, ended(dawn.subtract(const Duration(minutes: 3))));
    expect(find.textContaining('stopped at dawn'), findsOneWidget, reason: 'frames short, ended within a sub of dawn');
    await imaging(tester, c, seq, ended(dawn.add(const Duration(minutes: 4))));
    expect(find.textContaining('stopped at dawn'), findsOneWidget, reason: 'a frame interrupted at dawn');
    await imaging(tester, c, seq, ended(dawn.subtract(const Duration(hours: 1))));
    expect(find.text('Night done'), findsOneWidget);
    expect(find.textContaining('stopped at dawn'), findsNothing, reason: 'the frames ran out first');
    final planned = c.read(simpleRunProvider).steps.fold(0, (n, s) => n + s.frameCount);
    await imaging(tester, c, seq, SequenceRunStateInfo(
        sequenceId: 'seq-1', state: SequenceRunState.completed, framesCaptured: planned,
        startedUtc: morning, completedUtc: dawn.subtract(const Duration(minutes: 1))));
    expect(find.textContaining('stopped at dawn'), findsNothing, reason: 'every frame was taken, just before dawn');
  });

  testWidgets('the last plan square says the mount parks at the end (#1560)', (tester) async {
    final (c, _) = await run(tester);
    await create(tester);
    final tracker = find.byKey(const Key('simple-run-tracker'));
    expect(find.descendant(of: tracker, matching: find.text('Parks')), findsOneWidget);
    c.read(planetariumSelectionProvider.notifier).set(_m42);
    await tester.pumpAndSettle();
    await create(tester);
    // The queue runs right to left: the caption sits on M42's square, the leftmost.
    final caption = tester.getRect(find.descendant(of: tracker, matching: find.text('Parks')));
    expect(find.descendant(of: tracker, matching: find.text('Parks')), findsOneWidget);
    expect(caption.center.dx, closeTo(tester.getRect(find.descendant(of: tracker, matching: find.text('M42'))).center.dx, 1));
  });

  test('progress fills the queue in order, target by target', () {
    const a = SimpleTarget(name: 'M31', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)]);
    const b = SimpleTarget(name: 'M42', steps: [
      FilterPlanStep(filterName: 'Ha', exposureSeconds: 180, frameCount: 4),
      FilterPlanStep(filterName: 'OIII', exposureSeconds: 180, frameCount: 4),
    ]);
    expect(simpleTargetProgress([a, b], 12), [
      [10],
      [2, 0],
    ]);
    expect(simpleCurrentTarget([a, b], 9), 0);
    expect(simpleCurrentTarget([a, b], 10), 1);
    expect(simpleCurrentTarget([a, b], 99), 1, reason: 'the last once all are full');
  });

  test('After target flats count in the plan squares of their target: the next target waits for them', () {
    const a = SimpleTarget(
      name: 'M31',
      flats: SimpleTargetFlats.afterTarget,
      steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)],
    );
    const b = SimpleTarget(name: 'M42', steps: [FilterPlanStep(filterName: 'L', exposureSeconds: 60, frameCount: 10)]);
    // The daemon counts every frame of the session, flats included.
    expect(simpleCurrentTarget([a, b], 10 + 24), 0, reason: 'A is still shooting its flats');
    expect(simpleCurrentTarget([a, b], 10 + kSimpleFlatFrames), 1);
    expect(simpleTargetProgress([a, b], 10 + kSimpleFlatFrames + 3), [
      [10],
      [3],
    ], reason: 'the flats take their frames off the count, B starts after them');
    expect(simpleTargetProgress([a, b], 10 + 25), [
      [10],
      [0],
    ], reason: 'B does not read done');
  });

  group('the queue', () {
    Future<void> pick(WidgetTester tester, ProviderContainer c, PlanetariumSelection s) async {
      c.read(planetariumSelectionProvider.notifier).set(s);
      await tester.pumpAndSettle();
    }

    testWidgets('Add to queue appends to the one sequence; the squares line up right to left', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      expect(seq.calls, ['create M31', 'update seq-1'], reason: 'appended, nothing deleted');
      expect(seq.targets('seq-1'), ['M31', 'M42']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42']);
      final tracker = find.byKey(const Key('simple-run-tracker'));
      final m31 = tester.getRect(find.descendant(of: tracker, matching: find.text('M31')));
      final m42 = tester.getRect(find.descendant(of: tracker, matching: find.text('M42')));
      expect(m42.right, lessThan(m31.left), reason: 'the next one to the left');
    });

    testWidgets('with a queue, Start now puts the target first and starts', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await pick(tester, c, _m42);
      expect(find.text('Start now'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-run-start')));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M42', 'M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M42', 'M31']);
      expect(seq.calls.last, 'start seq-1');
    });

    testWidgets('the queue lives in its own panel, not the sheet: a tap edits a target, × deletes one', (tester) async {
      int? edited;
      final seq = _Seq();
      final c = await _pump(
          tester,
          seq,
          SingleChildScrollView(
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
              SimpleRunPanel(onAdd: () {}, onEdit: (i) => edited = i),
            ]),
          ));
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      expect(find.descendant(of: find.byType(SimpleRunSheet), matching: find.text('QUEUE')), findsNothing,
          reason: 'the sheet only plans the next target');
      final panel = find.byType(SimpleRunPanel);
      expect(find.descendant(of: panel, matching: find.textContaining('QUEUE')), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-run-edit-1')));
      expect(edited, 1);
      await tester.tap(find.byKey(const Key('simple-run-remove-1')));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31']);
      await tester.pump(kSimpleResultLife + const Duration(seconds: 1)); // the Undo pill fades
    });

    Future<(ProviderContainer, _Seq)> queueOfThree(WidgetTester tester) async {
      final seq = _Seq();
      final c = await _pump(
          tester,
          seq,
          SingleChildScrollView(
            child: Column(mainAxisSize: MainAxisSize.min, children: [
              SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
              SimpleRunPanel(onAdd: () {}, onEdit: (_) {}),
            ]),
          ));
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      await pick(tester, c, _m45);
      await create(tester);
      expect(seq.targets('seq-1'), ['M31', 'M42', 'M45']);
      return (c, seq);
    }

    testWidgets('on iOS a long press on a queued target lists Edit and Remove, and Remove removes it',
        variant: TargetPlatformVariant.only(TargetPlatform.iOS), (tester) async {
      // The queue's own narrow column, as the shell draws it.
      final seq = _Seq();
      final c = await _pump(
          tester,
          seq,
          Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 360,
              child: SingleChildScrollView(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
                  SimpleRunPanel(onAdd: () {}, onEdit: (_) {}),
                ]),
              ),
            ),
          ));
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      await pick(tester, c, _m45);
      await create(tester);
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-run-edit-1'))));
      for (var i = 0; i < 60; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
      await g.up();
      await tester.pumpAndSettle();
      expect(find.text('Edit M42'), findsOneWidget);
      await tester.tap(find.text('Remove M42 from the queue'));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31', 'M45']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M45']);
      await tester.pump(kSimpleResultLife + const Duration(seconds: 1)); // the Undo pill fades
    });

    // The queue's own narrow column on iOS, with M31, M42 and (when asked) the end-of-night Calibration block.
    Future<ProviderContainer> iosQueue(WidgetTester tester, {bool endFlats = false}) async {
      final c = await _pump(
          tester,
          _Seq(),
          Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: 360,
              child: SingleChildScrollView(
                child: Column(mainAxisSize: MainAxisSize.min, children: [
                  SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}),
                  SimpleRunPanel(onAdd: () {}, onEdit: (_) {}),
                ]),
              ),
            ),
          ));
      await create(tester);
      await pick(tester, c, _m42);
      if (endFlats) c.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
      await create(tester);
      return c;
    }

    Future<void> longPress(WidgetTester tester, Finder row) async {
      final g = await tester.startGesture(tester.getCenter(row));
      for (var i = 0; i < 60; i++) {
        await tester.pump(const Duration(milliseconds: 50));
      }
      await g.up();
      await tester.pumpAndSettle();
    }

    testWidgets('on iOS a long press on the Calibration block lists Remove and no Edit',
        variant: TargetPlatformVariant.only(TargetPlatform.iOS), (tester) async {
      final c = await iosQueue(tester, endFlats: true);
      expect(c.read(simpleRunProvider).targets.last.name, kEndFlatsBlockName);
      await tester.ensureVisible(find.byKey(const Key('simple-run-edit-2')));
      await tester.pumpAndSettle();
      await longPress(tester, find.byKey(const Key('simple-run-edit-2')));
      expect(find.byType(CupertinoContextMenuAction), findsOneWidget);
      expect(find.textContaining('Remove'), findsOneWidget);
      expect(find.textContaining('Edit'), findsNothing);
    });

    testWidgets('on iOS the lifted queue row is as wide as the row in its 360 px column',
        variant: TargetPlatformVariant.only(TargetPlatform.iOS), (tester) async {
      await iosQueue(tester);
      final row = find.byKey(const Key('simple-run-edit-1'));
      final inPage = tester.getSize(row).width;
      await longPress(tester, row);
      final widths = [for (final e in row.evaluate()) (e.renderObject! as RenderBox).size.width];
      expect(widths.length, 2, reason: 'the row in the page and its lifted preview');
      expect(widths, [inPage, inPage]);
    });

    testWidgets('on iOS the lifted queue row is only a preview: a tap on its × or a swipe removes nothing',
        variant: TargetPlatformVariant.only(TargetPlatform.iOS), (tester) async {
      final c = await iosQueue(tester);
      final row = find.byKey(const Key('simple-run-edit-1'));
      await longPress(tester, row);
      await tester.tap(find.byKey(const Key('simple-run-remove-1')).last, warnIfMissed: false);
      await tester.pumpAndSettle();
      await tester.drag(row.last, const Offset(-400, 0), warnIfMissed: false);
      await tester.pumpAndSettle();
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42']);
      expect(c.read(simpleMessagesProvider), isEmpty);
    });

    testWidgets('on iOS a menu left open on a row that is gone does nothing', variant: TargetPlatformVariant.only(TargetPlatform.iOS),
        (tester) async {
      final (c, seq) = await queueOfThree(tester);
      await longPress(tester, find.byKey(const Key('simple-run-edit-1')));
      // M42 goes another way (the × on another screen) while its menu is up.
      await c.read(simpleRunProvider.notifier).remove(1);
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31', 'M45']);
      await tester.tap(find.text('Remove M42 from the queue'));
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(find.byType(CupertinoContextMenuAction), findsNothing, reason: 'the menu closes');
      expect(seq.targets('seq-1'), ['M31', 'M45'], reason: 'not the row that took its place');
    });

    testWidgets('on iOS under Reduce Motion a sheet left open on a row that is gone does nothing',
        variant: TargetPlatformVariant.only(TargetPlatform.iOS), (tester) async {
      tester.platformDispatcher.accessibilityFeaturesTestValue = const FakeAccessibilityFeatures(reduceMotion: true);
      addTearDown(tester.platformDispatcher.clearAccessibilityFeaturesTestValue);
      final (c, seq) = await queueOfThree(tester);
      await longPress(tester, find.byKey(const Key('simple-run-edit-1')));
      expect(find.byType(CupertinoActionSheet), findsOneWidget);
      await c.read(simpleRunProvider.notifier).remove(1);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Remove M42 from the queue'));
      await tester.pumpAndSettle();
      expect(find.byType(CupertinoActionSheet), findsNothing);
      expect(seq.targets('seq-1'), ['M31', 'M45'], reason: 'not the row that took its place');
    });

    testWidgets('a swipe left removes a queued target; Undo puts it back where it was', (tester) async {
      final (c, seq) = await queueOfThree(tester);
      await tester.drag(find.byKey(const Key('simple-run-edit-0')), const Offset(-400, 0));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M42', 'M45']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M42', 'M45']);
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.title, 'M31 removed');
      expect(pill.actions.single.label, 'Undo');
      c.read(simpleMessagesProvider.notifier).dismiss(pill.id); // what the pill's Undo does first
      pill.actions.single.onPressed();
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31', 'M42', 'M45']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42', 'M45']);
    });

    testWidgets('Undo still puts the target back after the queue panel has closed', (tester) async {
      final (c, seq) = await queueOfThree(tester);
      await tester.drag(find.byKey(const Key('simple-run-edit-0')), const Offset(-400, 0));
      await tester.pumpAndSettle();
      final pill = c.read(simpleMessagesProvider).single;
      // The panel closes (another mode, the sheet shut); the Undo pill stays up for 6 s.
      await tester.pumpWidget(UncontrolledProviderScope(container: c, child: const MaterialApp(home: SizedBox())));
      pill.actions.single.onPressed();
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      expect(seq.targets('seq-1'), ['M31', 'M42', 'M45']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42', 'M45']);
      await tester.pump(kSimpleResultLife + const Duration(seconds: 1)); // the pills fade
    });

    testWidgets('a second reorder while the first is on its way is not sent', (tester) async {
      final (c, seq) = await queueOfThree(tester);
      seq.updateGate = Completer<void>();
      final before = seq.calls.length;
      final n = c.read(simpleRunProvider.notifier);
      final first = n.reorder(0, 2);
      final second = n.reorder(1, 0);
      seq.updateGate!.complete();
      await Future.wait([first, second]);
      await tester.pumpAndSettle();
      expect(seq.calls.skip(before).where((call) => call.startsWith('update')), hasLength(2),
          reason: "the first reorder's two moves only");
      expect(seq.targets('seq-1'), ['M42', 'M45', 'M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M42', 'M45', 'M31']);
    });

    testWidgets('queued targets reorder by dragging their handle; the rig follows', (tester) async {
      final (c, seq) = await queueOfThree(tester);
      expect(find.byKey(const Key('simple-run-drag-0')), findsOneWidget, reason: 'a visible handle');
      await c.read(simpleRunProvider.notifier).reorder(0, 2);
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M42', 'M45', 'M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M42', 'M45', 'M31']);
      await c.read(simpleRunProvider.notifier).reorder(2, 0);
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31', 'M42', 'M45']);
    });

    testWidgets('editing a queued target: the sheet edits its rows only; Save updates it in place', (tester) async {
      var closed = 0;
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      // The shell loads the target's rows, then opens the sheet on it.
      c.read(simpleRunPlanProvider.notifier).load(c.read(simpleRunProvider).targets[1].steps);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: MaterialApp(
            home: Scaffold(
                body: Center(
                    child: SimpleRunSheet(
                        editing: 1, onShowSky: () {}, onPickInSky: () {}, onCreated: () => closed++)))),
      ));
      await tester.pumpAndSettle();
      expect(find.text('Edit M42'), findsOneWidget);
      expect(find.byKey(const Key('simple-run-search')), findsNothing, reason: 'the target stays');
      expect(find.text('Add to queue'), findsNothing);
      c.read(simpleRunPlanProvider.notifier).setFrames(0, 7);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-run-save')));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31', 'M42'], reason: 'still second');
      expect(c.read(simpleRunProvider).targets[1].steps.first.frameCount, 7);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31', 'M42']);
      expect(closed, 1);
    });

    testWidgets('an edit the rig loses halfway shows the queue as the rig has it, and says so', (tester) async {
      // Review: edit removes the block, then adds it back; a drop in between left the screen showing a
      // target the rig no longer had, so it was never imaged.
      final seq = _Seq();
      final c = await _pump(tester, seq,
          SimpleRunSheet(onShowSky: () {}, onPickInSky: () {}, onCreated: () {}));
      await create(tester);
      await pick(tester, c, _m42);
      await create(tester);
      c.read(simpleRunPlanProvider.notifier).load(c.read(simpleRunProvider).targets[1].steps);
      await tester.pumpWidget(UncontrolledProviderScope(
        container: c,
        child: MaterialApp(
            home: Scaffold(
                body: Center(
                    child: SimpleRunSheet(
                        editing: 1, onShowSky: () {}, onPickInSky: () {}, onCreated: () {})))),
      ));
      await tester.pumpAndSettle();
      seq.updatesBeforeFail = 1; // the remove goes through; adding it back does not
      await tester.tap(find.byKey(const Key('simple-run-save')));
      await tester.pumpAndSettle();
      expect(seq.targets('seq-1'), ['M31']);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), ['M31'], reason: 'what the rig will image');
      expect(c.read(simpleMessagesProvider).map((m) => m.kind), [SimpleMessageKind.error], reason: 'says so, as a pill');
      expect(c.read(simpleRunProvider).error, isNull);
    });

    testWidgets('while imaging: the panel offers Add to queue; the sheet only adds', (tester) async {
      var adds = 0;
      final seq = _Seq();
      final c = await _pump(tester, seq, SimpleRunPanel(onAdd: () => adds++, onEdit: (_) {}));
      c.read(selectedSequenceIdProvider.notifier).select('seq-1');
      await imaging(tester, c, seq, _imaging(3));
      await tester.tap(find.byKey(const Key('simple-run-add')));
      expect(adds, 1);
    });
  });

  group('the progress panel', () {
    testWidgets('a panic stop reads Mount stopped while paused; resuming clears it', (tester) async {
      final ws = StreamController<WsEvent>.broadcast();
      addTearDown(ws.close);
      final seq = _Seq();
      final c = await _pump(tester, seq, SimpleRunPanel(onAdd: () {}, onEdit: (_) {}),
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

    group('each fact once (#1719)', () {
      Future<void> running(WidgetTester tester, List<SimpleTarget> targets) async {
        final seq = _Seq();
        final c = await _pump(tester, seq, SimpleRunPanel(onAdd: () {}, onEdit: (_) {}));
        c.read(selectedSequenceIdProvider.notifier).select('seq-1');
        c.read(simpleRunProvider.notifier).state = SimpleRun(sequenceId: 'seq-1', started: true, targets: targets);
        await imaging(tester, c, seq, _imaging(4));
      }

      const single = [FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30)];

      testWidgets('one target, one filter: the headline only, in the plan square\'s spelling', (tester) async {
        await running(tester, const [SimpleTarget(name: 'NGC0246', tag: 'NGC 246', steps: single)]);
        expect(find.textContaining('NGC 246'), findsOneWidget);
        expect(find.textContaining('NGC0246'), findsNothing);
        expect(find.textContaining('/ 30'), findsOneWidget, reason: 'the frame count shows once');
        expect(find.byType(LinearProgressIndicator), findsNothing, reason: 'one filter: no per-filter rows');
      });

      testWidgets('several filters get their rows, a long filter name on one line', (tester) async {
        await running(tester, const [
          SimpleTarget(name: 'M 31', steps: [
            FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 30),
            FilterPlanStep(filterName: 'UV/IR', exposureSeconds: 120, frameCount: 30),
          ]),
        ]);
        expect(find.byType(LinearProgressIndicator), findsNWidgets(2));
        expect(tester.renderObject<RenderParagraph>(find.descendant(of: find.text('UV/IR'), matching: find.byType(RichText))).didExceedMaxLines, isFalse);
      });

      testWidgets('several targets get the queue; one target does not', (tester) async {
        await running(tester, const [
          SimpleTarget(name: 'M 31', steps: single),
          SimpleTarget(name: 'M 33', steps: single),
        ]);
        expect(find.byKey(const ValueKey('simple-run-row-M 33')), findsOneWidget);
      });
    });

    testWidgets('paused: resume without re-centering, or re-center and refocus; imaging: neither', (tester) async {
      final seq = _Seq();
      final c = await _pump(tester, seq, SimpleRunPanel(onAdd: () {}, onEdit: (_) {}));
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
    testWidgets('nothing picked: no tracker, and the shutter does nothing but say why', (tester) async {
      final (_, seq) = await run(tester, sel: null);
      expect(find.byKey(const Key('simple-run-tracker')), findsNothing);
      expect(tester.getSemantics(find.byKey(const Key('simple-shutter'))), isSemantics(hasEnabledState: true, isEnabled: false),
          reason: 'announced as unavailable, not as a live button');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(seq.calls, isEmpty);
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      expect(c.read(simpleMessagesProvider).single.title, 'Pick a target first', reason: 'user, 2026-11-04');
      await tester.pump(const Duration(seconds: 11));
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
      expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('0 / 60')), findsOneWidget);
      expect(find.text('L · 5 s'), findsOneWidget);
      // A white ring, like every mode's; amber only while paused.
      expect(((tester.widget<Container>(find.byKey(const Key('simple-shutter'))).decoration as BoxDecoration).border
              as Border)
          .top
          .color, AraColors.textPrimary);
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
      final c = ProviderScope.containerOf(tester.element(find.byKey(const Key('simple-shutter'))));
      expect(c.read(simpleMessagesProvider).single.title, startsWith('Rises'), reason: 'and says so (user, 2026-11-04)');
      await tester.pump(const Duration(seconds: 11));
    });

    testWidgets('a target that never rises here can\'t be queued', (tester) async {
      const never = PlanetariumSelection(
          name: 'Omega Cen', kind: 'Globular', raHours: 13.4, decDeg: -47.5, raJ2000Hours: 13.4,
          decJ2000Deg: -47.5, altDeg: -30, maxAltDeg: -5);
      await run(tester, sel: never);
      expect(tester.widget<ButtonStyleButton>(find.byKey(const Key('simple-run-queue'))).onPressed, isNull);
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-run-start'))).onPressed, isNull);
    });

    testWidgets('imaging: a tap pauses (Pausing… until it lands), paused a tap resumes', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await imaging(tester, c, seq, _imaging(23));
      expect(find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('23 / 60')), findsOneWidget);
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

    testWidgets('while the pause is on its way the shutter is greyed out, and another tap says so (user, 2026-11-11)',
        (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await imaging(tester, c, seq, _imaging(23));
      Color ring() => ((tester.widget<Container>(find.byKey(const Key('simple-shutter'))).decoration! as BoxDecoration)
              .border! as Border)
          .top
          .color;
      final imagingRing = ring();
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(ring(), AraColors.textDisabled, reason: 'greyed out after the first pause (user, 2026-11-11)');
      expect(tester.widget<Icon>(find.descendant(of: find.byKey(const Key('simple-shutter')), matching: find.byType(Icon))).color,
          AraColors.textDisabled);
      expect(ring(), isNot(imagingRing));
      final calls = seq.calls.length;
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(seq.calls.length, calls, reason: 'nothing more is sent');
      expect(c.read(simpleMessagesProvider).map((m) => m.title), contains('Pausing after this frame'));
      await tester.pump(kSimpleResultLife);
    });

    testWidgets('a slowish tap (0.6 s: gloves, cold hands) still pauses', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      await imaging(tester, c, seq, _imaging(23));
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-shutter'))));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 600));
      await g.up();
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'pause seq-1');
    });

    testWidgets('the keyboard reaches the shutter: Enter starts, pauses and resumes the plan', (tester) async {
      final (c, seq) = await run(tester);
      await create(tester);
      await tabTo(tester, find.byKey(const Key('simple-shutter')));
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'start seq-1');
      await imaging(tester, c, seq, _imaging(23));
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'pause seq-1');
      await imaging(tester, c, seq, _imaging(24, state: SequenceRunState.paused));
      await tester.sendKeyEvent(LogicalKeyboardKey.space);
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'resume seq-1');
    });

    testWidgets('the keyboard reaches a plan square; Enter opens it', (tester) async {
      var opened = 0;
      final (_, _) = await run(tester, onTracker: () => opened++);
      await create(tester);
      await tabTo(tester, find.descendant(of: find.byKey(const Key('simple-run-tracker')), matching: find.text('M31')));
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(opened, 1);
    });

    testWidgets('a right-click does nothing; only a finger or the main button works the shutter', (tester) async {
      final (_, seq) = await run(tester);
      await create(tester);
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-shutter'))),
          kind: PointerDeviceKind.mouse, buttons: kSecondaryMouseButton);
      await g.up();
      await tester.pumpAndSettle();
      expect(seq.calls, ['create M31'], reason: 'not started');
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
