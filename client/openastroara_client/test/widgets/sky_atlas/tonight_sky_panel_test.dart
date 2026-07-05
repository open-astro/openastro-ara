import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/tonight_sky_panel.dart';

/// Records create()/updateSequence() calls; the append path additionally reads
/// [runState] (null = no active run) and [detail] (the open sequence's body).
class _RecordingClient implements SequenceClient {
  String? createdName;
  Map<String, dynamic>? createdBody;
  bool throwOnCreate = false;
  SequenceRunStateInfo? runState;
  SequenceDetail? detail;
  String? updatedId;
  Map<String, dynamic>? updatedBody;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description}) async {
    if (throwOnCreate) throw Exception('boom');
    createdName = name;
    createdBody = body;
    return 'seq-new';
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName} not stubbed');

  // The interface members the panel never calls — explicit overrides keep the
  // analyzer happy without `noSuchMethod` swallowing a real miss.
  @override
  Future<SequencePage> list({int limit = 50}) => throw UnimplementedError();
  @override
  Future<SequenceImportResult> importNina(String n, Map<String, dynamic> f,
          {bool treatWarningsAsErrors = false}) =>
      throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() => throw UnimplementedError();
  @override
  Future<String> instantiateTemplate(String t, String n) =>
      throw UnimplementedError();
  @override
  Future<SequenceNode> getSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      detail ?? (throw UnimplementedError());
  @override
  Future<SequenceDetail> updateSequence(String id,
      {String? name, String? description, Map<String, dynamic>? body}) async {
    updatedId = id;
    updatedBody = body;
    return SequenceDetail(id: id, name: name ?? '', body: body ?? const {});
  }

  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) =>
      throw UnimplementedError();
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => runState;
  @override
  Future<String> start(String id) => throw UnimplementedError();
  @override
  Future<String> pause(String id) => throw UnimplementedError();
  @override
  Future<String> resume(String id) => throw UnimplementedError();
  @override
  Future<String> skipCurrent(String id) => throw UnimplementedError();
  @override
  Future<String> abort(String id) => throw UnimplementedError();
  @override
  Future<String> stop(String id) => throw UnimplementedError();
  @override
  Future<SequenceShareExport> exportShare(String id) =>
      throw UnimplementedError();
  @override
  void close() {}
}

// A high-scoring, well-framed target with a full timing payload. Window times
// are UTC; the panel renders them in local tz, so the test asserts on the
// score / framing / type rather than on a tz-sensitive clock string.
final _m31 = TonightSkyObject(
  id: 'M31',
  name: 'Andromeda Galaxy',
  type: 'galaxy',
  magnitude: 3.4,
  raDeg: 10.6847,
  decDeg: 41.269,
  altitudeDeg: 55,
  maxAltitudeDeg: 60,
  windowStartUtc: DateTime.utc(2026, 6, 29, 20, 14),
  windowEndUtc: DateTime.utc(2026, 6, 30, 4, 32),
  transitUtc: DateTime.utc(2026, 6, 30, 1, 10),
  integrationHours: 6.3,
  remainingHours: 3.2,
  framing: TonightFraming.good,
  score: 88,
  scoreReasons: const ['fills the frame (+35)', '6 h dark window (+25)'],
);

// A low-scoring, oversized target — must still appear (advise, don't dictate).
const _ngc = TonightSkyObject(
  id: 'NGC1976',
  name: 'Orion Nebula',
  type: 'nebula',
  magnitude: 4.0,
  raDeg: 83.8,
  decDeg: -5.4,
  altitudeDeg: 11,
  maxAltitudeDeg: 40,
  integrationHours: 2.0,
  framing: TonightFraming.tooBig,
  score: 32,
);

Widget _host(_RecordingClient client,
        {List<TonightSkyObject>? objects}) =>
    ProviderScope(
      overrides: [
        tonightSkyProvider.overrideWith((ref) async => objects ?? [_m31]),
        sequenceApiProvider.overrideWith((ref) => client),
      ],
      child: const MaterialApp(home: Scaffold(body: TonightSkyPanel())),
    );

void main() {
  testWidgets('the add action creates a full imaging run named after the object',
      (tester) async {
    final client = _RecordingClient();
    await tester.pumpWidget(_host(client));
    await tester.pump(); // resolve the tonightSky future

    expect(find.text('Andromeda Galaxy'), findsOneWidget);
    await tester.tap(find.byIcon(Icons.playlist_add));
    await tester.pump(); // run create()
    await tester.pump(); // settle the SnackBar

    expect(client.createdName, 'Andromeda Galaxy');
    final body = client.createdBody!;
    expect(body['schemaVersion'], 'openastroara-sequence-v1');
    // A real session, not a bare slew: the slew plus an imaging loop of
    // TakeExposures are all in the created tree.
    final json = body.toString();
    expect(json, contains('SlewScopeToRaDec'));
    expect(json, contains('TakeExposure'));
    expect(json, contains('LoopCondition'));
    expect(find.textContaining('Created an imaging run for "Andromeda Galaxy"'),
        findsOneWidget);
  });

  // The append-path harness: a session for M 42 already open (selected), then
  // the add action for Andromeda. Returns the container so tests can pre-set
  // run state and inspect what happened.
  Future<ProviderContainer> pumpWithOpenSequence(
      WidgetTester tester, _RecordingClient client) async {
    client.detail = SequenceDetail(
      id: 'seq-open',
      name: 'M 42',
      body: buildImagingRunBody(
        raDeg: 83.8,
        decDeg: -5.4,
        targetName: 'M 42',
        exposureSeconds: 120,
        frameCount: 48,
        warmAtEnd: true,
      ),
    );
    final container = ProviderContainer(overrides: [
      tonightSkyProvider.overrideWith((ref) async => [_m31]),
      sequenceApiProvider.overrideWith((ref) => client),
    ]);
    addTearDown(container.dispose);
    container.read(selectedSequenceIdProvider.notifier).select('seq-open');
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: TonightSkyPanel())),
    ));
    await tester.pump(); // resolve the tonightSky future
    return container;
  }

  testWidgets('with a sequence open, the add action appends the target to it',
      (tester) async {
    final client = _RecordingClient();
    await pumpWithOpenSequence(tester, client);

    await tester.tap(find.byIcon(Icons.playlist_add));
    await tester.pump(); // run the append round-trip
    await tester.pump(); // settle the SnackBar

    // Patched, not created — ONE plan with both targets, the warm-up still last.
    expect(client.createdName, isNull);
    expect(client.updatedId, 'seq-open');
    final names = childrenOf(client.updatedBody!).map((c) => c['Name']).toList();
    expect(names, contains('M 42'));
    expect(names, contains('Andromeda Galaxy'));
    expect(names.indexOf('Andromeda Galaxy'), greaterThan(names.indexOf('M 42')));
    expect((childrenOf(client.updatedBody!).last[r'$type'] as String),
        contains('WarmCamera'));
    expect(find.textContaining('Added "Andromeda Galaxy" to the open sequence'),
        findsOneWidget);
  });

  testWidgets('an actively-running open sequence gets a fresh run instead',
      (tester) async {
    // Appending to a live run would edit a file the executor won't re-read —
    // the target must land in a new sequence, not silently miss tonight.
    final client = _RecordingClient()
      ..runState = const SequenceRunStateInfo(
          sequenceId: 'seq-open', state: SequenceRunState.running);
    await pumpWithOpenSequence(tester, client);

    await tester.tap(find.byIcon(Icons.playlist_add));
    await tester.pump();
    await tester.pump();

    expect(client.updatedId, isNull);
    expect(client.createdName, 'Andromeda Galaxy');
    expect(find.textContaining('Created an imaging run for "Andromeda Galaxy"'),
        findsOneWidget);
  });

  testWidgets('tapping the row sends a framing goto and highlights the row',
      (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();

    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pump();

    final container = ProviderScope.containerOf(
        tester.element(find.byType(TonightSkyPanel)));
    // The framing goto carries the name (the page labels the frame with it)
    // and frame:true (the page opens the framing overlay on the object).
    expect(container.read(planetariumCommandProvider), {
      'type': 'goto',
      'ra': _m31.raDeg,
      'dec': _m31.decDeg,
      'name': 'Andromeda Galaxy',
      'frame': true,
    });
    // The tapped row is remembered and painted selected, so the user can see
    // which row drove the atlas.
    expect(container.read(selectedTonightObjectProvider), 'M31');
    final rowBox = tester.widget<Container>(find
        .ancestor(of: find.text('Andromeda Galaxy'),
            matching: find.byType(Container))
        .first);
    expect(rowBox.color, isNotNull);
  });

  testWidgets('framing a second row after the first still sends its goto',
      (tester) async {
    // Repro for "frame A, then frame B doesn't work": two sequential row taps
    // must each deliver their own framing goto (the command bus re-fires on
    // every non-null send, even when the page-side frame was toggled between).
    await tester.pumpWidget(_host(_RecordingClient(), objects: [_m31, _ngc]));
    await tester.pump();
    final container = ProviderScope.containerOf(
        tester.element(find.byType(TonightSkyPanel)));

    await tester.tap(find.text('Andromeda Galaxy'));
    await tester.pump();
    expect(container.read(planetariumCommandProvider)?['ra'], _m31.raDeg);

    // The StellariumView listener consumes + clears the bus after forwarding;
    // model that so the second tap starts from a cleared bus like the real app.
    container.read(planetariumCommandProvider.notifier).clear();
    expect(container.read(planetariumCommandProvider), isNull);

    await tester.tap(find.text('Orion Nebula'));
    await tester.pump();
    expect(container.read(planetariumCommandProvider), {
      'type': 'goto',
      'ra': _ngc.raDeg,
      'dec': _ngc.decDeg,
      'name': 'Orion Nebula',
      'frame': true,
    });
    expect(container.read(selectedTonightObjectProvider), 'NGC1976');
  });

  testWidgets('the crosshair icon stays a centre-only goto (no frame key)',
      (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();

    await tester.tap(find.byIcon(Icons.my_location));
    await tester.pump();

    final container = ProviderScope.containerOf(
        tester.element(find.byType(TonightSkyPanel)));
    expect(container.read(planetariumCommandProvider), {
      'type': 'goto',
      'ra': _m31.raDeg,
      'dec': _m31.decDeg,
    });
    // Centre-only: no selection claim — the row is not marked as framed.
    expect(container.read(selectedTonightObjectProvider), isNull);
  });

  testWidgets('a create failure surfaces an error SnackBar', (tester) async {
    final client = _RecordingClient()..throwOnCreate = true;
    await tester.pumpWidget(_host(client));
    await tester.pump();

    await tester.tap(find.byIcon(Icons.playlist_add));
    await tester.pump();
    await tester.pump();

    expect(find.textContaining("Couldn't create the run"), findsOneWidget);
  });

  testWidgets('renders the score badge, framing chip and timing line',
      (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();

    expect(find.text('88'), findsOneWidget); // score badge
    expect(find.text('Fills frame'), findsOneWidget); // framing chip (good)
    // Timing line: tz-agnostic substrings (the clock part is localised).
    expect(find.textContaining('6.3 h dark'), findsOneWidget);
    expect(find.textContaining('3.2 h left'), findsOneWidget);
    expect(find.textContaining('transit'), findsOneWidget);
  });

  testWidgets('the "why" reasons stay collapsed until tapped', (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();

    expect(find.text('• fills the frame (+35)'), findsNothing);
    await tester.tap(find.text('Why?'));
    await tester.pump();
    expect(find.text('• fills the frame (+35)'), findsOneWidget);
    expect(find.text('• 6 h dark window (+25)'), findsOneWidget);
  });

  testWidgets(
      'filter advice renders a chip, and the Why? breakdown carries the '
      'reason + Optimal Sub + Glover attribution', (tester) async {
    final advised = TonightSkyObject(
      id: 'NGC6960',
      name: 'Veil Nebula (West)',
      type: 'SNR',
      magnitude: 7.0,
      raDeg: 311.9,
      decDeg: 30.7,
      altitudeDeg: 48,
      maxAltitudeDeg: 80,
      integrationHours: 5.0,
      framing: TonightFraming.good,
      score: 75,
      scoreReasons: const ['narrowband recommended (+0)'],
      filterAdvice: TonightFilterAdvice.narrowband,
      adviceReason:
          'Emission-line target — narrowband (Ha 7nm) sees ~14× less sky glow.',
      optimalSubS: 300,
    );
    await tester.pumpWidget(_host(_RecordingClient(), objects: [advised]));
    await tester.pump();

    expect(find.text('Narrowband'), findsOneWidget); // the advice chip
    // Reason + figure + attribution live in the collapsed Why? breakdown.
    expect(find.textContaining('~14×'), findsNothing);
    await tester.tap(find.text('Why?'));
    await tester.pump();
    expect(find.textContaining('~14× less sky glow'), findsOneWidget);
    expect(find.textContaining('Optimal sub ≈ 5.0 min'), findsOneWidget);
    expect(find.textContaining('Dr. Robin Glover'), findsOneWidget,
        reason: 'the attribution is a condition of the permission');
  });

  testWidgets('no advice fields → no chip and no advice lines', (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();
    expect(find.text('Narrowband'), findsNothing);
    expect(find.text('Broadband'), findsNothing);
    await tester.tap(find.text('Why?'));
    await tester.pump();
    expect(find.textContaining('Optimal sub'), findsNothing);
    expect(find.textContaining('Glover'), findsNothing);
  });

  testWidgets('a moon-up window renders the separation/illumination chip',
      (tester) async {
    final moonlit = TonightSkyObject(
      id: _m31.id, name: _m31.name, type: _m31.type,
      magnitude: _m31.magnitude, raDeg: _m31.raDeg, decDeg: _m31.decDeg,
      altitudeDeg: 55, maxAltitudeDeg: 60, score: 88,
      moonSeparationDeg: 42.3, moonIlluminationPct: 78, moonUpFraction: 0.65,
    );
    await tester.pumpWidget(_host(_RecordingClient(), objects: [moonlit]));
    await tester.pump();
    expect(find.text('☾ 42° · 78%'), findsOneWidget);
    expect(find.text('Moonless'), findsNothing);
  });

  testWidgets('a moonless window renders the Moonless chip', (tester) async {
    final moonless = TonightSkyObject(
      id: _m31.id, name: _m31.name, type: _m31.type,
      magnitude: _m31.magnitude, raDeg: _m31.raDeg, decDeg: _m31.decDeg,
      altitudeDeg: 55, maxAltitudeDeg: 60, score: 88,
      moonSeparationDeg: 120, moonIlluminationPct: 78, moonUpFraction: 0,
    );
    await tester.pumpWidget(_host(_RecordingClient(), objects: [moonless]));
    await tester.pump();
    expect(find.text('Moonless'), findsOneWidget);
    expect(find.textContaining('☾'), findsNothing);
  });

  group('windowStateFor (slice-4 best-window highlight)', () {
    TonightSkyObject obj({DateTime? start, DateTime? end}) => TonightSkyObject(
          id: 'X', name: 'X', type: 'galaxy', magnitude: 8,
          raDeg: 0, decDeg: 0, altitudeDeg: 40, maxAltitudeDeg: 60,
          windowStartUtc: start, windowEndUtc: end,
        );
    final t0 = DateTime.utc(2026, 12, 21, 22);
    final t1 = DateTime.utc(2026, 12, 22, 4);

    test('no window → none', () {
      expect(windowStateFor(obj(), t0), TonightWindowState.none);
      expect(windowStateFor(obj(start: t0), t0), TonightWindowState.none);
    });
    test('before the window → upcoming', () {
      expect(windowStateFor(obj(start: t0, end: t1), DateTime.utc(2026, 12, 21, 20)),
          TonightWindowState.upcoming);
    });
    test('inside (and at both boundaries) → open', () {
      final o = obj(start: t0, end: t1);
      expect(windowStateFor(o, DateTime.utc(2026, 12, 22, 1)), TonightWindowState.open);
      expect(windowStateFor(o, t0), TonightWindowState.open,
          reason: 'the opening instant is in the window');
      expect(windowStateFor(o, t1), TonightWindowState.open,
          reason: 'an exposure started in the last minute still counts');
    });
    test('after the window → passed', () {
      expect(windowStateFor(obj(start: t0, end: t1), DateTime.utc(2026, 12, 22, 6)),
          TonightWindowState.passed);
    });
  });

  testWidgets('an open window renders the green "now · " timing treatment',
      (tester) async {
    // Window straddling the real clock (±1 h) — deterministic for any test run.
    final now = DateTime.now().toUtc();
    final inWindow = TonightSkyObject(
      id: _m31.id, name: _m31.name, type: _m31.type, magnitude: _m31.magnitude,
      raDeg: _m31.raDeg, decDeg: _m31.decDeg,
      altitudeDeg: 55, maxAltitudeDeg: 60, score: 88,
      windowStartUtc: now.subtract(const Duration(hours: 1)),
      windowEndUtc: now.add(const Duration(hours: 1)),
      integrationHours: 2,
    );
    await tester.pumpWidget(_host(_RecordingClient(), objects: [inWindow]));
    await tester.pump();
    expect(find.textContaining('now · '), findsOneWidget);
  });

  testWidgets('a fully-passed window renders without the "now" marker',
      (tester) async {
    final now = DateTime.now().toUtc();
    final passed = TonightSkyObject(
      id: _m31.id, name: _m31.name, type: _m31.type, magnitude: _m31.magnitude,
      raDeg: _m31.raDeg, decDeg: _m31.decDeg,
      altitudeDeg: 55, maxAltitudeDeg: 60, score: 88,
      windowStartUtc: now.subtract(const Duration(hours: 8)),
      windowEndUtc: now.subtract(const Duration(hours: 2)),
      integrationHours: 6,
    );
    await tester.pumpWidget(_host(_RecordingClient(), objects: [passed]));
    await tester.pump();
    expect(find.textContaining('now · '), findsNothing);
    expect(find.textContaining('h dark'), findsOneWidget,
        reason: 'the row still shows its timing — advise, never hide');
  });

  testWidgets('a pre-slice-4 daemon (no moon fields) renders no moon chip',
      (tester) async {
    // _m31 carries no moon fields at all.
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();
    expect(find.text('Moonless'), findsNothing);
    expect(find.textContaining('☾'), findsNothing);
  });

  testWidgets(
      'the recentre action sends a goto with the object ra/dec to the '
      'planetarium command provider', (tester) async {
    // Own container so we can read the provider seam after the tap.
    final container = ProviderContainer(overrides: [
      tonightSkyProvider.overrideWith((ref) async => [_m31]),
      sequenceApiProvider.overrideWith((ref) => _RecordingClient()),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: TonightSkyPanel())),
    ));
    await tester.pump(); // resolve the tonightSky future

    expect(container.read(planetariumCommandProvider), isNull);
    await tester.tap(find.byIcon(Icons.my_location));
    await tester.pump();

    expect(container.read(planetariumCommandProvider), {
      'type': 'goto',
      'ra': _m31.raDeg,
      'dec': _m31.decDeg,
    });
  });

  testWidgets('low-scoring rows are still shown, ranked below', (tester) async {
    // Server ranks descending; the panel must render every row it's given,
    // including the low-score oversized target (advise, don't dictate).
    await tester.pumpWidget(
      _host(_RecordingClient(), objects: [_m31, _ngc]),
    );
    await tester.pump();

    expect(find.text('Andromeda Galaxy'), findsOneWidget);
    expect(find.text('Orion Nebula'), findsOneWidget);
    expect(find.text('32'), findsOneWidget); // low score still rendered
    expect(find.text('Too big'), findsOneWidget); // framing advice shown
  });
}
