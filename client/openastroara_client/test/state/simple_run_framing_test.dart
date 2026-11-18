import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart'
    show centerAndRotateType, slewScopeToRaDecType;
import 'package:openastroara/models/sequence/nina_dom.dart' show childrenOf;
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/state/sky_atlas/plan_framing_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/util/input_coordinates.dart';

/// #1899 — a Target framed from Simple Mode (the planetarium page's framing
/// box, as `planFramingProvider` holds it) reaches the rig in the block the
/// Queue sends: Center and Rotate at the dialed angle on the framing's centre.
/// The seam is the run body: the block the Queue stores, not widget internals.

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

/// M31 framed off centre at 135° east of north (J2000 degrees).
const _m31Framed = PlanFraming(
  on: true,
  raDeg: 11.2,
  decDeg: 41.9,
  rotationDeg: 135,
  name: 'M31',
);

class _Sel extends PlanetariumSelectionNotifier {
  @override
  PlanetariumSelection? build() => null;
}

/// The rig as the Queue sees it: it keeps each sequence's body.
class _Seq implements SequenceClient {
  final bodies = <String, Map<String, dynamic>>{};
  var _n = 0;
  @override
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
    String? idempotencyKey,
  }) async {
    final id = 'seq-${++_n}';
    bodies[id] = body;
    return id;
  }

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, body: bodies[id] ?? const {});

  @override
  Future<SequenceDetail> updateSequence(
    String id, {
    String? name,
    String? description,
    Map<String, dynamic>? body,
  }) async {
    if (body != null) bodies[id] = body;
    return SequenceDetail(id: id, body: bodies[id] ?? const {});
  }

  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async =>
      const SequencePage(items: []);
  @override
  Future<bool> deleteSequence(String id) async => true;
  @override
  void close() {}
  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

/// A target block's go-to: the Center and Rotate, or the centring slew (never
/// the blind one a focus-first block adds).
Map<String, dynamic> _goTo(Map<String, dynamic> body, String target) {
  final block = childrenOf(body)[indexOfTargetBlock(body, target)];
  final found = <Map<String, dynamic>>[];
  void walk(Map<String, dynamic> n) {
    final t = n[r'$type'];
    if (t == centerAndRotateType ||
        (t == slewScopeToRaDecType && n['Center'] != false)) {
      found.add(n);
    }
    for (final c in childrenOf(n)) {
      walk(c);
    }
  }

  walk(block);
  expect(found, hasLength(1), reason: '$target has one go-to');
  return found.single;
}

void _expectFramed(Map<String, dynamic> goTo) {
  expect(goTo[r'$type'], centerAndRotateType);
  expect(goTo['PositionAngle'], 135);
  expect(goTo['Coordinates'], inputCoordinatesFromDeg(11.2, 41.9));
}

void _expectPlain(Map<String, dynamic> goTo, PlanetariumSelection sel) {
  expect(goTo[r'$type'], slewScopeToRaDecType);
  expect(goTo['Center'], true);
  expect(
    goTo['Coordinates'],
    inputCoordinatesFromDeg(sel.raJ2000Hours * 15, sel.decJ2000Deg),
  );
}

void main() {
  late _Seq seq;
  late ProviderContainer c;

  setUp(() {
    seq = _Seq();
    c = ProviderContainer(
      overrides: [
        sequenceApiProvider.overrideWith((ref) => seq),
        planetariumSelectionProvider.overrideWith(_Sel.new),
      ],
    );
    addTearDown(c.dispose);
    c.read(simpleRunPlanProvider.notifier).load(const [
      FilterPlanStep(filterName: '', exposureSeconds: 120, frameCount: 30),
    ]);
  });

  void pick(PlanetariumSelection s) =>
      c.read(planetariumSelectionProvider.notifier).set(s);
  void frame(PlanFraming f) => c.read(planFramingProvider.notifier).set(f);
  SimpleRunNotifier run() => c.read(simpleRunProvider.notifier);
  Map<String, dynamic> body() => seq.bodies['seq-1']!;

  group('a framed Target planned from Simple Mode', () {
    test('Add to queue sends Center and Rotate at the dialed angle on the framing centre', () async {
      pick(_m31);
      frame(_m31Framed);
      expect(await run().create(), isTrue);
      _expectFramed(_goTo(body(), 'M31'));
    });

    test('framing on at 0° and moved off centre is a plain centred go-to on the framing centre', () async {
      pick(_m31);
      frame(
        const PlanFraming(
          on: true,
          raDeg: 11.2,
          decDeg: 41.9,
          rotationDeg: 0,
          name: 'M31',
        ),
      );
      expect(await run().create(), isTrue);
      final g = _goTo(body(), 'M31');
      expect(g[r'$type'], slewScopeToRaDecType);
      expect(g['Center'], true);
      expect(g['Coordinates'], inputCoordinatesFromDeg(11.2, 41.9));
    });

    test(
      'framing off is a plain centred go-to on the catalogue centre',
      () async {
        pick(_m31);
        frame(PlanFraming.none);
        expect(await run().create(), isTrue);
        _expectPlain(_goTo(body(), 'M31'), _m31);
      },
    );

    test(
      'the framing of another Target does not reach a newly picked Target',
      () async {
        frame(_m31Framed);
        pick(_m42);
        expect(await run().create(), isTrue);
        _expectPlain(_goTo(body(), 'M42'), _m42);
      },
    );

    test('After target flats do not take the framing from the block', () async {
      c
          .read(simpleRunFlatsProvider.notifier)
          .set(SimpleTargetFlats.afterTarget);
      pick(_m31);
      frame(_m31Framed);
      expect(await run().create(), isTrue);
      _expectFramed(_goTo(body(), 'M31'));
    });

    test('Start now on a queued Target moves its framed block to the front, framing kept', () async {
      pick(_m31);
      frame(_m31Framed);
      await run().create();
      pick(_m42);
      frame(PlanFraming.none);
      await run().create();
      pick(_m31);
      frame(PlanFraming.none);
      expect(await run().create(first: true), isTrue);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), [
        'M31',
        'M42',
      ]);
      _expectFramed(_goTo(body(), 'M31'));
      _expectPlain(_goTo(body(), 'M42'), _m42);
    });

    test('re-ordering a queued Target keeps its framing', () async {
      pick(_m42);
      frame(PlanFraming.none);
      await run().create();
      pick(_m31);
      frame(_m31Framed);
      await run().create();
      expect(await run().reorder(1, 0), isTrue);
      expect(c.read(simpleRunProvider).targets.map((t) => t.name), [
        'M31',
        'M42',
      ]);
      _expectFramed(_goTo(body(), 'M31'));
    });

    test('editing a queued Target keeps its framing', () async {
      pick(_m31);
      frame(_m31Framed);
      await run().create();
      frame(PlanFraming.none);
      expect(
        await run().edit(0, const [
          FilterPlanStep(filterName: '', exposureSeconds: 60, frameCount: 10),
        ]),
        isTrue,
      );
      _expectFramed(_goTo(body(), 'M31'));
    });

    test(
      'Undo after a removal puts the Target back with its framing',
      () async {
        pick(_m31);
        frame(_m31Framed);
        await run().create();
        pick(_m42);
        frame(PlanFraming.none);
        await run().create();
        final gone = c.read(simpleRunProvider).targets.first;
        await run().remove(0);
        frame(PlanFraming.none);
        expect(await run().restore(gone, 0), isTrue);
        _expectFramed(_goTo(body(), 'M31'));
      },
    );
  });
}
