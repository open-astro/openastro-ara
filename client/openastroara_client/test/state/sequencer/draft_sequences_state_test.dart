import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// Fake daemon client for the push path — records create() calls; everything
/// else is unused by the drafts notifier.
class _FakeSeqClient implements SequenceClient {
  final List<(String, Map<String, dynamic>)> created = [];
  Object? createError;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description}) async {
    if (createError != null) throw createError!;
    created.add((name, body));
    return 'daemon-id-${created.length}';
  }

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  late Directory tmp;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('draft_state_test');
  });

  tearDown(() async {
    await tmp.delete(recursive: true);
  });

  ProviderContainer container({SequenceClient? api}) {
    final c = ProviderContainer(overrides: [
      draftSequenceServiceProvider.overrideWithValue(
          DraftSequenceService(supportDir: () async => tmp)),
      sequenceApiProvider.overrideWith((ref) => api),
    ]);
    addTearDown(c.dispose);
    return c;
  }

  test('create stores a draft and returns a namespaced id', () async {
    final c = container();
    final id = await c
        .read(draftSequencesProvider.notifier)
        .create('M 31', {'items': []});
    expect(id, startsWith('draft:'));
    final drafts = await c.read(draftSequencesProvider.future);
    expect(drafts.single.name, 'M 31');
  });

  test('saveBody overwrites the body and keeps the name', () async {
    final c = container();
    final notifier = c.read(draftSequencesProvider.notifier);
    final id = await notifier.create('M 31', {'v': 1});
    await notifier.saveBody(id, {'v': 2});
    final drafts = await c.read(draftSequencesProvider.future);
    expect(drafts.single.name, 'M 31');
    expect(drafts.single.body, {'v': 2});
  });

  test('push creates on the daemon then deletes the local draft', () async {
    final api = _FakeSeqClient();
    final c = container(api: api);
    final notifier = c.read(draftSequencesProvider.notifier);
    await c.read(draftSequencesProvider.future); // settle initial load
    final id = await notifier.create('M 31', {'items': []});
    final newId = await notifier.push(id);
    expect(newId, 'daemon-id-1');
    expect(api.created.single.$1, 'M 31');
    expect(await c.read(draftSequencesProvider.future), isEmpty);
  });

  test('a failed push keeps the local draft', () async {
    final api = _FakeSeqClient()..createError = Exception('boom');
    final c = container(api: api);
    final notifier = c.read(draftSequencesProvider.notifier);
    await c.read(draftSequencesProvider.future);
    final id = await notifier.create('M 31', {'items': []});
    await expectLater(notifier.push(id), throwsA(isA<Exception>()));
    final drafts = await c.read(draftSequencesProvider.future);
    expect(drafts.single.id, id);
  });

  test('push with no server throws StateError and keeps the draft', () async {
    final c = container();
    final notifier = c.read(draftSequencesProvider.notifier);
    await c.read(draftSequencesProvider.future);
    final id = await notifier.create('M 31', {'items': []});
    await expectLater(notifier.push(id), throwsA(isA<StateError>()));
    expect(await c.read(draftSequencesProvider.future), isNotEmpty);
  });

  test('delete removes the draft', () async {
    final c = container();
    final notifier = c.read(draftSequencesProvider.notifier);
    final id = await notifier.create('M 31', {'items': []});
    await notifier.delete(id);
    expect(await c.read(draftSequencesProvider.future), isEmpty);
  });
}
