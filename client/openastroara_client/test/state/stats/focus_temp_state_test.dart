import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/focus_temp.dart';
import 'package:openastroara/services/focus_temp_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/focus_temp_state.dart';

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

class _FakeFocusTempClient implements FocusTempClient {
  _FakeFocusTempClient(this.value);
  FocusTempSeries value;
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<FocusTempSeries> fetch() async {
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
class _GatedFocusTempClient implements FocusTempClient {
  final List<Completer<FocusTempSeries>> calls = [];

  @override
  Future<FocusTempSeries> fetch() {
    final c = Completer<FocusTempSeries>();
    calls.add(c);
    return c.future;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

FocusTempSeries _series(double? r2) => FocusTempSeries(
      samples: const [FocusTempPoint(temperatureC: 5.0, focuserPosition: 1000)],
      correlationR2: r2,
    );

ProviderContainer _container(List<AraServer> servers, FocusTempClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    focusTempApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('focusTempProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeFocusTempClient(_series(null));
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(focusTempProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server scatter', () async {
      final api = _FakeFocusTempClient(_series(0.9));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final s = await c.read(focusTempProvider.future);
      expect(s!.correlationR2, 0.9);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeFocusTempClient(_series(0.9));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(focusTempProvider.future);

      api.value = _series(0.4);
      await c.read(focusTempProvider.notifier).refresh();
      expect(c.read(focusTempProvider).value!.correlationR2, 0.4);
      expect(api.fetches, 2);
    });

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeFocusTempClient(_series(null))..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(focusTempProvider.future), throwsA(isA<StateError>()));
      expect(c.read(focusTempProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior scatter and rethrows (no blanking)', () async {
      final api = _FakeFocusTempClient(_series(0.7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(focusTempProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(focusTempProvider.notifier).refresh(), throwsA(isA<StateError>()));
      expect(c.read(focusTempProvider).hasError, isFalse);
      expect(c.read(focusTempProvider).value!.correlationR2, 0.7);
    });

    test('a successful refresh swaps the new scatter in without a loading flash', () async {
      final api = _FakeFocusTempClient(_series(0.7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(focusTempProvider.future);

      api.value = _series(0.4);
      final future = c.read(focusTempProvider.notifier).refresh();
      expect(c.read(focusTempProvider).isLoading, isFalse);
      expect(c.read(focusTempProvider).value!.correlationR2, 0.7);
      await future;
      expect(c.read(focusTempProvider).value!.correlationR2, 0.4);
    });

    test('a server switch mid-refresh discards the stale result (generation guard)', () async {
      final api = _GatedFocusTempClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(focusTempProvider.future);
      api.calls[0].complete(_series(0.9));
      await built;

      final notifier = c.read(focusTempProvider.notifier);
      final refreshing = notifier.refresh(); // captures generation; calls[1] pending
      notifier.markBuild(); // stand in for a server-switch build() re-run
      api.calls[1].complete(_series(0.1)); // now stale
      await refreshing;

      expect(c.read(focusTempProvider).value!.correlationR2, 0.9);
    });

    test('a refresh that FAILS after a server switch is swallowed, not rethrown', () async {
      final api = _GatedFocusTempClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(focusTempProvider.future);
      api.calls[0].complete(_series(0.9));
      await built;

      final notifier = c.read(focusTempProvider.notifier);
      final refreshing = notifier.refresh(); // calls[1] pending
      notifier.markBuild(); // server switch bumps the generation
      api.calls[1].completeError(StateError('boom')); // stale failure

      // The generation mismatch means the stale error is discarded, not
      // rethrown — so the widget can't flash a stale chip over the new data.
      await expectLater(refreshing, completes);
      expect(c.read(focusTempProvider).value!.correlationR2, 0.9);
    });
  });
}
