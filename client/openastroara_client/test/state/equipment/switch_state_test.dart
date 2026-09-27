import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/services/equipment_discovery_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/switch_api.dart';
import 'package:openastroara/state/equipment/equipment_discovery_provider.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// Pure [SwitchClient] fake — records calls, serves a scripted list that the
/// actions mutate so the post-action refresh sees the change.
class _FakeSwitchApi implements SwitchClient {
  final removed = <String>[];
  List<SwitchDevice> devices = const [];
  final List<String> calls = [];
  bool throwOnConnect = false;
  bool throwOnGetAll = false;
  // When set, connect() awaits this before completing — lets a test hold one
  // action in flight to exercise the re-entrancy drop.
  Completer<void>? connectGate;

  SwitchDevice _dev(int n, SwitchConnectionState s) => SwitchDevice(
        deviceId: 'sw-$n',
        alpacaDeviceNumber: n,
        name: 'Switch $n',
        connectionState: s,
        ports: const [],
      );

  @override
  Future<List<SwitchDevice>> getAll() async {
    if (throwOnGetAll) throw StateError('getAll failed');
    return devices;
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {
    calls.add('connect:${device.alpacaDeviceNumber}');
    if (connectGate != null) await connectGate!.future;
    if (throwOnConnect) throw StateError('connect failed');
    devices = [...devices, _dev(device.alpacaDeviceNumber, SwitchConnectionState.connected)];
  }

  @override

  Future<void> reconnect() async => calls.add("reconnect");
  /// Thrown by [reconnectDevice] when set (an older daemon answers 404).
  Object? reconnectDeviceError;

  @override
  Future<void> reconnectDevice(String deviceId) async {
    calls.add('reconnectDevice:$deviceId');
    if (reconnectDeviceError != null) throw reconnectDeviceError!;
  }


  @override
  Future<void> disconnect(String deviceId) async {
    calls.add('disconnect:$deviceId');
    devices = devices
        .map((d) => d.deviceId == deviceId
            ? _dev(d.alpacaDeviceNumber, SwitchConnectionState.disconnected)
            : d)
        .toList();
  }

  @override
  Future<void> remove(String deviceId) async => removed.add(deviceId);

  @override
  Future<void> setValue({
    required String deviceId,
    required int portId,
    required double value,
  }) async {
    calls.add('setValue:$deviceId:$portId=$value');
  }

  @override
  void close() {}
}

class _FakeDiscoveryApi implements EquipmentDiscoveryApi {
  _FakeDiscoveryApi(this.devices);
  final List<DiscoveredDevice> devices;
  int scans = 0;
  bool closed = false;

  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    scans++;
    return devices;
  }

  @override
  void close() => closed = true;
}

DioException _notFound() => DioException(
  requestOptions: RequestOptions(path: '/x'),
  response: Response(requestOptions: RequestOptions(path: '/x'), statusCode: 404),
);

DiscoveredDevice _discovered(int n) => DiscoveredDevice(
      uniqueId: 'sw-$n',
      name: 'Switch $n',
      deviceType: EquipmentDeviceType.switchDevice,
      hostName: 'h',
      ipAddress: '1.2.3.4',
      ipPort: 11111,
      alpacaDeviceNumber: n,
      useHttps: false,
    );

ProviderContainer _container(
  List<AraServer> servers,
  SwitchClient api, {
  EquipmentDiscoveryApi? discovery,
}) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    switchApiFactoryProvider.overrideWithValue((_) => api),
    if (discovery != null)
      equipmentDiscoveryApiFactoryProvider.overrideWithValue((_) => discovery),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  const server = AraServer(hostname: 'h', port: 5555);

  test('no saved server → empty list and no API built', () async {
    final c = _container(const [], _FakeSwitchApi());
    await c.read(savedServersProvider.future);
    expect(c.read(switchApiProvider), isNull);
    expect(await c.read(switchListProvider.future), isEmpty);
  });

  test('active server → exposes the daemon switch list', () async {
    final api = _FakeSwitchApi()
      ..devices = [
        SwitchDevice(
          deviceId: 'sw-0',
          alpacaDeviceNumber: 0,
          name: 'PowerBox',
          connectionState: SwitchConnectionState.connected,
          ports: const [
            SwitchPort(id: 0, name: 'Dew', value: 1, min: 0, max: 1, canWrite: true),
          ],
        ),
      ];
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    final list = await c.read(switchListProvider.future);
    expect(list, hasLength(1));
    expect(list.first.isConnected, isTrue);
    expect(list.first.ports.single.name, 'Dew');
  });

  test('connect adds a switch and the list refreshes', () async {
    final api = _FakeSwitchApi();
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future); // materialize

    await c.read(switchListProvider.notifier).connect(_discovered(0));
    await c.read(switchListProvider.notifier).connect(_discovered(1));

    expect(api.calls, containsAllInOrder(['connect:0', 'connect:1']));
    final list = c.read(switchListProvider).value!;
    expect(list.map((d) => d.alpacaDeviceNumber), [0, 1],
        reason: 'both switches present after connecting two');
  });

  test('reconnectDevice uses the per-switch route when the daemon has it', () async {
    final api = _FakeSwitchApi();
    final discovery = _FakeDiscoveryApi([_discovered(0)]);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    expect(await c.read(switchListProvider.notifier).reconnectDevice('sw-0'), isTrue);

    expect(api.calls, ['reconnectDevice:sw-0']);
    expect(discovery.scans, 0, reason: 'no discovery when the route exists');
  });

  test('reconnectDevice falls back to discovery + /connect on a 404 (older daemon)',
      () async {
    final api = _FakeSwitchApi()..reconnectDeviceError = _notFound();
    final discovery = _FakeDiscoveryApi([_discovered(3), _discovered(0)]);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    expect(await c.read(switchListProvider.notifier).reconnectDevice('sw-0'), isTrue);

    expect(api.calls, ['reconnectDevice:sw-0', 'connect:0'],
        reason: 'the discovered device with the matching unique id is connected');
    expect(discovery.scans, 1, reason: 'the cached list had it — no forced rescan');
    expect(discovery.closed, isTrue, reason: 'one-shot discovery client is closed');
  });

  test('reconnectDevice on a 404 reuses the record from an earlier connect — no broadcast',
      () async {
    // Add switch handed the notifier the discovery record; a later Connect on
    // an older daemon must not pay the ~2 s discovery broadcast again.
    final api = _FakeSwitchApi();
    final discovery = _FakeDiscoveryApi([_discovered(0)]);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);
    await c.read(switchListProvider.notifier).connect(_discovered(0));
    api.reconnectDeviceError = _notFound();

    expect(await c.read(switchListProvider.notifier).reconnectDevice('sw-0'), isTrue);

    expect(api.calls, ['connect:0', 'reconnectDevice:sw-0', 'connect:0']);
    expect(discovery.scans, 0, reason: 'the record from Add switch is reused');
  });

  test('a discovery fallback keeps the record, so the next Connect skips the broadcast',
      () async {
    final api = _FakeSwitchApi()..reconnectDeviceError = _notFound();
    final discovery = _FakeDiscoveryApi([_discovered(0)]);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    await c.read(switchListProvider.notifier).reconnectDevice('sw-0');
    await c.read(switchListProvider.notifier).reconnectDevice('sw-0');

    expect(discovery.scans, 1, reason: 'one broadcast, then the kept record');
    expect(api.calls.where((c) => c == 'connect:0').length, 2);
  });

  test('reconnectDevice rethrows the 404 when discovery has no such switch', () async {
    final api = _FakeSwitchApi()..reconnectDeviceError = _notFound();
    final discovery = _FakeDiscoveryApi([_discovered(3)]);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    await expectLater(
      c.read(switchListProvider.notifier).reconnectDevice('sw-0'),
      throwsA(isA<DioException>()),
    );
    expect(discovery.scans, 1,
        reason: 'a populated cached list without the id is the answer — no forced rescan');
    expect(api.calls, isNot(contains(startsWith('connect:'))));
  });

  test('an EMPTY cached discovery list earns one forced rescan', () async {
    final api = _FakeSwitchApi()..reconnectDeviceError = _notFound();
    final discovery = _FakeDiscoveryApi(const []);
    final c = _container(const [server], api, discovery: discovery);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    await expectLater(
      c.read(switchListProvider.notifier).reconnectDevice('sw-0'),
      throwsA(isA<DioException>()),
    );
    expect(discovery.scans, 2, reason: 'cached (empty), then one forced rescan');
  });

  test('§25.3 switchActingProvider is true exactly while an action is in flight',
      () async {
    final api = _FakeSwitchApi()..connectGate = Completer<void>();
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);
    expect(c.read(switchActingProvider), isFalse, reason: 'idle before any action');

    final action = c.read(switchListProvider.notifier).connect(_discovered(0));
    expect(c.read(switchActingProvider), isTrue,
        reason: 'amber signal raised while the action holds the gate');

    api.connectGate!.complete();
    await action;
    expect(c.read(switchActingProvider), isFalse,
        reason: 'cleared when the action unwinds');
  });

  test('§25.3 the acting signal resets on a mid-action server change (r1 fix)',
      () async {
    // The abandoned action's finally deliberately SKIPS the clear when the
    // generation has moved on (it must not stomp a newer action's signal), so
    // without the notifier's own server-change reset the chip would stick
    // amber forever. Mutable server list → we can change the active server.
    final servers = [server];
    final api = _FakeSwitchApi()..connectGate = Completer<void>();
    final c = _container(servers, api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    final action = c.read(switchListProvider.notifier).connect(_discovered(0));
    expect(c.read(switchActingProvider), isTrue, reason: 'action in flight');

    // Active server changes while the action is still holding the gate.
    servers.add(const AraServer(hostname: 'h2', port: 5556));
    c.invalidate(savedServersProvider);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);
    expect(c.read(switchActingProvider), isFalse,
        reason: 'server change must reset the signal — nothing else ever will');

    // The abandoned action unwinding later must not re-raise the signal.
    api.connectGate!.complete();
    await action;
    expect(c.read(switchActingProvider), isFalse,
        reason: 'the stale generation must not clear-or-set anything');
  });

  test('an action while another is in flight is dropped (returns false)', () async {
    final api = _FakeSwitchApi()..connectGate = Completer<void>();
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    final first = c.read(switchListProvider.notifier).connect(_discovered(0)); // holds the gate
    final dropped = await c.read(switchListProvider.notifier).connect(_discovered(1));
    expect(dropped, isFalse, reason: 'second action dropped while the first is in flight');

    api.connectGate!.complete();
    expect(await first, isTrue, reason: 'the first action ran');
    expect(api.calls, isNot(contains('connect:1')), reason: 'the dropped call never hit the API');
  });

  test('a manual refresh racing an in-flight action is safe', () async {
    final api = _FakeSwitchApi()..connectGate = Completer<void>();
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    final action = c.read(switchListProvider.notifier).connect(_discovered(0)); // gated, in flight
    await c.read(switchListProvider.notifier).refresh(); // racing read — must not throw/corrupt
    api.connectGate!.complete();
    await action;

    final list = c.read(switchListProvider).value!;
    expect(list.map((d) => d.alpacaDeviceNumber), [0],
        reason: 'the action result lands; the racing refresh neither lost nor duplicated it');
  });

  test('a post-action list read failure surfaces as the provider error', () async {
    final api = _FakeSwitchApi();
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future); // initial read ok (empty)

    api.throwOnGetAll = true; // the post-connect refresh read fails
    await c.read(switchListProvider.notifier).connect(_discovered(0));
    expect(c.read(switchListProvider).hasError, isTrue,
        reason: "a list we can't re-read becomes the provider's error");
  });

  test('disconnect targets one switch; setValue forwards the write', () async {
    final api = _FakeSwitchApi()
      ..devices = [
        SwitchDevice(
            deviceId: 'sw-0',
            alpacaDeviceNumber: 0,
            name: 'A',
            connectionState: SwitchConnectionState.connected,
            ports: const []),
        SwitchDevice(
            deviceId: 'sw-1',
            alpacaDeviceNumber: 1,
            name: 'B',
            connectionState: SwitchConnectionState.connected,
            ports: const []),
      ];
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    await c.read(switchListProvider.notifier).disconnect('sw-0');
    await c.read(switchListProvider.notifier)
        .setValue(deviceId: 'sw-1', portId: 3, value: 42.0);

    expect(api.calls, contains('disconnect:sw-0'));
    expect(api.calls, contains('setValue:sw-1:3=42.0'));
    final list = c.read(switchListProvider).value!;
    expect(list.firstWhere((d) => d.alpacaDeviceNumber == 0).connectionState,
        SwitchConnectionState.disconnected);
    expect(list.firstWhere((d) => d.alpacaDeviceNumber == 1).isConnected, isTrue);
  });

  test('a failed action throws to the caller and keeps the list intact', () async {
    final api = _FakeSwitchApi()
      ..devices = [
        SwitchDevice(
            deviceId: 'sw-0',
            alpacaDeviceNumber: 0,
            name: 'A',
            connectionState: SwitchConnectionState.connected,
            ports: const []),
      ]
      ..throwOnConnect = true;
    final c = _container(const [server], api);
    await c.read(savedServersProvider.future);
    await c.read(switchListProvider.future);

    // The error propagates to the caller (the UI surfaces it per-control)...
    await expectLater(
      c.read(switchListProvider.notifier).connect(_discovered(1)),
      throwsA(isA<StateError>()),
    );
    // ...and the loaded list is NOT wiped — a one-off failure on one device
    // doesn't blow away the view of every other switch.
    final state = c.read(switchListProvider);
    expect(state.hasError, isFalse);
    expect(state.value!.single.alpacaDeviceNumber, 0);
  });
}
