import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/screens/settings/panels/equipment_switch_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/switch_api.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

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

class _FakeSwitchApi implements SwitchClient {
  _FakeSwitchApi(this.devices);
  List<SwitchDevice> devices;
  final List<String> calls = [];

  @override
  Future<List<SwitchDevice>> getAll() async => devices;
  @override
  Future<void> connect(DiscoveredDevice device) async => calls.add('connect:${device.alpacaDeviceNumber}');
  @override
  Future<void> disconnect(int deviceNumber) async => calls.add('disconnect:$deviceNumber');
  @override
  Future<void> setValue({required int deviceNumber, required int portId, required double value}) async =>
      calls.add('setValue:$deviceNumber:$portId=$value');
  @override
  void close() {}
}

SwitchDevice _device(List<SwitchPort> ports) => SwitchDevice(
      deviceId: 'sw-0',
      alpacaDeviceNumber: 0,
      name: 'PowerBox',
      connectionState: SwitchConnectionState.connected,
      ports: ports,
    );

Future<_FakeSwitchApi> _pump(WidgetTester tester, List<SwitchDevice> devices) async {
  final api = _FakeSwitchApi(devices);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      switchApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentSwitchPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('empty list shows the empty state + Add switch', (tester) async {
    await _pump(tester, const []);
    expect(find.text('No switches connected'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Add switch'), findsOneWidget);
  });

  testWidgets('renders a connected switch with its ports', (tester) async {
    await _pump(tester, [
      _device(const [
        SwitchPort(id: 0, name: 'Dew A', value: 1, min: 0, max: 1, canWrite: true),
        SwitchPort(id: 1, name: 'PWM', value: 40, min: 0, max: 100, canWrite: true),
        SwitchPort(id: 2, name: 'Volts', value: 12, min: 0, max: 30, canWrite: false),
      ]),
    ]);
    expect(find.text('PowerBox'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Dew A'), findsOneWidget); // boolean → toggle
    expect(find.byType(Switch), findsOneWidget);
    expect(find.text('PWM'), findsOneWidget); // value → slider
    expect(find.byType(Slider), findsOneWidget);
    expect(find.text('Volts'), findsOneWidget); // read-only → text value
  });

  testWidgets('toggling a boolean port writes its value', (tester) async {
    final api = await _pump(tester, [
      _device(const [SwitchPort(id: 0, name: 'Dew A', value: 0, min: 0, max: 1, canWrite: true)]),
    ]);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(api.calls, contains('setValue:0:0=1.0'));
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, [_device(const [])]);
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect:0'));
  });
}
