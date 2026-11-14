import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart'
    show EquipmentConnectionState;
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart'
    show timeConditionType, timeProviders;
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart'
    show flatPanelFlatsType, switchFilterType, takeExposureType;
import 'package:openastroara/models/sequence/nina_dom.dart'
    show childrenOf, conditionsOf, withChildren;
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

// #1875 slice 1: a queue rebuilt from the rig is written to again (Add, Edit,
// Undo) from a device that did not queue it. Each write re-sends what the
// block holds beyond its position: the dawn stop, the plan's gain, its After
// target flats and, for the end-of-night Flats block, the filters of every
// target set to End of night (ADR 0010, amended 2026-11-11). None of that may
// fall back to "none" or the profile's default because this device did not
// make the block.

const _dawn = 'Astronomical Dawn';

const _m31 = PlanetariumSelection(
  name: 'M31',
  kind: 'Galaxy',
  raHours: 0.73,
  decDeg: 41.4,
  raJ2000Hours: 0.71,
  decJ2000Deg: 41.27,
  altDeg: 62.4,
  maxAltDeg: 74.2,
);

const _m42 = PlanetariumSelection(
  name: 'M42',
  kind: 'Nebula',
  raHours: 5.59,
  decDeg: -5.39,
  raJ2000Hours: 5.59,
  decJ2000Deg: -5.39,
  altDeg: 30,
  maxAltDeg: 45,
);

const _m45 = PlanetariumSelection(
  name: 'M45',
  kind: 'Cluster',
  raHours: 3.79,
  decDeg: 24.1,
  raJ2000Hours: 3.79,
  decJ2000Deg: 24.1,
  altDeg: 40,
  maxAltDeg: 60,
);

const _lrgb = [
  PlanningFilter(name: 'L', kind: FilterKind.l),
  PlanningFilter(name: 'R', kind: FilterKind.r),
  PlanningFilter(name: 'G', kind: FilterKind.g),
  PlanningFilter(name: 'B', kind: FilterKind.b),
  PlanningFilter(name: 'Ha', kind: FilterKind.ha),
];

/// The queue device A made: M31 (imaging now, End of night flats, gain 200),
/// M42 (After target flats, gain 150), then the Flats block for M31's filters.
const _m31Rows = [
  FilterPlanStep(filterName: 'L', exposureSeconds: 120, frameCount: 10),
  FilterPlanStep(filterName: 'R', exposureSeconds: 120, frameCount: 10),
];
const _m42Rows = [
  FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 6),
];

/// What the daemon serves for that queue: the plan route's snapshot.
Map<String, dynamic> _plan(String id) => {
  'sequence_id': id,
  'run_id': 'R-1',
  'targets': [
    {
      'name': 'M31',
      'ra_deg': 0.71 * 15,
      'dec_deg': 41.27,
      'gain': 200,
      'flats': 'end_of_night',
      'dawn_provider': timeProviders[_dawn],
      'rows': [
        {
          'filter_name': 'L',
          'exposure_seconds': 120,
          'frames_total': 10,
          'frames_done': 4,
        },
        {
          'filter_name': 'R',
          'exposure_seconds': 120,
          'frames_total': 10,
          'frames_done': 0,
        },
      ],
    },
    {
      'name': 'M42',
      'ra_deg': 5.59 * 15,
      'dec_deg': -5.39,
      'gain': 150,
      'flats': 'after_target',
      'dawn_provider': timeProviders[_dawn],
      'rows': [
        {
          'filter_name': 'Ha',
          'exposure_seconds': 300,
          'frames_total': 6,
          'frames_done': 0,
        },
      ],
    },
    {
      'name': kEndFlatsBlockName,
      'calibration': true,
      'rows': [
        {
          'filter_name': 'L',
          'exposure_seconds': 1,
          'frames_total': kSimpleFlatFrames,
          'frames_done': 0,
        },
        {
          'filter_name': 'R',
          'exposure_seconds': 1,
          'frames_total': kSimpleFlatFrames,
          'frames_done': 0,
        },
      ],
    },
  ],
};

/// The daemon over HTTP, in memory: the stored bodies, the live run-item
/// edits on them, the run state once [active], and the plan route.
class _Daemon implements HttpClientAdapter {
  final requests = <String>[];
  final bodies = <String, Map<String, dynamic>>{};
  final added = <Map<String, dynamic>>[];
  bool active = false;
  var _n = 0;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions o,
    Stream<Uint8List>? body,
    Future<void>? cancel,
  ) async {
    requests.add('${o.method} ${o.path}');
    final data = o.data is Map ? (o.data as Map).cast<String, dynamic>() : null;
    final seg = o.path.split('/').skip(4).toList(); // after /api/v1/sequences
    ResponseBody ok(Object json) => ResponseBody.fromString(
      jsonEncode(json),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
    final missing = ResponseBody.fromString(
      '{"title":"Not Found","status":404}',
      404,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
    Map<String, dynamic> detail(String id) => {
      'id': id,
      'name': 'Queue',
      'body': bodies[id],
    };
    if (seg.isEmpty) {
      if (o.method == 'GET') return ok({'items': <Object>[]});
      final id = 'seq-${++_n}';
      bodies[id] = (data!['body'] as Map).cast<String, dynamic>();
      return ok({'id': id});
    }
    final id = seg[0];
    if (!bodies.containsKey(id)) return missing;
    final rest = seg.skip(1).join('/');
    switch ((o.method, rest)) {
      case ('GET', ''):
        return ok(detail(id));
      case ('PATCH', ''):
        bodies[id] = (data!['body'] as Map).cast<String, dynamic>();
        return ok(detail(id));
      case ('DELETE', ''):
        bodies.remove(id);
        return ResponseBody.fromString('', 204);
      case ('GET', 'state'):
        if (!active) return missing;
        return ok({'sequence_id': id, 'run_id': 'R-1', 'state': 'running'});
      case ('GET', 'plan'):
        return ok(_plan(id));
      case ('POST', 'run/items'):
        final item = (data!['item'] as Map).cast<String, dynamic>();
        added.add(item);
        final kids = [...childrenOf(bodies[id]!)];
        kids.insert((data['index'] as int?) ?? kids.length, item);
        bodies[id] = withChildren(bodies[id]!, kids);
        return ok(detail(id));
      case ('DELETE', 'run/items'):
        final at = int.parse('${o.queryParameters['path']}'.split(',').first);
        bodies[id] = withChildren(
          bodies[id]!,
          [...childrenOf(bodies[id]!)]..removeAt(at),
        );
        return ok(detail(id));
      case ('POST', 'run/items/move'):
        final from = (data!['path'] as List).first as int;
        final kids = [...childrenOf(bodies[id]!)];
        kids.insert(data['new_index'] as int, kids.removeAt(from));
        bodies[id] = withChildren(bodies[id]!, kids);
        return ok(detail(id));
    }
    return missing;
  }
}

class _Sel extends PlanetariumSelectionNotifier {
  @override
  PlanetariumSelection? build() => _m31;
}

class _Filters extends FilterSetNotifier {
  @override
  FilterSetSettings build() => const FilterSetSettings(filters: _lrgb);
}

class _Wheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus(
    deviceId: 'w',
    name: 'Wheel',
    connectionState: EquipmentConnectionState.connected,
    runtimeState: 'idle',
    currentSlot: 0,
    slots: const [],
  );
}

class _Optics extends OpticsSettingsNotifier {
  @override
  OpticsSettings build() => const OpticsSettings(
    focalLengthMm: 447,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 91,
  );
}

class _Electronics extends CameraElectronicsNotifier {
  @override
  CameraElectronics build() => const CameraElectronics(
    readNoiseE: 0.7,
    fullWellE: 50000,
    electronsPerAdu: 0.8,
    quantumEfficiencyPeak: 0.8,
  );
}

// The Sydney landmark site, at 07:00 local on Jan 15: a queue made now stops
// at astronomical dawn.
const _site = SiteSettings(
  latitudeDeg: -33.856784,
  longitudeDeg: 151.215297,
  twilightDefinition: TwilightDefinition.astronomical,
);

class _Site extends SiteSettingsNotifier {
  @override
  SiteSettings build() => _site;
}

class _NoSkyModel extends SkyModelCalibrationNotifier {
  @override
  Future<SkyModelCalibration?> build() async => null;
}

class _MemDrafts extends DraftSequenceService {
  final store = <String, DraftSequence>{};
  var _n = 0;
  @override
  String newId() => '${draftIdPrefix}mem-${_n++}';
  @override
  Future<List<DraftSequence>> loadAll() async => store.values.toList();
  @override
  Future<void> save(DraftSequence draft) async => store[draft.id] = draft;
  @override
  Future<void> delete(String id) async => store.remove(id);
}

/// The run as a device that did not queue it sees it: active on seq-1.
class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
    sequenceId: 'seq-1',
    runId: 'R-1',
    state: SequenceRunState.running,
    currentTargetName: 'M31',
    framesCaptured: 16,
  );
}

class _Idle extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
}

ProviderContainer _device(_Daemon daemon, {required bool running}) {
  final c = ProviderContainer(
    overrides: [
      sequenceApiProvider.overrideWith(
        (ref) => SequenceApi(
          const AraServer(hostname: '192.0.2.10', port: 1888),
          dio: Dio(BaseOptions(baseUrl: 'http://192.0.2.10:1888'))
            ..httpClientAdapter = daemon,
        ),
      ),
      sequenceRunStateProvider.overrideWith(running ? _Running.new : _Idle.new),
      planetariumSelectionProvider.overrideWith(_Sel.new),
      filterSetProvider.overrideWith(_Filters.new),
      filterWheelProvider.overrideWith(_Wheel.new),
      opticsSettingsProvider.overrideWith(_Optics.new),
      cameraElectronicsProvider.overrideWith(_Electronics.new),
      siteSettingsProvider.overrideWith(_Site.new),
      skyModelCalibrationProvider.overrideWith(_NoSkyModel.new),
      draftSequenceServiceProvider.overrideWithValue(_MemDrafts()),
      tonightSkyProvider.overrideWith(
        (ref) async => const <TonightSkyObject>[],
      ),
      clockProvider.overrideWithValue(
        () => DateTime.utc(2026, 1, 14, 20).toLocal(),
      ),
    ],
  );
  addTearDown(c.dispose);
  c.listen(simpleRunProvider, (_, _) {});
  return c;
}

/// Device A queues M31 and M42 (not started), then the run starts on the rig.
Future<_Daemon> _queuedOnA() async {
  final daemon = _Daemon();
  final a = _device(daemon, running: false);
  await a.read(tonightSkyProvider.future);
  await a.read(filterWheelProvider.future);
  final run = a.read(simpleRunProvider.notifier);

  a.read(simpleRunPlanProvider.notifier).load(_m31Rows);
  a.read(simpleRunGainProvider.notifier).set(200);
  a.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
  expect(await run.create(), isTrue);

  a.read(planetariumSelectionProvider.notifier).set(_m42);
  a.read(simpleRunPlanProvider.notifier).load(_m42Rows);
  a.read(simpleRunGainProvider.notifier).set(150);
  a.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.afterTarget);
  expect(await run.create(), isTrue);

  expect(a.read(simpleRunProvider).targets.map((t) => t.name), [
    'M31',
    'M42',
    kEndFlatsBlockName,
  ], reason: "device A's queue, as the served plan describes it");
  daemon.active = true;
  daemon.requests.clear();
  return daemon;
}

/// Device B: the run already active; waits for its queue to be rebuilt.
Future<ProviderContainer> _deviceB(_Daemon daemon) async {
  final b = _device(daemon, running: true);
  await b.read(sequenceRunStateProvider.future);
  await b.read(tonightSkyProvider.future);
  await b.read(filterWheelProvider.future);
  for (var i = 0; i < 50 && b.read(simpleRunProvider).targets.isEmpty; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
  return b;
}

Map<String, dynamic> _block(_Daemon d, String name) =>
    childrenOf(d.bodies['seq-1']!).firstWhere((b) => b['Name'] == name);

Iterable<Map<String, dynamic>> _tree(Map<String, dynamic> n) sync* {
  yield n;
  for (final k in childrenOf(n)) {
    yield* _tree(k);
  }
}

List<Object?> _lightGains(Map<String, dynamic> block) => [
  for (final n in _tree(block))
    if (n[r'$type'] == takeExposureType) n['Gain'],
];

bool _flatsAfter(Map<String, dynamic> block) =>
    _tree(block).any((n) => n[r'$type'] == flatPanelFlatsType);

List<Object?> _dawns(Map<String, dynamic> block) => [
  for (final k in conditionsOf(block))
    if (k[r'$type'] == timeConditionType)
      (k['SelectedProvider'] as Map)[r'$type'],
];

List<Object?> _endFlatsFilters(_Daemon d) => [
  for (final k in childrenOf(_block(d, kEndFlatsBlockName)))
    if (k[r'$type'] == switchFilterType) (k['Filter'] as Map)['_name'],
];

void main() {
  test('a rebuilt queue carries what its writes re-send: started, the dawn stop, each block\'s position, gain and flats', () async {
    final daemon = await _queuedOnA();
    final b = await _deviceB(daemon);

    final run = b.read(simpleRunProvider);
    expect(run.sequenceId, 'seq-1');
    expect(run.started, isTrue, reason: 'the run is active on the rig');
    expect(
      run.dawnStop,
      _dawn,
      reason: "the blocks' TimeCondition, as the rig holds it",
    );
    final [m31, m42, flats] = run.targets;
    expect(m31.raDeg, closeTo(10.65, 1e-9), reason: 'J2000, to slew again');
    expect(
      (m31.decDeg, m31.gain, m31.flats),
      (41.27, 200, SimpleTargetFlats.endOfNight),
    );
    expect(m42.raDeg, closeTo(83.85, 1e-9));
    expect(
      (m42.decDeg, m42.gain, m42.flats),
      (-5.39, 150, SimpleTargetFlats.afterTarget),
    );
    expect(
      (flats.name, flats.calibration),
      (kEndFlatsBlockName, true),
      reason: 'the Flats block keeps its square on every device',
    );
  });

  test('Add to queue from a device that did not queue it appends, and the Flats block keeps every End of night target\'s filters', () async {
    final daemon = await _queuedOnA();
    final b = await _deviceB(daemon);

    b.read(planetariumSelectionProvider.notifier).set(_m45);
    b.read(simpleRunPlanProvider.notifier).load(const [
      FilterPlanStep(filterName: 'G', exposureSeconds: 60, frameCount: 10),
    ]);
    b.read(simpleRunFlatsProvider.notifier).set(SimpleTargetFlats.endOfNight);
    expect(await b.read(simpleRunProvider.notifier).create(), isTrue);

    expect(
      daemon.requests,
      isNot(contains('POST /api/v1/sequences')),
      reason: 'joins the running queue; no second sequence',
    );
    expect(_dawns(_block(daemon, 'M45')), [timeProviders[_dawn]]);
    expect(
      _endFlatsFilters(daemon),
      ['L', 'R', 'G'],
      reason: "M31 is End of night too: its L and R stay in the Flats block (ADR 0010)",
    );
  });

  test('an edit from a device that did not queue it keeps the block\'s gain and After target flats', () async {
    final daemon = await _queuedOnA();
    final b = await _deviceB(daemon);
    final run = b.read(simpleRunProvider.notifier);
    final m42 = b.read(simpleRunProvider).targets[1];

    const ha8 = [
      FilterPlanStep(filterName: 'Ha', exposureSeconds: 300, frameCount: 8),
    ];
    // As the sheet does: its gain seeded from the target opened for the edit.
    expect(await run.edit(1, ha8, gain: m42.gain), isTrue);

    final block = _block(daemon, 'M42');
    expect(
      _lightGains(block),
      everyElement(150),
      reason: "the plan's own gain, not the profile's default",
    );
    expect(_flatsAfter(block), isTrue, reason: 'its After target flats stay');
    expect(_dawns(block), [timeProviders[_dawn]]);
    expect(b.read(simpleRunProvider).targets[1].steps.single.frameCount, 8);
  });

  test('Undo on a device that did not queue it puts the block back with its gain and After target flats', () async {
    final daemon = await _queuedOnA();
    final b = await _deviceB(daemon);
    final run = b.read(simpleRunProvider.notifier);
    final m42 = b.read(simpleRunProvider).targets[1];

    await run.remove(1);
    expect(
      daemon.requests,
      isNot(contains('DELETE /api/v1/sequences/seq-1')),
      reason: 'the running queue stays',
    );
    expect(await run.restore(m42, 1), isTrue);

    final block = _block(daemon, 'M42');
    expect(_lightGains(block), everyElement(150));
    expect(
      _flatsAfter(block),
      isTrue,
      reason: 'Undo re-sends the block\'s flats too',
    );
    expect(b.read(simpleRunProvider).targets.map((t) => t.name), [
      'M31',
      'M42',
      kEndFlatsBlockName,
    ]);
  });
}
