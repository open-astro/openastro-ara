import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/best_frame.dart';
import 'package:openastroara/services/best_frames_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/best_frames_state.dart';

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

class _FakeBestFramesClient implements BestFramesClient {
  _FakeBestFramesClient(this.frames);
  List<BestFrame> frames;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<List<BestFrame>> fetch() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return frames;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, BestFramesClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    bestFramesApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('bestFramesProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeBestFramesClient(const []);
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(bestFramesProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server best frames', () async {
      final api = _FakeBestFramesClient(const [
        BestFrame(frameId: 'a', compositeScore: 0.9),
        BestFrame(frameId: 'b', compositeScore: 0.8),
      ]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final list = await c.read(bestFramesProvider.future);
      expect(list!.map((f) => f.frameId), ['a', 'b']);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeBestFramesClient(const [BestFrame(frameId: 'a')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(bestFramesProvider.future);

      api.frames = const [BestFrame(frameId: 'a'), BestFrame(frameId: 'c')];
      await c.read(bestFramesProvider.notifier).refresh();
      expect(c.read(bestFramesProvider).value!.length, 2);
      expect(api.fetches, 2);
    });

    test('a fetch failure lands in the provider error state', () async {
      final api = _FakeBestFramesClient(const [])..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(bestFramesProvider.future), throwsA(isA<StateError>()));
      expect(c.read(bestFramesProvider).hasError, isTrue);
    });

    test('concurrent refreshes: only the latest result is written', () async {
      final api = _FakeBestFramesClient(const [BestFrame(frameId: 'a')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(bestFramesProvider.future);

      api.frames = const [BestFrame(frameId: 'a'), BestFrame(frameId: 'b')];
      final first = c.read(bestFramesProvider.notifier).refresh();
      api.frames = const [BestFrame(frameId: 'z')];
      final second = c.read(bestFramesProvider.notifier).refresh();
      await Future.wait([first, second]);

      // The fake has no interior await, so both reads see ['z']; this asserts
      // the generation guard lets the latest refresh win and discards the
      // first's now-stale-token write.
      expect(c.read(bestFramesProvider).value!.map((f) => f.frameId), ['z']);
    });
  });
}
