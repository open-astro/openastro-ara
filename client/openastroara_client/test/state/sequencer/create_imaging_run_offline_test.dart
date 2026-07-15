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

/// In-memory draft store — widget tests can't await real file IO.
class _MemDraftService extends DraftSequenceService {
  final Map<String, DraftSequence> store = {};
  int _n = 0;

  @override
  String newId() => '$draftIdPrefix mem-${_n++}'.replaceAll(' ', '');

  @override
  Future<List<DraftSequence>> loadAll() async => store.values.toList();

  @override
  Future<void> save(DraftSequence draft) async => store[draft.id] = draft;

  @override
  Future<void> delete(String id) async => store.remove(id);
}

/// Daemon client whose create() throws a configurable error.
class _ThrowingClient implements SequenceClient {
  _ThrowingClient(this.error);
  final Object error;

  @override
  Future<String> create(String name, Map<String, dynamic> body,
          {String? description}) async =>
      throw error;

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  late _MemDraftService drafts;

  setUp(() {
    drafts = _MemDraftService();
  });

  /// Mounts a Consumer so createImagingRun gets a real WidgetRef + context,
  /// runs it, and returns (result, error). Errors are captured AT CREATION —
  /// an error completing before a listener attaches would otherwise be
  /// reported by the test binding as an uncaught in-test exception.
  Future<({ImagingRunResult? result, Object? error})> run(
    WidgetTester tester, {
    required SequenceClient? api,
  }) async {
    final container = ProviderContainer(overrides: [
      draftSequenceServiceProvider.overrideWithValue(drafts),
      sequenceApiProvider.overrideWith((ref) => api),
    ]);
    addTearDown(container.dispose);
    Future<({ImagingRunResult? result, Object? error})>? pending;
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        home: Consumer(
          builder: (context, ref, _) => ElevatedButton(
            onPressed: () => pending = createImagingRun(ref,
                    raDeg: 10.7, decDeg: 41.3, targetName: 'M 31')
                .then<({ImagingRunResult? result, Object? error})>(
                    (r) => (result: r, error: null),
                    onError: (Object e) => (result: null, error: e)),
            child: const Text('go'),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('go'));
    await tester.pump();
    final outcome = await tester.runAsync(() => pending!);
    return outcome!;
  }

  testWidgets('no server: saves a local draft and reports draft:true',
      (tester) async {
    final outcome = await run(tester, api: null);
    final result = outcome.result;
    expect(outcome.error, isNull);
    expect(result, isNotNull);
    expect(result!.draft, isTrue);
    expect(isDraftSequenceId(result.sequenceId), isTrue);
    expect(drafts.store, hasLength(1));
    expect(drafts.store.values.single.name, 'M 31');
    // The draft body is a real run body, not a placeholder.
    expect(drafts.store.values.single.body, isNotEmpty);
  });

  testWidgets(
      'unreachable daemon (DioException without response) degrades to a draft',
      (tester) async {
    final api = _ThrowingClient(DioException(
      requestOptions: RequestOptions(path: '/api/v1/sequences'),
      type: DioExceptionType.connectionError,
    ));
    final outcome = await run(tester, api: api);
    expect(outcome.error, isNull);
    expect(outcome.result?.draft, isTrue);
    expect(drafts.store, hasLength(1));
  });

  testWidgets(
      'daemon rejection (DioException WITH a response) rethrows — no silent draft',
      (tester) async {
    final api = _ThrowingClient(DioException(
      requestOptions: RequestOptions(path: '/api/v1/sequences'),
      response: Response(
          requestOptions: RequestOptions(path: '/api/v1/sequences'),
          statusCode: 422),
      type: DioExceptionType.badResponse,
    ));
    final outcome = await run(tester, api: api);
    expect(outcome.result, isNull);
    expect(outcome.error, isA<DioException>());
    expect((outcome.error as DioException).response?.statusCode, 422);
    expect(drafts.store, isEmpty);
  });
}
