import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/frame_quality.dart';
import 'package:openastroara/services/frame_quality_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/frame_quality_state.dart';

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

class _FakeFrameQualityClient implements FrameQualityClient {
  _FakeFrameQualityClient(this.value);
  FrameQualityDistribution value;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<FrameQualityDistribution> fetch({String? filter}) async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

/// A fake whose `fetch()` resolves only when the test completes it, so the
/// generation-guard test can hold a refresh open while a (simulated) server
/// switch re-runs build().
class _GatedFrameQualityClient implements FrameQualityClient {
  final List<Completer<FrameQualityDistribution>> calls = [];

  @override
  Future<FrameQualityDistribution> fetch({String? filter}) {
    final c = Completer<FrameQualityDistribution>();
    calls.add(c);
    return c.future;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

FrameQualityDistribution _dist(int count) => FrameQualityDistribution(
      buckets: [FrameQualityBucket(rangeLow: 0.9, rangeHigh: 1.0, count: count)],
      meanScore: 0.95,
    );

ProviderContainer _container(List<AraServer> servers, FrameQualityClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    frameQualityApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('frameQualityProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeFrameQualityClient(_dist(0));
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(frameQualityProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server distribution', () async {
      final api = _FakeFrameQualityClient(_dist(7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final d = await c.read(frameQualityProvider.future);
      expect(d!.buckets.single.count, 7);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeFrameQualityClient(_dist(1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(frameQualityProvider.future);

      api.value = _dist(9);
      await c.read(frameQualityProvider.notifier).refresh();
      expect(c.read(frameQualityProvider).value!.buckets.single.count, 9);
      expect(api.fetches, 2);
    });

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeFrameQualityClient(_dist(0))..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(frameQualityProvider.future), throwsA(isA<StateError>()));
      expect(c.read(frameQualityProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior distribution and rethrows (no blanking)', () async {
      final api = _FakeFrameQualityClient(_dist(7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(frameQualityProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(frameQualityProvider.notifier).refresh(), throwsA(isA<StateError>()));
      expect(c.read(frameQualityProvider).hasError, isFalse);
      expect(c.read(frameQualityProvider).value!.buckets.single.count, 7);
    });

    test('a successful refresh swaps the new distribution in without a loading flash', () async {
      final api = _FakeFrameQualityClient(_dist(1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(frameQualityProvider.future);

      api.value = _dist(9);
      final future = c.read(frameQualityProvider.notifier).refresh();
      expect(c.read(frameQualityProvider).isLoading, isFalse);
      expect(c.read(frameQualityProvider).value!.buckets.single.count, 1);
      await future;
      expect(c.read(frameQualityProvider).value!.buckets.single.count, 9);
    });

    test('a server switch mid-refresh discards the stale result (generation guard)', () async {
      final api = _GatedFrameQualityClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(frameQualityProvider.future);
      api.calls[0].complete(_dist(1));
      await built;

      final notifier = c.read(frameQualityProvider.notifier);
      final refreshing = notifier.refresh(); // captures generation; calls[1] pending
      notifier.markBuild(); // stand in for a server-switch build() re-run
      api.calls[1].complete(_dist(99)); // now stale
      await refreshing;

      expect(c.read(frameQualityProvider).value!.buckets.single.count, 1);
    });
  });
}
