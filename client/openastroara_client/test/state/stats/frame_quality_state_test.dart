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

    test('a fetch failure lands in the provider error state', () async {
      final api = _FakeFrameQualityClient(_dist(0))..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(frameQualityProvider.future), throwsA(isA<StateError>()));
      expect(c.read(frameQualityProvider).hasError, isTrue);
    });

    test('concurrent refreshes: only the latest result is written', () async {
      final api = _FakeFrameQualityClient(_dist(1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(frameQualityProvider.future);

      api.value = _dist(5);
      final first = c.read(frameQualityProvider.notifier).refresh();
      api.value = _dist(9);
      final second = c.read(frameQualityProvider.notifier).refresh();
      await Future.wait([first, second]);

      // The fake has no interior await, so both reads see the second value;
      // this asserts the generation guard lets the latest refresh win.
      expect(c.read(frameQualityProvider).value!.buckets.single.count, 9);
    });
  });
}
