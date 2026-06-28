import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_focuser_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
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

class _FakeFocuserApi implements EquipmentDeviceClient<FocuserStatus> {
  _FakeFocuserApi(this.status);
  FocuserStatus? status;
  final List<String> calls = [];
  @override
  Future<FocuserStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath:${body?['target_position']}');
  @override
  void close() {}
}

FocuserStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  int? position = 1000,
  String runtimeState = 'idle',
}) =>
    FocuserStatus(
      deviceId: 'foc-0',
      name: 'MoonLite',
      connectionState: state,
      capabilities: const FocuserCapabilities(
        minPosition: 0,
        maxPosition: 50000,
        stepSizeUm: 1.5,
        canTempComp: true,
        absoluteFocuser: true,
      ),
      runtimeState: runtimeState,
      position: position,
      temperature: 3.2,
      tempCompEnabled: false,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1000));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeFocuserApi> _pump(WidgetTester tester, FocuserStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeFocuserApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      focuserApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentFocuserPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live position + temperature + move control', (tester) async {
    await _pump(tester, _status(position: 1234));
    expect(find.text('MoonLite'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    // Shown twice: the live Position row + the Target field seeded from it.
    expect(find.text('1234'), findsNWidgets(2));
    expect(find.text('Position'), findsOneWidget);
    expect(find.text('3.2 °C'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Move'), findsOneWidget);
  });

  testWidgets('Move sends the target position command', (tester) async {
    final api = await _pump(tester, _status(position: 1000));
    await tester.enterText(find.byType(TextField), '2500');
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:move:2500'));
  });

  testWidgets('Move clamps a past-max target to the device range', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '99999'); // > max 50000
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:move:50000'));
  });

  testWidgets('a moving focuser shows Moving… and disables Move', (tester) async {
    await _pump(tester, _status(runtimeState: 'moving'));
    expect(find.text('Moving…'), findsOneWidget);
    final btn = tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Move'));
    expect(btn.onPressed, isNull);
  });

  testWidgets('a relative focuser allows a negative step and does not clamp',
      (tester) async {
    final api = await _pump(
        tester,
        FocuserStatus(
          deviceId: 'foc-1',
          name: 'Relative',
          connectionState: EquipmentConnectionState.connected,
          capabilities: const FocuserCapabilities(
            minPosition: 0,
            maxPosition: 0, // relative focusers report no absolute range
            stepSizeUm: 1.0,
            canTempComp: false,
            absoluteFocuser: false,
          ),
          runtimeState: 'idle',
          position: 0,
          temperature: null,
          tempCompEnabled: false,
        ));
    expect(find.text('Steps (±)'), findsOneWidget);
    await tester.enterText(find.byType(TextField), '-300'); // inward, negative
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    // Sent as-is (no clamp to the 0..0 absolute range).
    expect(api.calls, contains('command:move:-300'));
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No focuser connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
