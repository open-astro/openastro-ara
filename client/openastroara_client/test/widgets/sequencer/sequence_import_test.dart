import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/sequence_node.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/sequence/sequence_share_export.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/widgets/sequencer/sequence_import.dart';

/// importNina returns a configured result (or throws); records what was sent.
class _ImportClient implements SequenceClient {
  @override
  Future<SequenceValidationResult> validate(Map<String, dynamic> body) async =>
      const SequenceValidationResult(valid: true);
  _ImportClient({this.result, this.throwOnImport = false});
  final SequenceImportResult? result;
  final bool throwOnImport;
  String? lastName;
  Map<String, dynamic>? lastBody;

  @override
  Future<SequenceImportResult> importNina(String name, Map<String, dynamic> file,
      {bool treatWarningsAsErrors = false}) async {
    lastName = name;
    lastBody = file;
    if (throwOnImport) throw Exception('boom');
    return result ??
        const SequenceImportResult(createdSequenceId: 'imp-1', name: 'Imported');
  }

  @override
  Future<SequencePage> list({int limit = 50}) async => const SequencePage(items: []);
  @override
  Future<SequenceDetail> getSequenceDetail(String id) async =>
      SequenceDetail(id: id, name: id, body: const {});
  @override
  Future<SequenceDetail> updateSequence(String id,
          {String? name, String? description, Map<String, dynamic>? body}) async =>
      SequenceDetail(id: id, name: name ?? id, description: description, body: body ?? const {});
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
  Future<String> resume(String id) async => 'op';
  @override
  Future<String> abort(String id) async => 'op';
  @override
  Future<String> stop(String id) async => 'op';
  @override
  Future<SequenceShareExport> exportShare(String id) async => throw UnimplementedError();
  @override
  Future<List<SequenceTemplate>> listTemplates() async => const [];
  @override
  Future<String> instantiateTemplate(String t, String n) async => 'new-seq';
  @override
  void close() {}
}

void main() {
  // Captures importSequenceFromJson's return value (the created id, or null on
  // failure) so tests can assert the contract the Load dialog relies on to
  // decide whether to close.
  late String? returnedId;
  late bool returned;

  Future<ProviderContainer> pump(WidgetTester tester, _ImportClient client) async {
    returnedId = null;
    returned = false;
    final container = ProviderContainer(overrides: [
      sequenceApiProvider.overrideWithValue(client),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(builder: (context, ref, _) {
            return ElevatedButton(
              onPressed: () async {
                final id = await importSequenceFromJson(context, ref,
                    name: 'M42', ninaJson: const {'Name': 'M42'});
                returnedId = id;
                returned = true;
              },
              child: const Text('import'),
            );
          }),
        ),
      ),
    ));
    return container;
  }

  testWidgets('a clean import selects the new sequence + confirms', (tester) async {
    final container = await pump(
        tester, _ImportClient(result: const SequenceImportResult(
            createdSequenceId: 'imp-9', name: 'M42 imported')));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(container.read(selectedSequenceIdProvider), 'imp-9');
    expect(find.textContaining('Imported "M42 imported"'), findsOneWidget); // SnackBar
    // Returns the created id so the caller knows the import succeeded (→ closes
    // the Load dialog).
    expect(returned, isTrue);
    expect(returnedId, 'imp-9');
  });

  testWidgets('a lossy import shows the warnings dialog', (tester) async {
    final container = await pump(
        tester,
        _ImportClient(
            result: const SequenceImportResult(
          createdSequenceId: 'imp-2',
          name: 'M42',
          lossyTranslation: true,
          droppedInstructionTypes: ['NINA.X.Foo'],
          warnings: ['Approximated a trigger'],
        )));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining('with warnings'), findsOneWidget);
    expect(find.textContaining('NINA.X.Foo'), findsOneWidget);
    // Still selected the new sequence.
    expect(container.read(selectedSequenceIdProvider), 'imp-2');
  });

  testWidgets('an import failure shows a SnackBar and selects nothing',
      (tester) async {
    final container = await pump(tester, _ImportClient(throwOnImport: true));
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();

    expect(find.textContaining("Couldn't import the sequence"), findsOneWidget);
    expect(container.read(selectedSequenceIdProvider), isNull);
    // Returns null on failure so the caller keeps the Load dialog open.
    expect(returned, isTrue);
    expect(returnedId, isNull);
  });

  testWidgets('sends the NINA body + name to the API', (tester) async {
    final client = _ImportClient();
    await pump(tester, client);
    await tester.tap(find.text('import'));
    await tester.pumpAndSettle();
    expect(client.lastName, 'M42');
    expect(client.lastBody, const {'Name': 'M42'});
  });

  group('readNinaSequenceFile', () {
    late Directory tmp;
    setUp(() async => tmp = await Directory.systemTemp.createTemp('nina_import_'));
    tearDown(() async => tmp.delete(recursive: true));

    test('parses a valid object and derives the name from the file', () async {
      final f = File('${tmp.path}/M42 Wide.json')
        ..writeAsStringSync('{"Name":"M42"}');
      final r = await readNinaSequenceFile(f.path);
      expect(r.ok, isTrue);
      expect(r.nina, const {'Name': 'M42'});
      expect(r.name, 'M42 Wide'); // basename minus .json
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

    test('a missing/unreadable file degrades to readError, does not throw',
        () async {
      // length()/readAsString() on a nonexistent path throws FileSystemException;
      // it must be caught (not escape) and surface as a read error — distinct
      // from a parse failure so the user gets the right hint.
      final r = await readNinaSequenceFile('${tmp.path}/does-not-exist.json');
      expect(r.ok, isFalse);
      expect(r.error, NinaFileError.readError);
    });

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
