import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/stats_overview.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/stats_overview_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/stats_overview_state.dart';

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

class _FakeStatsOverviewClient implements StatsOverviewClient {
  _FakeStatsOverviewClient(this.value);
  StatsOverview value;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<StatsOverview> fetch() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, StatsOverviewClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    statsOverviewApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('statsOverviewProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview());
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(statsOverviewProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server overview', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview(totalFrames: 10, totalSessions: 2));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final o = await c.read(statsOverviewProvider.future);
      expect(o!.totalFrames, 10);
      expect(o.totalSessions, 2);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview(totalFrames: 1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsOverviewProvider.future);

      api.value = const StatsOverview(totalFrames: 9);
      await c.read(statsOverviewProvider.notifier).refresh();
      expect(c.read(statsOverviewProvider).value!.totalFrames, 9);
      expect(api.fetches, 2);
    });

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview())..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(statsOverviewProvider.future), throwsA(isA<StateError>()));
      expect(c.read(statsOverviewProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior data and rethrows (no blanking)', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview(totalFrames: 7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsOverviewProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(statsOverviewProvider.notifier).refresh(), throwsA(isA<StateError>()));
      // State still holds the last-good totals (the widget shows a stale banner).
      expect(c.read(statsOverviewProvider).hasError, isFalse);
      expect(c.read(statsOverviewProvider).value!.totalFrames, 7);
    });

    test('a successful refresh swaps the new totals in without a loading flash', () async {
      final api = _FakeStatsOverviewClient(const StatsOverview(totalFrames: 1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsOverviewProvider.future);

      api.value = const StatsOverview(totalFrames: 9);
      final future = c.read(statsOverviewProvider.notifier).refresh();
      // refresh() never drops to a loading state — the prior data stays readable
      // throughout, then the new value swaps in.
      expect(c.read(statsOverviewProvider).isLoading, isFalse);
      expect(c.read(statsOverviewProvider).value!.totalFrames, 1);
      await future;
      expect(c.read(statsOverviewProvider).value!.totalFrames, 9);
    });
  });
}
