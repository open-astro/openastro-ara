import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_flat_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
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

class _FakeFlatApi implements EquipmentDeviceClient<FlatPanelStatus> {
  _FakeFlatApi(this.status);
  FlatPanelStatus? status;
  final List<String> calls = [];

  @override
  Future<FlatPanelStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath');
  @override
  void close() {}
}

FlatPanelStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
}) =>
    FlatPanelStatus(
      deviceId: 'flat-0',
      name: 'FlatMaster',
      connectionState: state,
      runtimeState: 'cover_closed',
      coverOpen: false,
      lightOn: false,
      brightness: 0,
    );

// The settings panels are designed for the wide right-hand pane; give the test a
// generous surface so the shared SettingsSwitchRow doesn't overflow at 800px.
Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 900));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeFlatApi> _pump(WidgetTester tester, FlatPanelStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeFlatApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      flatPanelApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentFlatPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('offers Reconnect last while no flat panel is connected',
      (tester) async {
    await _pump(tester, null);
    expect(find.widgetWithText(TextButton, 'Reconnect last'), findsOneWidget);
  });

  testWidgets('offers Reconnect last when the flat panel is in error',
      (tester) async {
    await _pump(tester, _status(state: EquipmentConnectionState.error));
    expect(find.widgetWithText(TextButton, 'Reconnect last'), findsOneWidget);
  });

  testWidgets('hides Reconnect last while the flat panel is connected',
      (tester) async {
    // Guard from review finding #3: Reconnect must not be offered for an already
    // connected panel, so it can't interrupt a live cover/light.
    await _pump(tester, _status());
    expect(find.widgetWithText(TextButton, 'Reconnect last'), findsNothing);
  });

  testWidgets('hides Reconnect last while the flat panel is connecting',
      (tester) async {
    await _pump(tester, _status(state: EquipmentConnectionState.connecting));
    expect(find.widgetWithText(TextButton, 'Reconnect last'), findsNothing);
  });

  testWidgets('tapping Reconnect last dispatches the reconnect command',
      (tester) async {
    final api = await _pump(tester, null);
    await tester.tap(find.widgetWithText(TextButton, 'Reconnect last'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:reconnect'));
  });
}
