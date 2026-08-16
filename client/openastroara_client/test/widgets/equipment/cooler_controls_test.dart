import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/equipment/cooler_controls.dart';

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
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    final enabled = body?['enabled'];
    final target = body?['target_temperature_c'];
    final fanSpeed = body?['fan_speed'];
    calls.add('command:$subpath:enabled=$enabled:target=$target:fan=$fanSpeed');
  }

  @override
  void close() {}
}

CameraStatus _status({
  bool canSetTemperature = true,
  bool? hasCooler,
  bool coolerOn = false,
  double? coolerSetpointC,
  int? fanSpeed,
  int? fanMaxSpeed,
}) =>
    CameraStatus(
      deviceId: 'cam-0',
      name: 'ATR2600M',
      connectionState: EquipmentConnectionState.connected,
      capabilities: CameraCapabilities(
        sensorWidth: 6208,
        sensorHeight: 4176,
        pixelSizeUm: 3.45,
        canSetTemperature: canSetTemperature,
        hasCooler: hasCooler ?? canSetTemperature,
        minGain: 0,
        maxGain: 400,
        minOffset: 0,
        maxOffset: 100,
        minBinX: 1,
        maxBinX: 1,
        minBinY: 1,
        maxBinY: 1,
        minExposureSec: 0.001,
        maxExposureSec: 2000,
        bayerPattern: 'RGGB',
      ),
      runtimeState: 'idle',
      ccdTemperature: 17.9,
      coolerPowerPct: 0,
      coolerOn: coolerOn,
      exposureProgressPct: null,
      coolerSetpointC: coolerSetpointC,
      fanSpeed: fanSpeed,
      fanMaxSpeed: fanMaxSpeed,
    );

Future<_FakeCameraApi> _pump(WidgetTester tester, CameraStatus status,
    {bool compact = false}) async {
  final api = _FakeCameraApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      cameraStatusApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(
          child: CoolerControls(compact: compact),
        ),
      ),
    ),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders the cooler switch, presets, custom field and fan',
      (tester) async {
    await _pump(tester, _status(coolerOn: true, coolerSetpointC: -5, fanMaxSpeed: 1));
    expect(find.text('Cooler'), findsOneWidget);
    // Presets −10 / −5 / 0 / +5.
    expect(find.text('-10 °C'), findsOneWidget);
    expect(find.text('-5 °C'), findsOneWidget);
    expect(find.text('0 °C'), findsOneWidget);
    expect(find.text('+5 °C'), findsOneWidget);
    expect(find.text('+10 °C'), findsOneWidget);
    expect(find.text('Custom (°C)'), findsOneWidget);
    expect(find.text('Cooling fan'), findsOneWidget);
  });

  testWidgets('tapping a preset turns cooling on at that target', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.text('-10 °C'));
    await tester.pumpAndSettle();
    expect(api.calls,
        contains('command:cooler:enabled=true:target=-10.0:fan=null'));
  });

  testWidgets('custom target + Set sends the entered temperature',
      (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '-7.5');
    await tester.tap(find.widgetWithText(OutlinedButton, 'Set'));
    await tester.pumpAndSettle();
    expect(api.calls,
        contains('command:cooler:enabled=true:target=-7.5:fan=null'));
  });

  testWidgets('the fan switch sends 0/max', (tester) async {
    final api = await _pump(tester, _status(fanSpeed: 1, fanMaxSpeed: 1));
    final fan = find.descendant(
      of: find.byType(CoolerControls),
      matching: find.byType(Switch),
    ).at(1);
    await tester.tap(fan);
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:fan:enabled=null:target=null:fan=0'));
  });

  testWidgets(
      'compact (Imaging tab) shows only the target picker — no toggles or '
      'readouts', (tester) async {
    await _pump(
      tester,
      _status(coolerOn: true, coolerSetpointC: -5, fanMaxSpeed: 1),
      compact: true,
    );
    expect(find.text('Cooling target'), findsOneWidget);
    expect(find.text('-10 °C'), findsOneWidget);
    expect(find.text('+5 °C'), findsOneWidget);
    expect(find.text('Custom (°C)'), findsOneWidget);
    // Only the sensor-temp readout — the other readouts and on/off toggles
    // stay in Settings → Camera.
    expect(find.text('Sensor temperature'), findsOneWidget);
    expect(find.text('17.9 °C'), findsOneWidget);
    expect(find.text('Cooler power'), findsNothing);
    expect(find.text('Cooling to'), findsNothing);
    expect(find.text('Cooling fan'), findsNothing);
    expect(find.byType(Switch), findsNothing);
    // The presets still arm cooling at that target.
    final api = await _pump(tester, _status(), compact: true);
    await tester.tap(find.text('-10 °C'));
    await tester.pumpAndSettle();
    expect(api.calls,
        contains('command:cooler:enabled=true:target=-10.0:fan=null'));
  });

  testWidgets('hides entirely when the camera has no cooler', (tester) async {
    await _pump(tester, _status(hasCooler: false));
    expect(find.text('Cooler'), findsNothing);
    expect(find.text('Cooling fan'), findsNothing);
  });
}
