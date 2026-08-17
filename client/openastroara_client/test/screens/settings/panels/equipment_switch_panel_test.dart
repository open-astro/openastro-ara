import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
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
  final removed = <String>[];
  _FakeSwitchApi(this.devices);
  List<SwitchDevice> devices;
  final List<String> calls = [];

  @override
  Future<List<SwitchDevice>> getAll() async => devices;
  @override
  Future<void> connect(DiscoveredDevice device) async => calls.add('connect:${device.alpacaDeviceNumber}');
  @override
  Future<void> reconnect() async => calls.add("reconnect");

  @override
  Future<void> disconnect(String deviceId) async => calls.add('disconnect:$deviceId');

  @override
  Future<void> remove(String deviceId) async => removed.add(deviceId);
  @override
  Future<void> setValue({required String deviceId, required int portId, required double value}) async =>
      calls.add('setValue:$deviceId:$portId=$value');
  @override
  void close() {}
}

SwitchDevice _device(List<SwitchPort> ports,
        {SwitchConnectionState state = SwitchConnectionState.connected}) =>
    SwitchDevice(
      deviceId: 'sw-0',
      alpacaDeviceNumber: 0,
      name: 'PowerBox',
      connectionState: state,
      ports: ports,
    );

/// Minimal connected camera with the given cooler state (for the fan-off
/// interlock tests).
CameraStatus _camera({required bool coolerOn}) => CameraStatus(
      deviceId: 'cam-2',
      name: 'ATR2600M',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      runtimeState: 'idle',
      ccdTemperature: 17.9,
      coolerPowerPct: 0,
      coolerOn: coolerOn,
      exposureProgressPct: null,
    );

class _FixedCameraNotifier extends CameraStatusNotifier {
  _FixedCameraNotifier(this._status);
  final CameraStatus? _status;
  @override
  Future<CameraStatus?> build() async => _status;
}

Future<_FakeSwitchApi> _pump(WidgetTester tester, List<SwitchDevice> devices,
    {CameraStatus? camera}) async {
  final api = _FakeSwitchApi(devices);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      switchApiFactoryProvider.overrideWithValue((_) => api),
      cameraStatusProvider.overrideWith(() => _FixedCameraNotifier(camera)),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentSwitchPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('a connecting switch settles to connected via the poll', (tester) async {
    // The daemon's connect is 202 + background, so the first read shows `connecting`.
    // The panel polls while anything is connecting; once the daemon finishes, the
    // card must settle to Connected without the user re-opening the panel.
    final api = _FakeSwitchApi([_device(const [], state: SwitchConnectionState.connecting)]);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        serverLinkUpProvider.overrideWith((ref) => true),
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        switchApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(home: Scaffold(body: EquipmentSwitchPanel())),
    ));
    await tester.pump(); // build
    await tester.pump(); // resolve the initial getAll
    expect(find.text('Connecting'), findsOneWidget);

    // Daemon finished connecting in the background.
    api.devices = [_device(const [], state: SwitchConnectionState.connected)];
    await tester.pump(const Duration(milliseconds: 1600)); // fire the settle-poll tick → refresh
    await tester.pump(); // resolve the refresh getAll
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Connecting'), findsNothing);
  });

  testWidgets('empty list shows the empty state + Add switch', (tester) async {
    await _pump(tester, const []);
    expect(find.text('No switches connected'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Add switch'), findsOneWidget);
  });

  testWidgets('offers Reconnect when no switch is connected (post power-cycle)',
      (tester) async {
    await _pump(tester, const []);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
  });

  testWidgets('hides Reconnect while a switch is connected', (tester) async {
    // Guard from review: reconnectAll re-dispatches every remembered switch, so
    // it must not be offered while one is live (re-connecting a switch whose
    // remembered host differs can tear down the live connection).
    await _pump(tester, [_device(const [])]);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsNothing);
  });

  testWidgets('hides Reconnect while a switch is connecting', (tester) async {
    await _pump(tester,
        [_device(const [], state: SwitchConnectionState.connecting)]);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsNothing);
  });

  testWidgets('offers Reconnect when a remembered switch is in error',
      (tester) async {
    await _pump(
        tester, [_device(const [], state: SwitchConnectionState.error)]);
    expect(find.widgetWithText(TextButton, 'Reconnect'), findsOneWidget);
  });

  testWidgets('tapping Reconnect dispatches reconnect', (tester) async {
    final api = await _pump(tester, const []);
    await tester.tap(find.widgetWithText(TextButton, 'Reconnect'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('reconnect'));
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
    // Scope to the switch Card so the panel's "Auto-connect on boot" toggle
    // (a separate Switch at the top of the panel) isn't matched.
    expect(find.descendant(of: find.byType(Card), matching: find.byType(Switch)),
        findsOneWidget);
    expect(find.text('PWM'), findsOneWidget); // value → slider
    expect(find.byType(Slider), findsOneWidget);
    expect(find.text('Volts'), findsOneWidget); // read-only → text value
  });

  testWidgets('toggling a boolean port writes its value', (tester) async {
    final api = await _pump(tester, [
      _device(const [SwitchPort(id: 0, name: 'Dew A', value: 0, min: 0, max: 1, canWrite: true)]),
    ]);
    await tester.tap(find.descendant(
        of: find.byType(Card), matching: find.byType(Switch)));
    await tester.pumpAndSettle();
    expect(api.calls, contains('setValue:sw-0:0=1.0'));
  });

  // §25.5.6 — the fan-off interlock must also cover this generic panel, not
  // just the FanSwitchRow in Settings → Camera (both reach the same port).
  SwitchDevice thermalSwitch({double fanValue = 1.0}) => SwitchDevice(
        deviceId: 'sw-1',
        alpacaDeviceNumber: 1,
        name: 'ToupTek Thermal Switch',
        connectionState: SwitchConnectionState.connected,
        ports: [
          SwitchPort(
              id: 0, name: 'Fan', value: fanValue, min: 0, max: 1, canWrite: true),
        ],
      );

  testWidgets('refuses a Thermal-Switch fan-off while the cooler is cooling',
      (tester) async {
    final api = await _pump(tester, [thermalSwitch()],
        camera: _camera(coolerOn: true));
    await tester.tap(find.descendant(
        of: find.byType(Card), matching: find.byType(Switch)));
    await tester.pumpAndSettle();
    expect(find.textContaining('damage the camera'), findsOneWidget);
    expect(api.calls.where((c) => c.startsWith('setValue')), isEmpty);
  });

  testWidgets(
      'allows a Thermal-Switch fan-off with no camera connected '
      '(resolved-null status = no TEC this client started)', (tester) async {
    final api = await _pump(tester, [thermalSwitch()]);
    await tester.tap(find.descendant(
        of: find.byType(Card), matching: find.byType(Switch)));
    await tester.pumpAndSettle();
    expect(api.calls, contains('setValue:sw-1:0=0.0'));
  });

  testWidgets(
      'refuses dragging a PWM Fan slider to its true off (min != 0) while '
      'cooling — the interlock is range-aware, not a fixed 0.5 threshold',
      (tester) async {
    final pwmThermal = SwitchDevice(
      deviceId: 'sw-1',
      alpacaDeviceNumber: 1,
      name: 'ToupTek Thermal Switch',
      connectionState: SwitchConnectionState.connected,
      ports: [
        SwitchPort(
            id: 0, name: 'Fan', value: 50, min: 10, max: 100, canWrite: true),
      ],
    );
    final api =
        await _pump(tester, [pwmThermal], camera: _camera(coolerOn: true));
    await tester.drag(find.byType(Slider), const Offset(-600, 0));
    await tester.pumpAndSettle();
    expect(find.textContaining('damage the camera'), findsOneWidget);
    expect(api.calls.where((c) => c.startsWith('setValue')), isEmpty);
  });

  testWidgets('allows a Thermal-Switch fan-off once the cooler is off',
      (tester) async {
    final api = await _pump(tester, [thermalSwitch()],
        camera: _camera(coolerOn: false));
    await tester.tap(find.descendant(
        of: find.byType(Card), matching: find.byType(Switch)));
    await tester.pumpAndSettle();
    expect(api.calls, contains('setValue:sw-1:0=0.0'));
  });

  testWidgets('a writable port with degenerate bounds (min==max) shows no slider', (tester) async {
    await _pump(tester, [
      _device(const [SwitchPort(id: 0, name: 'Bad', value: 5, min: 5, max: 5, canWrite: true)]),
    ]);
    // Must not crash on the Slider's min < max assert — falls back to read-only.
    expect(find.byType(Slider), findsNothing);
    expect(find.text('Bad'), findsOneWidget);
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, [_device(const [])]);
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect:sw-0'));
  });

  testWidgets('a disconnected switch shows Remove; confirm calls the API',
      (tester) async {
    final client = await _pump(tester, [
      const SwitchDevice(
        deviceId: 'dead-1',
        alpacaDeviceNumber: 1,
        name: 'ZWO Dew Heater',
        connectionState: SwitchConnectionState.disconnected,
        ports: [],
      ),
    ]);
    expect(find.byTooltip('Remove this switch'), findsOneWidget);
    await tester.tap(find.byTooltip('Remove this switch'));
    await tester.pumpAndSettle();
    expect(find.text('Remove ZWO Dew Heater?'), findsOneWidget);
    await tester.tap(find.text('Remove'));
    await tester.pumpAndSettle();
    expect(client.removed, contains('dead-1'));
  });

  testWidgets('a connected switch offers no Remove (disconnect first)',
      (tester) async {
    await _pump(tester, [
      const SwitchDevice(
        deviceId: 'live-1',
        alpacaDeviceNumber: 0,
        name: 'Hub',
        connectionState: SwitchConnectionState.connected,
        ports: [],
      ),
    ]);
    expect(find.byTooltip('Remove this switch'), findsNothing);
  });
}
