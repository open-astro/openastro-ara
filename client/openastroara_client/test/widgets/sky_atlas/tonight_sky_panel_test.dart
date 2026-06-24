import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
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

const _m31 = TonightSkyObject(
  id: 'M31',
  name: 'Andromeda Galaxy',
  type: 'galaxy',
  magnitude: 3.4,
  raDeg: 10.6847,
  decDeg: 41.269,
  altitudeDeg: 55,
  maxAltitudeDeg: 60,
);

Widget _host(_RecordingClient client) => ProviderScope(
      overrides: [
        tonightSkyProvider.overrideWith((ref) async => [_m31]),
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
}
