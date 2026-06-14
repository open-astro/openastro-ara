import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/achievements.dart';
import 'package:openastroara/services/achievements_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/achievements_state.dart';

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

class _FakeAchievementsClient implements AchievementsClient {
  _FakeAchievementsClient(this.value);
  StatsAchievements value;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<StatsAchievements> fetch() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, AchievementsClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    achievementsApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('achievementsProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeAchievementsClient(const StatsAchievements());
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(achievementsProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server achievements', () async {
      final api = _FakeAchievementsClient(
          const StatsAchievements(totalLightFrames: 5, totalNightsImaged: 3));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final a = await c.read(achievementsProvider.future);
      expect(a, isNotNull);
      expect(a!.totalLightFrames, 5);
      expect(a.totalNightsImaged, 3);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeAchievementsClient(const StatsAchievements(totalLightFrames: 1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(achievementsProvider.future);

      api.value = const StatsAchievements(totalLightFrames: 9);
      await c.read(achievementsProvider.notifier).refresh();
      expect(c.read(achievementsProvider).value!.totalLightFrames, 9);
      expect(api.fetches, 2);
    });

    test('an initial fetch failure lands in the provider error state', () async {
      final api = _FakeAchievementsClient(const StatsAchievements())..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(achievementsProvider.future), throwsA(isA<StateError>()));
      expect(c.read(achievementsProvider).hasError, isTrue);
    });

    test('a refresh failure rethrows and keeps the last-good data on screen', () async {
      final api = _FakeAchievementsClient(const StatsAchievements(totalLightFrames: 4));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(achievementsProvider.future);

      api.throwOnFetch = true;
      await expectLater(
        c.read(achievementsProvider.notifier).refresh(),
        throwsA(isA<StateError>()),
      );
      final state = c.read(achievementsProvider);
      expect(state.hasError, isFalse, reason: 'refresh failure must not blank the records');
      expect(state.value!.totalLightFrames, 4, reason: 'last-good data is retained');
    });
  });
}
