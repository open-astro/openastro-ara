import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/services/switch_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/widgets/equipment/fan_switch_row.dart';

class _FakeSwitchClient implements SwitchClient {
  final List<SwitchDevice> devices;
  _FakeSwitchClient(this.devices, {this.throwOnSet = false});
  final List<String> calls = [];
  final bool throwOnSet;
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
    if (throwOnSet) throw Exception('device rejected the write');
    calls.add('setValue:$deviceId:$portId:$value');
  }

  @override
  void close() {}
}

SwitchDevice _fanDevice({double value = 1.0}) => SwitchDevice(
      deviceId: 'switch-5',
      alpacaDeviceNumber: 5,
      name: 'ToupTek Thermal Switch',
      connectionState: SwitchConnectionState.connected,
      ports: [
        SwitchPort(
          id: 0,
          name: 'DewHeater',
          value: 0,
          min: 0,
          max: 1,
          canWrite: true,
        ),
        SwitchPort(
          id: 1,
          name: 'Fan',
          value: value,
          min: 0,
          max: 1,
          canWrite: true,
        ),
      ],
    );

Future<_FakeSwitchClient> _pump(
  WidgetTester tester, {
  List<SwitchDevice>? switches,
  CameraStatus? camera,
  _FakeSwitchClient? switchClient,
  bool cameraFails = false,
}) async {
  final fake = switchClient ?? _FakeSwitchClient(switches ?? const []);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      switchApiProvider.overrideWithValue(fake),
      switchListProvider.overrideWith(
          () => _SwitchListNotifierForTest(fake)),
      cameraStatusProvider.overrideWith(() => cameraFails
          ? _ErrorCameraNotifier()
          : _FixedCameraNotifier(camera)),
    ],
    child: const MaterialApp(
      home: Scaffold(body: SingleChildScrollView(child: FanSwitchRow())),
    ),
  ));
  await tester.pumpAndSettle();
  return fake;
}

void main() {
  testWidgets('hidden when no connected switch has a Fan port', (tester) async {
    await _pump(tester, switches: const []);
    expect(find.byType(Switch), findsNothing);
    expect(find.text('Cooling fan'), findsNothing);
  });

  testWidgets(
      'ignores a Fan port on a non-Thermal-Switch device (same scoping as '
      'the cooler auto-sync)', (tester) async {
    final relayBox = SwitchDevice(
      deviceId: 'switch-9',
      alpacaDeviceNumber: 9,
      name: 'Generic Relay Box',
      connectionState: SwitchConnectionState.connected,
      ports: [
        SwitchPort(id: 0, name: 'Fan', value: 1, min: 0, max: 1, canWrite: true),
      ],
    );
    await _pump(tester, switches: [relayBox]);
    expect(find.byType(Switch), findsNothing);
    expect(find.text('Cooling fan'), findsNothing);
  });

  testWidgets('shows the fan switch from the thermal-switch Fan port',
      (tester) async {
    await _pump(tester, switches: [_fanDevice(value: 1.0)]);
    expect(find.text('Cooling fan'), findsOneWidget);
    final sw = tester.widget<Switch>(find.byType(Switch));
    expect(sw.value, isTrue);
  });

  testWidgets('turning the fan off sends setValue 0', (tester) async {
    final fake = await _pump(tester, switches: [_fanDevice(value: 1.0)]);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(fake.calls, contains('setValue:switch-5:1:0.0'));
  });

  testWidgets('a failed fan write surfaces a snackbar (no silent swallow)',
      (tester) async {
    final sw = _FakeSwitchClient([_fanDevice(value: 1.0)], throwOnSet: true);
    await _pump(tester, switches: sw.devices, switchClient: sw);
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(find.textContaining("Couldn't set the fan"), findsOneWidget);
  });

  testWidgets(
      'refuses fan-off when the cooler state is unknown (camera read failed) '
      '— the interlock fails closed', (tester) async {
    final fake = await _pump(
      tester,
      switches: [_fanDevice(value: 1.0)],
      cameraFails: true,
    );
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(find.textContaining('cooler state is unknown'), findsOneWidget);
    expect(fake.calls, isEmpty); // no write went through
  });

  testWidgets('refuses fan-off while the cooler is cooling', (tester) async {
    await _pump(
      tester,
      switches: [_fanDevice(value: 1.0)],
      camera: FakeCameraStatus(coolerOn: true),
    );
    await tester.tap(find.byType(Switch));
    await tester.pumpAndSettle();
    expect(find.textContaining('damage the camera'), findsOneWidget);
  });
}

// Minimal camera fake — a connected camera with the given cooler state.
class FakeCameraStatus extends CameraStatus {
  FakeCameraStatus({required super.coolerOn})
      : super(
          deviceId: 'cam-2',
          name: 'ATR2600M',
          connectionState: EquipmentConnectionState.connected,
          capabilities: null,
          runtimeState: 'idle',
          ccdTemperature: 17.9,
          coolerPowerPct: 0,
          exposureProgressPct: null,
        );
}

class _FixedCameraNotifier extends CameraStatusNotifier {
  _FixedCameraNotifier(this._status);
  final CameraStatus? _status;
  @override
  Future<CameraStatus?> build() async => _status;
}

/// Camera status stuck in AsyncError — the "cooler state unknown" case the
/// fan-off interlock must fail closed on.
class _ErrorCameraNotifier extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async =>
      throw Exception('camera read failed');
}

class _SwitchListNotifierForTest extends SwitchListNotifier {
  _SwitchListNotifierForTest(this.client);
  final _FakeSwitchClient client;
  @override
  Future<List<SwitchDevice>> build() async => client.devices;
}
