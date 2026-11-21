import 'dart:async';

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

class _GatedBestFramesClient implements BestFramesClient {
  final List<Completer<List<BestFrame>>> calls = [];

  @override
  Future<List<BestFrame>> fetch() {
    final c = Completer<List<BestFrame>>();
    calls.add(c);
    return c.future;
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

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeBestFramesClient(const [])..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(bestFramesProvider.future), throwsA(isA<StateError>()));
      expect(c.read(bestFramesProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior list and rethrows (no blanking)', () async {
      final api = _FakeBestFramesClient(const [BestFrame(frameId: 'a')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(bestFramesProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(bestFramesProvider.notifier).refresh(), throwsA(isA<StateError>()));
      expect(c.read(bestFramesProvider).hasError, isFalse);
      expect(c.read(bestFramesProvider).value!.map((f) => f.frameId), ['a']);
    });

    test('a successful refresh swaps the new list in without a loading flash', () async {
      final api = _FakeBestFramesClient(const [BestFrame(frameId: 'a')]);
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(bestFramesProvider.future);

      api.frames = const [BestFrame(frameId: 'x'), BestFrame(frameId: 'y')];
      final future = c.read(bestFramesProvider.notifier).refresh();
      expect(c.read(bestFramesProvider).isLoading, isFalse);
      expect(c.read(bestFramesProvider).value!.map((f) => f.frameId), ['a']);
      await future;
      expect(c.read(bestFramesProvider).value!.map((f) => f.frameId), ['x', 'y']);
    });

    test('a server switch mid-refresh discards the stale result (generation guard)', () async {
      final api = _GatedBestFramesClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(bestFramesProvider.future);
      api.calls[0].complete(const [BestFrame(frameId: 'a')]);
      await built;

      final notifier = c.read(bestFramesProvider.notifier);
      final refreshing = notifier.refresh(); // captures generation; calls[1] pending
      notifier.markBuild(); // stand in for a server-switch build() re-run
      api.calls[1].complete(const [BestFrame(frameId: 'STALE')]); // now stale
      await refreshing;

      expect(c.read(bestFramesProvider).value!.map((f) => f.frameId), ['a']);
    });
  });
}
