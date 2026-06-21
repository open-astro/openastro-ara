import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/switch_api.dart';
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
  List<SwitchDevice> devices = const [];
  final List<String> calls = [];
  bool throwOnConnect = false;
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
  Future<List<SwitchDevice>> getAll() async => devices;

  @override
  Future<void> connect(DiscoveredDevice device) async {
    calls.add('connect:${device.alpacaDeviceNumber}');
    if (connectGate != null) await connectGate!.future;
    if (throwOnConnect) throw StateError('connect failed');
    devices = [...devices, _dev(device.alpacaDeviceNumber, SwitchConnectionState.connected)];
  }

  @override
  Future<void> disconnect(int deviceNumber) async {
    calls.add('disconnect:$deviceNumber');
    devices = devices
        .map((d) => d.alpacaDeviceNumber == deviceNumber
            ? _dev(deviceNumber, SwitchConnectionState.disconnected)
            : d)
        .toList();
  }

  @override
  Future<void> setValue({
    required int deviceNumber,
    required int portId,
    required double value,
  }) async {
    calls.add('setValue:$deviceNumber:$portId=$value');
  }

  @override
  void close() {}
}

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

ProviderContainer _container(List<AraServer> servers, SwitchClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    switchApiFactoryProvider.overrideWithValue((_) => api),
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

    await c.read(switchListProvider.notifier).disconnect(0);
    await c.read(switchListProvider.notifier)
        .setValue(deviceNumber: 1, portId: 3, value: 42.0);

    expect(api.calls, contains('disconnect:0'));
    expect(api.calls, contains('setValue:1:3=42.0'));
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
