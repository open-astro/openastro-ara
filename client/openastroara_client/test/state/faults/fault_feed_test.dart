import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/fault_row.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/faults_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/faults/fault_feed_state.dart';
import 'package:openastroara/state/faults/faults_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
}

FaultRow _row(String id, {String? sessionId}) => FaultRow(
      id: id,
      sessionId: sessionId,
      detectedUtc: DateTime.utc(2026, 7, 10, 4),
      equipmentType: 'camera',
      equipmentId: 'dev-1',
      equipmentName: 'Cam',
      faultType: 'disconnected',
      details: null,
      actionTaken: null,
      resolvedUtc: null,
    );

/// Serves two fixed pages so pagination can be exercised; records the filters
/// each call carried.
class _FakePagedFaultsClient implements FaultsClient {
  final List<Map<String, dynamic>> calls = <Map<String, dynamic>>[];

  @override
  Future<CursorPage<FaultRow>> list({
    int limit = 200,
    String? cursor,
    String? equipmentType,
    String? faultType,
    String? sessionId,
    bool? unresolvedOnly,
  }) async {
    calls.add({'cursor': cursor, 'sessionId': sessionId});
    if (sessionId != null) {
      return CursorPage(
          items: [_row('s1', sessionId: sessionId)],
          nextCursor: null,
          hasMore: false);
    }
    return cursor == null
        ? CursorPage(
            items: [_row('f1'), _row('f2')], nextCursor: '2', hasMore: true)
        : CursorPage(items: [_row('f3')], nextCursor: null, hasMore: false);
  }

  @override
  Future<FaultRow?> getById(String id) async => null;

  @override
  void close() {}
}

ProviderContainer _container(FaultsClient api) {
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(
        _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
    faultsApiFactoryProvider.overrideWithValue((server) => api),
  ]);
  addTearDown(container.dispose);
  return container;
}

void main() {
  group('faultFeedProvider', () {
    test('build lists page 1, loadMore appends, refresh replaces', () async {
      final api = _FakePagedFaultsClient();
      final container = _container(api);
      await container.read(savedServersProvider.future);

      final sub = container.listen(faultFeedProvider, (_, _) {});
      addTearDown(sub.close);
      final first = await container.read(faultFeedProvider.future);
      expect(first!.map((f) => f.id), ['f1', 'f2']);
      expect(container.read(faultFeedProvider.notifier).hasMore, isTrue);

      await container.read(faultFeedProvider.notifier).loadMore();
      expect(container.read(faultFeedProvider).value!.map((f) => f.id),
          ['f1', 'f2', 'f3']);
      expect(container.read(faultFeedProvider.notifier).hasMore, isFalse);

      await container.read(faultFeedProvider.notifier).refresh();
      expect(container.read(faultFeedProvider).value!.map((f) => f.id),
          ['f1', 'f2'], reason: 'refresh restarts from page 1');
    });

    test('no saved server → null data', () async {
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [])),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);
      final sub = container.listen(faultFeedProvider, (_, _) {});
      addTearDown(sub.close);
      expect(await container.read(faultFeedProvider.future), isNull);
    });
  });

  group('sessionFaultsProvider', () {
    test('filters by the session id', () async {
      final api = _FakePagedFaultsClient();
      final container = _container(api);
      await container.read(savedServersProvider.future);

      final rows =
          await container.read(sessionFaultsProvider('sess-9').future);
      expect(rows.single.sessionId, 'sess-9');
      expect(api.calls.single['sessionId'], 'sess-9');
    });
  });
}
