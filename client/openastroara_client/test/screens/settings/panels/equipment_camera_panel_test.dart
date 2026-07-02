import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_camera_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
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

class _FakeCameraApi implements EquipmentDeviceClient<CameraStatus> {
  _FakeCameraApi(this.status);
  CameraStatus? status;
  final List<String> calls = [];
  @override
  Future<CameraStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async => calls.add(
      'command:$subpath:enabled=${body?['enabled']}:target=${body?['target_temperature_c']}');
  @override
  void close() {}
}

CameraStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canSetTemperature = true,
  // Defaults to canSetTemperature (a TEC camera has a cooler), so pre-§25.5.5
  // fixtures read unchanged; pass explicitly for the dumb-cooler shape.
  bool? hasCooler,
  bool coolerOn = false,
  String runtimeState = 'idle',
}) =>
    CameraStatus(
      deviceId: 'cam-0',
      name: 'ASI2600',
      connectionState: state,
      capabilities: CameraCapabilities(
        sensorWidth: 6248,
        sensorHeight: 4176,
        pixelSizeUm: 3.76,
        canSetTemperature: canSetTemperature,
        hasCooler: hasCooler ?? canSetTemperature,
        minGain: 0,
        maxGain: 500,
        minOffset: 0,
        maxOffset: 80,
        minBinX: 1,
        maxBinX: 4,
        minBinY: 1,
        maxBinY: 4,
        minExposureSec: 0.0001,
        maxExposureSec: 3600,
        bayerPattern: 'RGGB',
      ),
      runtimeState: runtimeState,
      ccdTemperature: -9.8,
      coolerPowerPct: 42,
      coolerOn: coolerOn,
      exposureProgressPct: null,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeCameraApi> _pump(WidgetTester tester, CameraStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeCameraApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      cameraStatusApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentCameraPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live temp/cooler + sensor caps', (tester) async {
    await _pump(tester, _status());
    expect(find.text('ASI2600'), findsOneWidget);
    expect(find.text('-9.8 °C'), findsOneWidget);
    expect(find.text('42 %'), findsOneWidget);
    expect(find.text('6248 × 4176'), findsOneWidget);
    expect(find.text('Colour (RGGB)'), findsOneWidget);
    expect(find.text('0–500'), findsOneWidget); // gain range
    expect(find.byType(Switch), findsNWidgets(2)); // cooler + auto-connect
  });

  testWidgets('Set target sends the cooler command (enabled + target)',
      (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '-15');
    await tester.tap(find.widgetWithText(OutlinedButton, 'Set target'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:cooler:enabled=true:target=-15.0'));
  });

  testWidgets('toggling the cooler Switch sends enabled only (no set-point)',
      (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byType(Switch).first); // cooler switch (not auto-connect)
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:cooler:enabled=true:target=null'));
  });

  testWidgets('no cooler control when the camera cannot set temperature',
      (tester) async {
    await _pump(tester, _status(canSetTemperature: false));
    expect(find.widgetWithText(OutlinedButton, 'Set target'), findsNothing);
    // Only the auto-connect switch remains.
    expect(find.byType(Switch), findsOneWidget);
  });

  testWidgets(
      '§25.5.5 dumb cooler: on/off Switch without a set-point field',
      (tester) async {
    // CoolerOn implemented but no TEC regulation: the Switch must appear
    // (auto-connect + cooler = two switches) with no Target/Set target UI.
    final api =
        await _pump(tester, _status(canSetTemperature: false, hasCooler: true));
    expect(find.byType(Switch), findsNWidgets(2));
    expect(find.widgetWithText(OutlinedButton, 'Set target'), findsNothing);
    expect(find.text('Target (°C)'), findsNothing);
    // And the switch actually drives the cooler (never a set-point).
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:cooler:enabled=true:target=null'));
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No camera connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
