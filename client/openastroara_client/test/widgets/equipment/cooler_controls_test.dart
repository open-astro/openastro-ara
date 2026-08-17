import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/services/switch_api.dart';
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

class _FakeSwitchClient implements SwitchClient {
  _FakeSwitchClient(this.devices);
  List<SwitchDevice> devices;
  final List<String> calls = [];
  @override
  Future<List<SwitchDevice>> getAll() async => devices;
  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect(String deviceId) async {}
  @override
  Future<void> remove(String deviceId) async {}
  @override
  Future<void> reconnect() async {}
  @override
  Future<void> setValue({
    required String deviceId,
    required int portId,
    required double value,
  }) async {
    calls.add('setValue:$deviceId:$portId:$value');
  }

  @override
  void close() {}
}

class _FixedSwitchListNotifier extends SwitchListNotifier {
  _FixedSwitchListNotifier(this._client);
  final _FakeSwitchClient _client;
  @override
  Future<List<SwitchDevice>> build() async => _client.devices;
}

SwitchDevice _fanDevice({double value = 0.0}) => SwitchDevice(
      deviceId: 'switch-5',
      alpacaDeviceNumber: 5,
      name: 'ToupTek Thermal Switch',
      connectionState: SwitchConnectionState.connected,
      ports: [
        SwitchPort(
            id: 1, name: 'Fan', value: value, min: 0, max: 1, canWrite: true),
      ],
    );

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
    );

Future<_FakeCameraApi> _pump(WidgetTester tester, CameraStatus status,
    {bool compact = false, _FakeSwitchClient? switchClient}) async {
  final api = _FakeCameraApi(status);
  final sw = switchClient ?? _FakeSwitchClient(const []);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      cameraStatusApiFactoryProvider.overrideWithValue((_) => api),
      switchApiProvider.overrideWithValue(sw),
      switchListProvider.overrideWith(() => _FixedSwitchListNotifier(sw)),
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
  testWidgets('renders the cooler switch, presets and custom field',
      (tester) async {
    await _pump(tester, _status(coolerOn: true, coolerSetpointC: -5));
    expect(find.text('Cooler'), findsOneWidget);
    // Presets −10 / −5 / 0 / +5.
    expect(find.text('-10'), findsOneWidget);
    expect(find.text('-5'), findsOneWidget);
    expect(find.text('0'), findsOneWidget);
    expect(find.text('+5'), findsOneWidget);
    expect(find.text('+10'), findsOneWidget);
    expect(find.text('Target temperature (°C)'), findsOneWidget);
    expect(find.text('Custom (°C)'), findsOneWidget);
  });

  testWidgets('tapping a preset turns cooling on at that target', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.text('-10'));
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

  testWidgets(
      'compact (Imaging tab) shows only the target picker — no toggles or '
      'readouts', (tester) async {
    await _pump(
      tester,
      _status(coolerOn: true, coolerSetpointC: -5),
      compact: true,
    );
    expect(find.text('Cooling target'), findsOneWidget);
    expect(find.text('-10'), findsOneWidget);
    expect(find.text('+5'), findsOneWidget);
    expect(find.text('Custom (°C)'), findsOneWidget);
    expect(find.text('Target temperature (°C)'), findsOneWidget);
    // Only the sensor-temp readout — the other readouts and on/off toggles
    // stay in Settings → Camera.
    expect(find.text('Sensor temperature'), findsOneWidget);
    expect(find.text('17.9 °C'), findsOneWidget);
    expect(find.text('Cooler power'), findsNothing);
    expect(find.text('Cooling to'), findsNothing);
    expect(find.byType(Switch), findsNothing);
    // The presets still arm cooling at that target.
    final api = await _pump(tester, _status(), compact: true);
    await tester.tap(find.text('-10'));
    await tester.pumpAndSettle();
    expect(api.calls,
        contains('command:cooler:enabled=true:target=-10.0:fan=null'));
  });

  testWidgets('turning the cooler on syncs the fan switch port (single write)',
      (tester) async {
    final sw = _FakeSwitchClient([_fanDevice(value: 0.0)]);
    await _pump(tester, _status(coolerOn: false), switchClient: sw);
    await tester.tap(find.byType(Switch).first); // cooler switch
    await tester.pumpAndSettle();
    expect(sw.calls, contains('setValue:switch-5:1:1.0'));
  });

  testWidgets('turning the cooler off syncs the fan switch port off',
      (tester) async {
    final sw = _FakeSwitchClient([_fanDevice(value: 1.0)]);
    await _pump(tester, _status(coolerOn: true), switchClient: sw);
    await tester.tap(find.byType(Switch).first); // cooler switch
    await tester.pumpAndSettle();
    expect(sw.calls, contains('setValue:switch-5:1:0.0'));
  });

  testWidgets('hides entirely when the camera has no cooler', (tester) async {
    await _pump(tester, _status(hasCooler: false));
    expect(find.text('Cooler'), findsNothing);
    expect(find.text('Cooling fan'), findsNothing);
  });
}
