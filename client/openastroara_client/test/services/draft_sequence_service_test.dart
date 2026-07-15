import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/services/draft_sequence_service.dart';

void main() {
  late Directory tmp;
  late DraftSequenceService svc;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('draft_seq_test');
    svc = DraftSequenceService(supportDir: () async => tmp);
  });

  tearDown(() async {
    await tmp.delete(recursive: true);
  });

  DraftSequence make(String id, {String name = 'M 31', int minute = 0}) =>
      DraftSequence(
        id: id,
        name: name,
        updatedUtc: DateTime.utc(2026, 7, 14, 20, minute),
        body: {
          'kind': 'root',
          'items': [
            {'type': 'TakeExposure', 'ExposureTime': 120.0},
          ],
        },
      );

  test('save → loadAll round-trips a draft body verbatim', () async {
    await svc.save(make('draft:abc-1'));
    final loaded = await svc.loadAll();
    expect(loaded, hasLength(1));
    expect(loaded.single.id, 'draft:abc-1');
    expect(loaded.single.name, 'M 31');
    expect(loaded.single.body['items'], [
      {'type': 'TakeExposure', 'ExposureTime': 120.0},
    ]);
  });

  test('loadAll orders newest-first', () async {
    await svc.save(make('draft:old', minute: 0));
    await svc.save(make('draft:new', name: 'M 42', minute: 30));
    final loaded = await svc.loadAll();
    expect(loaded.map((d) => d.id), ['draft:new', 'draft:old']);
  });

  test('save overwrites by id', () async {
    await svc.save(make('draft:abc-1'));
    await svc.save(make('draft:abc-1', name: 'M 31 redo', minute: 5));
    final loaded = await svc.loadAll();
    expect(loaded, hasLength(1));
    expect(loaded.single.name, 'M 31 redo');
  });

  test('delete removes the draft; deleting a missing id is a no-op', () async {
    await svc.save(make('draft:abc-1'));
    await svc.delete('draft:abc-1');
    await svc.delete('draft:never-existed');
    expect(await svc.loadAll(), isEmpty);
  });

  test('a corrupt draft file is skipped, not fatal', () async {
    await svc.save(make('draft:good'));
    await File('${tmp.path}/sequence_drafts/broken.json')
        .writeAsString('{not json');
    final loaded = await svc.loadAll();
    expect(loaded.map((d) => d.id), ['draft:good']);
  });

  test('newId mints namespaced, unique ids', () {
    final a = svc.newId();
    final b = svc.newId();
    expect(isDraftSequenceId(a), isTrue);
    expect(a, isNot(b));
  });

  test('fromJson rejects records without a namespaced id or body', () {
    expect(DraftSequence.fromJson({'id': 'plain-guid', 'body': {}}), isNull);
    expect(DraftSequence.fromJson({'id': 'draft:x'}), isNull);
    expect(DraftSequence.fromJson('nonsense'), isNull);
  });
}
