import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_equipment_choices.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_equipment_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_equipment_state.dart';
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

class _FakeEquipmentClient implements GuiderEquipmentClient {
  _FakeEquipmentClient(this.response);
  GuiderEquipmentChoicesResponse response;
  List<String> discovered = const ['192.168.1.20:11111'];
  int choicesReads = 0;
  int discoveries = 0;
  int pushes = 0;
  int? lastNumQueries;
  int? lastTimeoutSeconds;
  bool throwOnDiscover = false;
  bool throwOnPush = false;

  @override
  Future<GuiderEquipmentChoicesResponse> getChoices() async {
    choicesReads++;
    return response;
  }

  @override
  Future<List<String>> discoverAlpaca({
    int? numQueries,
    int? timeoutSeconds,
  }) async {
    discoveries++;
    lastNumQueries = numQueries;
    lastTimeoutSeconds = timeoutSeconds;
    if (throwOnDiscover) throw StateError('discovery failed');
    return discovered;
  }

  @override
  Future<void> pushProfile() async {
    pushes++;
    if (throwOnPush) throw StateError('push failed');
  }

  @override
  void close() {}
}

ProviderContainer _container(List<AraServer> servers, GuiderEquipmentClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    guiderEquipmentApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

GuiderEquipmentChoicesResponse _resp({bool connected = true}) =>
    GuiderEquipmentChoicesResponse(
      connected: connected,
      choices: connected
          ? const GuiderEquipmentChoices(cameras: ['Simulator'])
          : null,
    );

void main() {
  const server = AraServer(hostname: 'h', port: 5555);

  group('guiderEquipmentProvider', () {
    test('no saved server → null', () async {
      final c = _container(const [], _FakeEquipmentClient(_resp()));
      await c.read(savedServersProvider.future);
      expect(c.read(guiderEquipmentApiProvider), isNull);
      expect(await c.read(guiderEquipmentProvider.future), isNull);
    });

    test('active server → exposes the choices envelope', () async {
      final c = _container(const [server], _FakeEquipmentClient(_resp()));
      await c.read(savedServersProvider.future);
      final r = await c.read(guiderEquipmentProvider.future);
      expect(r!.connected, isTrue);
      expect(r.choices!.cameras, ['Simulator']);
    });

    test('refresh re-reads the choices', () async {
      final api = _FakeEquipmentClient(_resp(connected: false));
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      final first = await c.read(guiderEquipmentProvider.future);
      expect(first!.connected, isFalse);

      api.response = _resp();
      await c.read(guiderEquipmentProvider.notifier).refresh();

      expect(api.choicesReads, 2);
      expect(c.read(guiderEquipmentProvider).value!.connected, isTrue);
    });

    test('discoverAlpaca forwards the bounds and returns the servers', () async {
      final api = _FakeEquipmentClient(_resp());
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderEquipmentProvider.future);

      final servers = await c
          .read(guiderEquipmentProvider.notifier)
          .discoverAlpaca(numQueries: 3, timeoutSeconds: 5);

      expect(servers, ['192.168.1.20:11111']);
      expect(api.discoveries, 1);
      expect(api.lastNumQueries, 3);
      expect(api.lastTimeoutSeconds, 5);
    });

    test('discoverAlpaca surfaces the client error to the caller', () async {
      final api = _FakeEquipmentClient(_resp())..throwOnDiscover = true;
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderEquipmentProvider.future);

      expect(
        c.read(guiderEquipmentProvider.notifier).discoverAlpaca(),
        throwsStateError,
      );
      // The choices state stays intact — discovery is a transient action.
      expect(c.read(guiderEquipmentProvider).value!.connected, isTrue);
    });

    test('pushProfile forwards to the client', () async {
      final api = _FakeEquipmentClient(_resp());
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderEquipmentProvider.future);

      await c.read(guiderEquipmentProvider.notifier).pushProfile();

      expect(api.pushes, 1);
    });

    test('pushProfile surfaces the client error to the caller', () async {
      final api = _FakeEquipmentClient(_resp())..throwOnPush = true;
      final c = _container(const [server], api);
      await c.read(savedServersProvider.future);
      await c.read(guiderEquipmentProvider.future);

      expect(
        c.read(guiderEquipmentProvider.notifier).pushProfile(),
        throwsStateError,
      );
    });

    test('actions without a saved server throw StateError', () async {
      final c = _container(const [], _FakeEquipmentClient(_resp()));
      await c.read(savedServersProvider.future);
      await c.read(guiderEquipmentProvider.future);

      expect(
        c.read(guiderEquipmentProvider.notifier).discoverAlpaca(),
        throwsStateError,
      );
      expect(
        c.read(guiderEquipmentProvider.notifier).pushProfile(),
        throwsStateError,
      );
    });
  });
}
