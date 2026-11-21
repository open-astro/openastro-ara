import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/stats_export_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';

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

class _FakeStatsExportClient implements StatsExportClient {
  _FakeStatsExportClient(this.targets);
  List<StatsTarget> targets;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<List<StatsTarget>> fetchTargets() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return targets;
  }

  @override
  Future<String> fetchCsv(String scope) async => 'header\n';

  @override
  String astrobinExportUrl(String target) => 'http://h:5555/api/v1/stats/export/astrobin?target=$target';

  @override
  void close() {}
}

class _GatedStatsExportClient implements StatsExportClient {
  final List<Completer<List<StatsTarget>>> calls = [];

  @override
  Future<List<StatsTarget>> fetchTargets() {
    final c = Completer<List<StatsTarget>>();
    calls.add(c);
    return c.future;
  }

  @override
  Future<String> fetchCsv(String scope) async => 'header\n';

  @override
  String astrobinExportUrl(String target) => 'http://h:5555/x?target=$target';

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, StatsExportClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    statsExportApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('statsTargetsProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeStatsExportClient(const []);
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(statsTargetsProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server targets', () async {
      final api = _FakeStatsExportClient(const [
        StatsTarget(targetName: 'M31', frameCount: 5),
        StatsTarget(targetName: 'M42', frameCount: 3),
      ]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final list = await c.read(statsTargetsProvider.future);
      expect(list, isNotNull);
      expect(list!.map((t) => t.targetName), ['M31', 'M42']);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeStatsExportClient(const [StatsTarget(targetName: 'M31')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsTargetsProvider.future);

      api.targets = const [StatsTarget(targetName: 'M31'), StatsTarget(targetName: 'M81')];
      await c.read(statsTargetsProvider.notifier).refresh();
      expect(c.read(statsTargetsProvider).value!.length, 2);
      expect(api.fetches, 2);
    });

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeStatsExportClient(const [])..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(statsTargetsProvider.future), throwsA(isA<StateError>()));
      expect(c.read(statsTargetsProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior list and rethrows (no blanking)', () async {
      final api = _FakeStatsExportClient(const [StatsTarget(targetName: 'M31')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsTargetsProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(statsTargetsProvider.notifier).refresh(), throwsA(isA<StateError>()));
      expect(c.read(statsTargetsProvider).hasError, isFalse);
      expect(c.read(statsTargetsProvider).value!.map((t) => t.targetName), ['M31']);
    });

    test('a successful refresh swaps the new list in without a loading flash', () async {
      final api = _FakeStatsExportClient(const [StatsTarget(targetName: 'M31')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(statsTargetsProvider.future);

      api.targets = const [StatsTarget(targetName: 'M81'), StatsTarget(targetName: 'M82')];
      final future = c.read(statsTargetsProvider.notifier).refresh();
      expect(c.read(statsTargetsProvider).isLoading, isFalse);
      expect(c.read(statsTargetsProvider).value!.map((t) => t.targetName), ['M31']);
      await future;
      expect(c.read(statsTargetsProvider).value!.map((t) => t.targetName), ['M81', 'M82']);
    });

    test('a server switch mid-refresh discards the stale result (generation guard)', () async {
      final api = _GatedStatsExportClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(statsTargetsProvider.future);
      api.calls[0].complete(const [StatsTarget(targetName: 'M31')]);
      await built;

      final notifier = c.read(statsTargetsProvider.notifier);
      final refreshing = notifier.refresh(); // captures generation; calls[1] pending
      notifier.markBuild(); // stand in for a server-switch build() re-run
      api.calls[1].complete(const [StatsTarget(targetName: 'STALE')]); // now stale
      await refreshing;

      expect(c.read(statsTargetsProvider).value!.map((t) => t.targetName), ['M31']);
    });
  });
}
