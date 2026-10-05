import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/draft_sequence.dart';
import 'package:openastroara/services/draft_sequence_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/sequencer/create_imaging_run.dart';
import 'package:openastroara/state/sequencer/draft_sequences_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

/// Regression for the rig failure of 2026-10-04: with nothing watching the
/// autoDispose [sequenceApiProvider] (the coordinate-entry dialog, a Tonight's
/// Sky row whose panel had closed), Riverpod disposed the provider — closing
/// its Dio — during the settings/rig awaits inside [createImagingRun], the
/// create then died with "Can't establish connection after the adapter was
/// closed", and the target was SAVED AS AN OFFLINE DRAFT while the status bar
/// said connected. createImagingRun now pins the provider with a listen for
/// the whole create; this drives it end to end against a client that behaves
/// like Dio after close() and insists on a LIVE create.

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

/// Mimics Dio: once [close] has run, the next request fails at the transport
/// layer (no response), which createImagingRun degrades to an offline draft.
class _ClosableClient implements SequenceClient {
  bool closed = false;
  final created = <String>[];

  @override
  void close() => closed = true;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
      {String? description, String? idempotencyKey}) async {
    if (closed) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/sequences'),
        type: DioExceptionType.connectionError,
        message: "Can't establish connection after the adapter was closed.",
      );
    }
    created.add(name);
    return 'seq-${created.length}';
  }

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  testWidgets(
      'rig 2026-10-04: connected create with NO other listener on '
      'sequenceApiProvider stays LIVE (not an offline draft)', (tester) async {
    final drafts = _MemDraftService();
    final client = _ClosableClient();
    // Keep the real provider's autoDispose + onDispose(close) contract.
    final container = ProviderContainer(overrides: [
      draftSequenceServiceProvider.overrideWithValue(drafts),
      sequenceApiProvider.overrideWith((ref) {
        ref.onDispose(client.close);
        return client;
      }),
    ]);
    addTearDown(container.dispose);

    ImagingRunResult? result;
    var done = false;
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Scaffold(
          body: Consumer(
            builder: (context, ref, _) => ElevatedButton(
              onPressed: () => createImagingRun(ref,
                      raDeg: 10.7, decDeg: 41.3, targetName: 'M 31')
                  .then((r) {
                result = r;
                done = true;
              }),
              child: const Text('go'),
            ),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('go'));
    // Each pump turns the event loop — exactly the turns that let Riverpod
    // dispose an unlistened autoDispose provider mid-create.
    for (var i = 0; i < 10 && !done; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    expect(done, isTrue, reason: 'createImagingRun must finish');

    expect(client.created, ['M 31'],
        reason: 'the create must reach the daemon on a still-open client');
    expect(result, isNotNull);
    expect(result!.draft, isFalse,
        reason: 'a connected create must not degrade to an offline draft');
    expect(result!.sequenceId, 'seq-1');
    expect(drafts.store, isEmpty);
    // The pin is released once the create returns.
    await tester.pump();
    expect(client.closed, isTrue);
  });
}
