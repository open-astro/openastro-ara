import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
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
  bool lightOn = false,
  int brightness = 0,
  bool coverOpen = false,
  String runtimeState = 'cover_closed',
}) =>
    FlatPanelStatus(
      deviceId: 'flat-0',
      name: 'FlatMaster',
      connectionState: state,
      runtimeState: runtimeState,
      coverOpen: coverOpen,
      lightOn: lightOn,
      brightness: brightness,
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
      serverLinkUpProvider.overrideWith((ref) => true),
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
  testWidgets('no flat panel connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No flat panel connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('offers Reconnect while disconnected', (tester) async {
    // The shared card surfaces Reconnect only in the disconnected state.
    await _pump(tester, null);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
  });

  testWidgets('a disconnected (non-null) status still offers Reconnect',
      (tester) async {
    // After a session disconnect the daemon keeps the device and reports
    // state=disconnected (a non-null status, not a 404). The card must still show
    // the disconnected layout with Reconnect — not treat the lingering device as
    // connected (which would hide Reconnect behind a Disconnect button).
    await _pump(tester, _status(state: EquipmentConnectionState.disconnected));
    expect(find.text('No flat panel connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
    expect(find.byIcon(Icons.link_off), findsNothing); // not the connected layout
  });

  testWidgets('a connected panel with the light on shows its readout, no Reconnect',
      (tester) async {
    await _pump(tester,
        _status(lightOn: true, brightness: 128, runtimeState: 'light_on'));
    expect(find.text('FlatMaster'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Light on · brightness 128'), findsOneWidget);
    // Reconnect is hidden while connected (Disconnect is offered instead).
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsNothing);
  });

  testWidgets('a connected panel with the light off shows "Light off"',
      (tester) async {
    await _pump(tester, _status(lightOn: false));
    expect(find.text('Light off'), findsOneWidget);
  });

  testWidgets('a moving cover shows "Cover moving…"', (tester) async {
    await _pump(tester, _status(runtimeState: 'cover_moving'));
    expect(find.text('Cover moving…'), findsOneWidget);
  });

  testWidgets('connecting then settling to connected via the poll turns live',
      (tester) async {
    // The daemon's connect is 202 + background, so the first read shows
    // `connecting`. The generic engine polls while connecting; once the daemon
    // finishes, the card settles to Connected without the user re-opening.
    await _wideSurface(tester);
    final api =
        _FakeFlatApi(_status(state: EquipmentConnectionState.connecting));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        serverLinkUpProvider.overrideWith((ref) => true),
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        flatPanelApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(home: Scaffold(body: EquipmentFlatPanel())),
    ));
    await tester.pump(); // build
    await tester.pump(); // resolve the initial getStatus
    expect(find.text('Connecting'), findsOneWidget);

    api.status = _status(lightOn: true, brightness: 64, runtimeState: 'light_on');
    await tester.pump(const Duration(milliseconds: 1600)); // settle tick → refresh
    await tester.pump(); // resolve the refresh getStatus
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Light on · brightness 64'), findsOneWidget);
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect'));
  });

  testWidgets('Reconnect dispatches the reconnect command', (tester) async {
    final api = await _pump(tester, null);
    await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:reconnect'));
  });
}
