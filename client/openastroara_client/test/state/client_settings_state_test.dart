import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/client_settings.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/client_settings_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/client_settings_state.dart';
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

class _FakeClientSettingsClient implements ClientSettingsClient {
  _FakeClientSettingsClient(this.stored);
  Map<String, dynamic> stored;
  int fetches = 0;
  Map<String, dynamic>? lastReplaced;
  Map<String, dynamic>? lastMerged;
  bool throwOnFetch = false;
  bool throwOnReplace = false;
  int _clock = 0;

  @override
  Future<ClientSettings> fetch() async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return ClientSettings(settings: Map<String, dynamic>.unmodifiable(stored));
  }

  @override
  Future<ClientSettings> replace(Map<String, dynamic> settings) async {
    if (throwOnReplace) throw StateError('rejected');
    lastReplaced = settings;
    stored = settings;
    return ClientSettings(
        settings: Map<String, dynamic>.unmodifiable(settings),
        updatedUtc: DateTime.utc(2026, 6, 14, 12));
  }

  /// Like the daemon: only the given top-level keys change, null removes one,
  /// and every write gets a fresh stamp.
  @override
  Future<ClientSettings> merge(Map<String, dynamic> patch) async {
    if (throwOnReplace) throw StateError('rejected');
    lastMerged = patch;
    final next = {...stored};
    patch.forEach((k, v) => v == null ? next.remove(k) : next[k] = v);
    stored = next;
    return ClientSettings(
        settings: Map<String, dynamic>.unmodifiable(next),
        updatedUtc: DateTime.utc(2026, 6, 14, 12).add(Duration(seconds: ++_clock)));
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

ProviderContainer _container(List<AraServer> servers, ClientSettingsClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    clientSettingsApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('clientSettingsProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeClientSettingsClient(const {});
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(clientSettingsProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('reads the active server settings on build', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final cs = await c.read(clientSettingsProvider.future);
      expect(cs!.settings['theme'], 'dark');
      expect(api.fetches, 1);
    });

    test('save replaces the blob and lands the stored view', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      final saved = await c.read(clientSettingsProvider.notifier).save({'theme': 'light', 'zoom': 2});
      expect(saved!.settings['theme'], 'light');
      expect(api.lastReplaced, {'theme': 'light', 'zoom': 2});
      expect(c.read(clientSettingsProvider).value!.settings['zoom'], 2);
    });

    test('merge sends only the patch to the rig and lands the stored view', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark', 'zoom': 1});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      final merged = await c.read(clientSettingsProvider.notifier).merge({'zoom': 3, 'theme': null});
      expect(api.lastMerged, {'zoom': 3, 'theme': null}, reason: 'only the patch goes over the wire');
      expect(api.lastReplaced, isNull, reason: 'no whole-blob replace');
      expect(merged!.settings, {'zoom': 3});
      expect(c.read(clientSettingsProvider).value!.settings, {'zoom': 3});
    });

    test('merge works before the first fetch has landed (the rig merges, not the client)', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'})..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(clientSettingsProvider.future), throwsA(isA<StateError>()));

      await c.read(clientSettingsProvider.notifier).merge({'zoom': 3});
      expect(api.stored, {'theme': 'dark', 'zoom': 3}, reason: 'the rig kept the keys the patch did not name');
    });

    test('a client_settings.updated event refetches', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      api.stored = {'theme': 'light'}; // another device saved
      await c.read(clientSettingsProvider.notifier).onUpdatedEvent(DateTime.utc(2030));
      expect(api.fetches, 2);
      expect(c.read(clientSettingsProvider).value!.settings['theme'], 'light');
    });

    test("the event for this device's own save is not refetched", () async {
      final api = _FakeClientSettingsClient(const {});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      final mine = await c.read(clientSettingsProvider.notifier).merge({'a': 1});
      await c.read(clientSettingsProvider.notifier).onUpdatedEvent(mine!.updatedUtc);
      expect(api.fetches, 1, reason: 'initial fetch only: the saver keeps its own screen');
    });

    test('a failed refetch keeps the held settings', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'});
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      api.throwOnFetch = true;
      await c.read(clientSettingsProvider.notifier).refetch();
      expect(c.read(clientSettingsProvider).value!.settings['theme'], 'dark');
    });

    test('merge is a no-op (null) when no server is bound', () async {
      final api = _FakeClientSettingsClient(const {});
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);
      expect(await c.read(clientSettingsProvider.notifier).merge({'a': 1}), isNull);
      expect(api.lastReplaced, isNull);
    });

    test('actions are no-ops (null) when no server is bound', () async {
      final api = _FakeClientSettingsClient(const {});
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);
      expect(await c.read(clientSettingsProvider.notifier).save({'a': 1}), isNull);
    });

    test('a fetch failure lands in the provider error state', () async {
      final api = _FakeClientSettingsClient(const {})..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(clientSettingsProvider.future), throwsA(isA<StateError>()));
      expect(c.read(clientSettingsProvider).hasError, isTrue);
    });

    test('a save failure propagates (and does not blank state)', () async {
      final api = _FakeClientSettingsClient(const {'theme': 'dark'})..throwOnReplace = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(clientSettingsProvider.future);

      await expectLater(
        c.read(clientSettingsProvider.notifier).save({'theme': 'light'}),
        throwsA(isA<StateError>()),
      );
      expect(c.read(clientSettingsProvider).value!.settings['theme'], 'dark', reason: 'old state retained');
    });
  });
}
