import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/guiding_rms.dart';
import 'package:openastroara/services/guiding_rms_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/guiding_rms_state.dart';

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

class _FakeGuidingRmsClient implements GuidingRmsClient {
  _FakeGuidingRmsClient(this.value);
  GuidingRmsSeries value;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<GuidingRmsSeries> fetch() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

/// A fake whose `fetch()` resolves only when the test completes it, in whatever
/// order it chooses — so the concurrent-refresh test can force the *earlier*
/// refresh to resolve *after* the newer one (the only ordering that actually
/// exercises the generation guard).
class _GatedGuidingRmsClient implements GuidingRmsClient {
  final List<Completer<GuidingRmsSeries>> calls = [];

  @override
  Future<GuidingRmsSeries> fetch() {
    final c = Completer<GuidingRmsSeries>();
    calls.add(c);
    return c.future;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

GuidingRmsSeries _series(double mean) =>
    GuidingRmsSeries(samples: const [GuidingRmsPoint(rmsArcsec: 1.0)], meanRmsArcsec: mean);

ProviderContainer _container(List<AraServer> servers, GuidingRmsClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    guidingRmsApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('guidingRmsProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeGuidingRmsClient(_series(0));
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(guidingRmsProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server series', () async {
      final api = _FakeGuidingRmsClient(_series(0.9));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final s = await c.read(guidingRmsProvider.future);
      expect(s!.meanRmsArcsec, 0.9);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeGuidingRmsClient(_series(0.9));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(guidingRmsProvider.future);

      api.value = _series(1.4);
      await c.read(guidingRmsProvider.notifier).refresh();
      expect(c.read(guidingRmsProvider).value!.meanRmsArcsec, 1.4);
      expect(api.fetches, 2);
    });

    test('a fetch failure lands in the provider error state', () async {
      final api = _FakeGuidingRmsClient(_series(0))..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(guidingRmsProvider.future), throwsA(isA<StateError>()));
      expect(c.read(guidingRmsProvider).hasError, isTrue);
    });

    test('a slow earlier refresh cannot clobber a newer one (generation guard)', () async {
      final api = _GatedGuidingRmsClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(guidingRmsProvider.future);
      api.calls[0].complete(_series(0.9));
      await built;

      final first = c.read(guidingRmsProvider.notifier).refresh();
      final second = c.read(guidingRmsProvider.notifier).refresh();
      expect(api.calls.length, 3);

      // Resolve the NEWER refresh first, then the OLDER one after — without the
      // guard the late older write would clobber state with the stale value.
      api.calls[2].complete(_series(2.0));
      api.calls[1].complete(_series(1.0));
      await Future.wait([first, second]);

      expect(c.read(guidingRmsProvider).value!.meanRmsArcsec, 2.0);
    });
  });
}
