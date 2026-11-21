import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_import.dart';

/// create() returns a configured id (or throws); records what was sent —
/// the import flow now translates CLIENT-side and lands via the ordinary
/// idempotent create (PORT_DECISIONS 2026-07-15).
class _ImportClient implements SequenceClient {
  @override
  Future<SequenceDetail> addRunItem(
    String id, {
    required List<int> parentPath,
    int? index,
    required Map<String, dynamic> item,
  }) => throw UnimplementedError();
  @override
  Future<SequenceDetail> removeRunItem(String id, List<int> path) =>
      throw UnimplementedError();
  @override
  Future<SequenceDetail> moveRunItem(String id, List<int> path, int newIndex) =>
      throw UnimplementedError();

  /// When set, create() throws this instead of throwOnImport's Exception —
  /// lets tests simulate a pure transport failure (DioException, response
  /// null) vs a daemon rejection.
  Object? throwError;
  @override
  Future<String> decideAutoFlats(
    String id, {
    required String choice,
    required bool remember,
  }) => throw UnimplementedError();
  @override
  Future<bool> deleteSequence(String id) => throw UnimplementedError();
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  _ImportClient({this.throwOnImport = false});
  final bool throwOnImport;
  String? lastName;
  Map<String, dynamic>? lastBody;
  String? lastKey;

  @override
  Future<SequencePage> list({int limit = 50, String? cursor}) async =>
      const SequencePage(items: []);
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: const {});
  @override
  Future<SequenceDetail> updateSequence(
    String id, {
    String? name,
    String? description,
    Map<String, dynamic>? body,
  }) async => SequenceDetail(
    id: id,
    name: name ?? id,
    description: description,
    body: body ?? const {},
  );
  @override
  Future<SequenceNode> getSequence(String id) async =>
      SequenceNode(id: 'root', kind: SequenceNodeKind.root, displayName: id);
  @override
  Future<SequenceRunStateInfo?> getRunState(String id) async => null;
  @override
  Future<String> start(String id) async => 'op';
  @override
  Future<String> pause(String id) async => 'op';
  @override
  Future<String> resume(
    String id, {
    bool recenter = true,
    bool refocus = false,
  }) async => 'op';
  @override
  Future<String> skipCurrent(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async =>
      throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  Future<String> create(
    String name,
    Map<String, dynamic> body, {
    String? description,
    String? idempotencyKey,
  }) async {
    lastName = name;
    lastBody = body;
    lastKey = idempotencyKey;
    if (throwError != null) throw throwError!;
    if (throwOnImport) throw Exception('boom');
    return 'imp-1';
  }

  @override
  void close() {}
}

/// In-memory draft store — widget tests can't await real file IO. Records
/// what the degraded/offline import path saved.
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

/// A minimal NINA export: a root holding one exposure. Imports clean.
const _nina = <String, dynamic>{
  r'$type': 'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
  'Name': 'M42',
  'Items': {
    r'$values': [
      {
        r'$type':
            'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
        'ExposureTime': 60.0,
        'Gain': 100,
        'Offset': 10,
        'ImageType': 'LIGHT',
      },
    ],
  },
};

const _exampleType =
    'NINA.Plugin.Example.Instructions.ExampleInstruction, NINA.Plugin.Example';

/// The same export plus one instruction Ara does not ship: one placeholder, so
/// one warning.
const _ninaWithPlaceholder = <String, dynamic>{
  r'$type': 'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
  'Name': 'M42',
  'Items': {
    r'$values': [
      {r'$type': _exampleType, 'Strength': 5},
    ],
  },
};

const _placeholderWarning =
    '"$_exampleType" is not supported by OpenAstro Ara and was not imported. '
    'It stays in the sequence as a placeholder that is skipped when the '
    'sequence runs.';

const _v2 = <String, dynamic>{
  'schemaVersion': 'openastroara-sequence-v2',
  'name': 'M42',
  'targets': <Object?>[],
  'root': {
    'kind': 'root',
    'items': [
      {'kind': 'capture', 'exposureTime': 60.0},
    ],
  },
};

/// Every key in [value] outside a placeholder's payload.
Iterable<String> _keys(Object? value) sync* {
  if (value is Map) {
    for (final e in value.entries) {
      yield e.key as String;
      if (e.key == 'originalPayload') continue;
      yield* _keys(e.value);
    }
  } else if (value is List) {
    for (final v in value) {
      yield* _keys(v);
    }
  }
}

void main() {
  // Captures importSequenceFromJson's return value (the created id, or null on
  // failure) so tests can assert the contract the Load dialog relies on to
  // decide whether to close.
  late String? returnedId;
  late bool returned;

  Future<ProviderContainer> pump(
    WidgetTester tester,
    _ImportClient? client, {
    Map<String, dynamic> ninaJson = _nina,
    _MemDraftService? drafts,
  }) async {
    returnedId = null;
    returned = false;
    final container = ProviderContainer(
      overrides: [
        sequenceApiProvider.overrideWithValue(client),
        if (drafts != null)
          draftSequenceServiceProvider.overrideWithValue(drafts),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: Consumer(
              builder: (context, ref, _) {
                return ElevatedButton(
                  onPressed: () async {
                    final id = await importSequenceFromJson(
                      context,
                      ref,
                      name: 'M42',
                      fileName: 'M42.json',
                      ninaJson: ninaJson,
                    );
                    returnedId = id;
                    returned = true;
                  },
                  child: const Text('import'),
                );
              },
            ),
          ),
        ),
      ),
    );
    return container;
  }

  testWidgets('a v2 body is uploaded as written, selected and confirmed', (
    tester,
  ) async {
    final client = _ImportClient();
    final container = await pump(tester, client, ninaJson: _v2);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(client.lastBody, _v2, reason: 'a v2 body is not mapped again');
    expect(container.read(selectedSequenceIdProvider), 'imp-1');
    expect(find.textContaining('Imported "M42"'), findsOneWidget); // SnackBar
    // Returns the created id so the caller knows the import succeeded (→ closes
    // the Load dialog).
    expect(returned, isTrue);
    expect(returnedId, 'imp-1');
  });

  testWidgets('a clean NINA import uploads v2 with no dialog', (tester) async {
    final client = _ImportClient();
    final container = await pump(tester, client);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.byType(AlertDialog), findsNothing);
    expect(client.lastBody!['schemaVersion'], 'openastroara-sequence-v2');
    expect(container.read(selectedSequenceIdProvider), 'imp-1');
    expect(find.textContaining('Imported "M42"'), findsOneWidget);
  });

  testWidgets('placeholder warnings are shown before anything is uploaded', (
    tester,
  ) async {
    final client = _ImportClient();
    final container = await pump(
      tester,
      client,
      ninaJson: _ninaWithPlaceholder,
    );
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining(_placeholderWarning), findsOneWidget);
    expect(
      client.lastBody,
      isNull,
      reason: 'nothing is uploaded until the user confirms',
    );
    expect(returned, isFalse);

    await tester.tap(find.text('Import'));
    await tester.pumpAndSettle();

    expect(client.lastBody!['schemaVersion'], 'openastroara-sequence-v2');
    expect(container.read(selectedSequenceIdProvider), 'imp-1');
    expect(returnedId, 'imp-1');
  });

  testWidgets('cancelling the warnings uploads nothing and selects nothing', (
    tester,
  ) async {
    final client = _ImportClient();
    final drafts = _MemDraftService();
    final container = await pump(
      tester,
      client,
      ninaJson: _ninaWithPlaceholder,
      drafts: drafts,
    );
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(client.lastBody, isNull);
    expect(drafts.store, isEmpty);
    expect(container.read(selectedSequenceIdProvider), isNull);
    expect(returned, isTrue);
    expect(returnedId, isNull, reason: 'the Load dialog stays open');
  });

  testWidgets('the uploaded body carries metadata.importedFrom and no \$type', (
    tester,
  ) async {
    final client = _ImportClient();
    await pump(tester, client);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    final from = (client.lastBody!['metadata'] as Map)['importedFrom'] as Map;
    expect(from['format'], 'nina');
    expect(from['fileName'], 'M42.json');
    final stamp = DateTime.parse(from['importedUtc'] as String);
    expect(stamp.isUtc, isTrue);
    expect(
      DateTime.now().toUtc().difference(stamp).inMinutes.abs(),
      lessThan(5),
    );
    expect(_keys(client.lastBody).where((k) => k.startsWith(r'$')), isEmpty);
    expect(client.lastBody!['root'], isA<Map>());
  });

  for (final refused in <String, Map<String, dynamic>>{
    'a body with no \$type': {'Name': 'M42'},
    'a root that is not a container': {
      r'$type':
          'NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer',
      'ExposureTime': 60.0,
    },
    'a shared node': {
      r'$type':
          'NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer',
      'Items': {
        r'$values': [
          {
            r'$id': '3',
            r'$type': 'NINA.Sequencer.SequenceItem.Utility.Annotation, NINA.Sequencer',
          },
          {r'$ref': '3'},
        ],
      },
    },
  }.entries) {
    testWidgets('${refused.key} is refused and uploads nothing', (
      tester,
    ) async {
      final client = _ImportClient();
      final drafts = _MemDraftService();
      final container = await pump(
        tester,
        client,
        ninaJson: refused.value,
        drafts: drafts,
      );
      await tester.tap(find.text('import'));
      await tester.pumpAndSettle();

      expect(client.lastBody, isNull);
      expect(drafts.store, isEmpty);
      expect(container.read(selectedSequenceIdProvider), isNull);
      expect(returned, isTrue);
      expect(returnedId, isNull);
      expect(find.byType(AlertDialog), findsNothing);
      expect(
        find.textContaining('Couldn\'t import the sequence'),
        findsNothing,
        reason: 'a refusal says what is wrong with the file, not "check the connection"',
      );
      expect(
        find.text(
          refused.key == 'a shared node'
              ? 'This sequence has a shared node, which OpenAstro Ara cannot import.'
              : 'This file is not a NINA sequence.',
        ),
        findsOneWidget,
      );
    });
  }

  testWidgets('an import failure shows a SnackBar and selects nothing', (
    tester,
  ) async {
    final container = await pump(tester, _ImportClient(throwOnImport: true));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't import the sequence"), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
    // Returns null on failure so the caller keeps the Load dialog open.
    expect(returned, isTrue);
    expect(returnedId, isNull);
  });

  testWidgets(
    'a pure transport failure degrades to a local v2 draft with the same key',
    (tester) async {
      // A saved server isn't a reachable one: DioException with NO response
      // (link down) must not lose the translated result — it lands as a draft
      // stamped with the import key so the eventual push dedupes (#854 round 2).
      final client = _ImportClient()
        ..throwError = DioException(
          requestOptions: RequestOptions(path: '/sequences'),
          type: DioExceptionType.connectionError,
        );
      final drafts = _MemDraftService();
      await pump(tester, client, drafts: drafts);
      await tester.tap(find.text('import'));
      await tester.pumpAndSettle();

      expect(drafts.store, hasLength(1));
      final draft = drafts.store.values.single;
      expect(
        draft.pushKey,
        client.lastKey,
        reason: 'the draft reuses the key the failed create already sent',
      );
      expect(draft.pushKey, startsWith('import-'));
      expect(draft.body['schemaVersion'], 'openastroara-sequence-v2');
      expect(
        draft.body,
        client.lastBody,
        reason: 'the draft holds what the create sent',
      );
      expect(returnedId, draft.id, reason: 'caller closes the dialog');
    },
  );

  testWidgets('an offline import saves the v2 body as a draft', (tester) async {
    final drafts = _MemDraftService();
    await pump(tester, null, drafts: drafts);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(
      drafts.store.values.single.body['schemaVersion'],
      'openastroara-sequence-v2',
    );
    expect(returnedId, drafts.store.keys.single);
  });

  testWidgets('a daemon rejection (response present) does NOT create a draft', (
    tester,
  ) async {
    final client = _ImportClient()
      ..throwError = DioException(
        requestOptions: RequestOptions(path: '/sequences'),
        response: Response(
          requestOptions: RequestOptions(path: '/sequences'),
          statusCode: 422,
        ),
        type: DioExceptionType.badResponse,
      );
    final drafts = _MemDraftService();
    await pump(tester, client, drafts: drafts);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(
      drafts.store,
      isEmpty,
      reason: 'a rejected body would be rejected on push too',
    );
    expect(find.textContaining("Couldn't import the sequence"), findsOneWidget);
    expect(returnedId, isNull);
  });

  testWidgets(
    'creates via the ordinary endpoint: a v2 body, a name and a key',
    (tester) async {
      final client = _ImportClient();
      await pump(tester, client);
      await tester.tap(find.text('import'));
      await tester.pumpAndSettle();
      expect(client.lastName, 'M42');
      expect(
        (client.lastBody!['root'] as Map)['kind'],
        'root',
        reason:
            'the body arrives mapped to v2 — no daemon-side normalizer left',
      );
      expect(client.lastBody!['name'], 'M42');
      expect(
        client.lastKey,
        startsWith('import-'),
        reason: 'imports ride the idempotent create (PR E)',
      );
    },
  );

  test('a file that is not JSON gets the one refusal sentence', () {
    expect(
      ninaFileErrorMessage(
        lookupAppLocalizations(const Locale('en')),
        NinaFileError.notJson,
      ),
      'This file is not a NINA sequence.',
    );
  });

  group('readNinaSequenceFile', () {
    late Directory tmp;
    setUp(
      () async => tmp = await Directory.systemTemp.createTemp('nina_import_'),
    );
    tearDown(() async => tmp.delete(recursive: true));

    test('parses a valid object and derives the name from the file', () async {
      final f = File('${tmp.path}/M42 Wide.json')
        ..writeAsStringSync('{"Name":"M42"}');
      final r = await readNinaSequenceFile(f.path);
      expect(r.ok, isTrue);
      expect(r.nina, const {'Name': 'M42'});
      expect(r.name, 'M42 Wide'); // basename minus .json
      expect(r.fileName, 'M42 Wide.json'); // the spec's `fileName`
    });

    test('a non-object JSON body → notJson', () async {
      final f = File('${tmp.path}/arr.json')..writeAsStringSync('[1,2,3]');
      final r = await readNinaSequenceFile(f.path);
      expect(r.ok, isFalse);
      expect(r.error, NinaFileError.notJson);
    });

    test('unparseable content → notJson', () async {
      final f = File('${tmp.path}/junk.json')..writeAsStringSync('not json');
      final r = await readNinaSequenceFile(f.path);
      expect(r.error, NinaFileError.notJson);
    });

    test(
      'a missing/unreadable file degrades to readError, does not throw',
      () async {
        // length()/readAsString() on a nonexistent path throws FileSystemException;
        // it must be caught (not escape) and surface as a read error — distinct
        // from a parse failure so the user gets the right hint.
        final r = await readNinaSequenceFile('${tmp.path}/does-not-exist.json');
        expect(r.ok, isFalse);
        expect(r.error, NinaFileError.readError);
      },
    );

    test('an over-limit file → tooLarge (distinct from notJson)', () async {
      // Make the file one byte past the 32 MiB cap WITHOUT allocating a 32 MiB
      // string: seek past the cap and write a single byte (the length check
      // returns tooLarge before any content is read, so the bytes don't matter).
      final f = File('${tmp.path}/huge.json');
      final raf = await f.open(mode: FileMode.write);
      await raf.setPosition(32 * 1024 * 1024); // == _maxSequenceFileBytes
      await raf.writeByte(0x20); // → length is cap + 1
      await raf.close();
      final r = await readNinaSequenceFile(f.path);
      expect(r.ok, isFalse);
      expect(r.error, NinaFileError.tooLarge);
    });
  });
}
