import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/sky_atlas/sky_atlas_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/tonight_sky_panel.dart';

/// Records create() calls; everything else is unused by this panel.
class _RecordingClient implements SequenceClient {
  String? createdName;
  Map<String, dynamic>? createdBody;
  bool throwOnCreate = false;

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
  Future<SequenceDetail> getSequenceDetail(String id) =>
      throw UnimplementedError();
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) =>
      throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) =>
      throw UnimplementedError();
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) =>
      throw UnimplementedError();
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
  framing: TonightFraming.fillsFrame,
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
  testWidgets('the add action creates a slew sequence named after the object',
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
    expect(find.textContaining('Added "Andromeda Galaxy"'), findsOneWidget);
  });

  testWidgets('a create failure surfaces an error SnackBar', (tester) async {
    final client = _RecordingClient()..throwOnCreate = true;
    await tester.pumpWidget(_host(client));
    await tester.pump();

    await tester.tap(find.byIcon(Icons.playlist_add));
    await tester.pump();
    await tester.pump();

    expect(find.textContaining("Couldn't add to a sequence"), findsOneWidget);
  });

  testWidgets('renders the score badge, framing chip and timing line',
      (tester) async {
    await tester.pumpWidget(_host(_RecordingClient()));
    await tester.pump();

    expect(find.text('88'), findsOneWidget); // score badge
    expect(find.text('Fills frame'), findsOneWidget); // framing chip (fillsFrame)
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
