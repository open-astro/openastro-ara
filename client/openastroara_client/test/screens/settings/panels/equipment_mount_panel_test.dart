import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_mount_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
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

class _FakeMountApi implements EquipmentDeviceClient<MountStatus> {
  _FakeMountApi(this.status);
  MountStatus? status;
  final List<String> calls = [];
  @override
  Future<MountStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      // moveaxis carries axis/rate; everything else carries (or omits) `enabled`.
      calls.add(body != null && body.containsKey('rate')
          ? 'command:$subpath:axis=${body['axis']}:rate=${body['rate']}'
          : 'command:$subpath:enabled=${body?['enabled']}');
  @override
  void close() {}
}

MountStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canSetTracking = true,
  bool canFindHome = false,
  bool canMoveAxis = false,
  List<double> axisRates = const [],
  bool tracking = false,
  bool parked = false,
  String runtimeState = 'idle',
}) =>
    MountStatus(
      deviceId: 'mount-0',
      name: 'EQ6-R',
      connectionState: state,
      capabilities: MountCapabilities(
        canSlew: true,
        canSync: true,
        canPark: true,
        canUnpark: true,
        canSetTracking: canSetTracking,
        canFindHome: canFindHome,
        canMoveAxis: canMoveAxis,
        axisRatesDegPerSec: axisRates,
      ),
      runtimeState: runtimeState,
      rightAscensionHours: 5.5,
      declinationDegrees: -12.25,
      tracking: tracking,
      parked: parked,
      atHome: false,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1400));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeMountApi> _pump(WidgetTester tester, MountStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeMountApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      mountApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentMountPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live RA/Dec + tracking switch + Park', (tester) async {
    await _pump(tester, _status());
    expect(find.text('EQ6-R'), findsOneWidget);
    expect(find.text('05h 30m 00s'), findsOneWidget);
    expect(find.text('-12° 15′ 00″'), findsOneWidget);
    expect(find.byType(Switch), findsNWidgets(2)); // tracking + auto-connect
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Stop'), findsOneWidget);
  });

  testWidgets('toggling tracking sends the tracking command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byKey(const Key('mount_tracking_switch')));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:tracking:enabled=true'));
  });

  testWidgets('Park sends the park command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.widgetWithText(OutlinedButton, 'Park'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:park:enabled=null'));
  });

  testWidgets('Home is hidden when the mount cannot find home', (tester) async {
    await _pump(tester, _status(canFindHome: false));
    expect(find.widgetWithText(OutlinedButton, 'Home'), findsNothing);
  });

  testWidgets('Home shows and sends the home command when supported',
      (tester) async {
    final api = await _pump(tester, _status(canFindHome: true));
    await tester.tap(find.widgetWithText(OutlinedButton, 'Home'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:home:enabled=null'));
  });

  testWidgets('Home is disabled while parked', (tester) async {
    await _pump(tester, _status(canFindHome: true, parked: true));
    final home = tester.widget<OutlinedButton>(
        find.widgetWithText(OutlinedButton, 'Home'));
    expect(home.onPressed, isNull); // must unpark before homing
  });

  testWidgets('parked mount shows Unpark and hides Park; tracking disabled',
      (tester) async {
    await _pump(tester, _status(parked: true));
    expect(find.widgetWithText(OutlinedButton, 'Unpark'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsNothing);
    final tracking =
        tester.widget<Switch>(find.byKey(const Key('mount_tracking_switch')));
    expect(tracking.onChanged, isNull); // disabled while parked
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No mount connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('manual control surfaces GoTo + speed + direction pad for a capable mount',
      (tester) async {
    await _pump(tester, _status(canMoveAxis: true, axisRates: const [1.0, 4.0]));
    expect(find.text('Manual control'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'GoTo'), findsOneWidget);
    expect(find.text('Speed'), findsOneWidget);
    expect(find.text('4°/s'), findsOneWidget); // a reported rate chip
    expect(find.byIcon(Icons.north), findsOneWidget); // a direction-pad button
    expect(find.byIcon(Icons.stop), findsOneWidget); // the centre stop
  });

  testWidgets('the direction pad is hidden when the mount cannot MoveAxis',
      (tester) async {
    await _pump(tester, _status(canMoveAxis: false));
    expect(find.byIcon(Icons.north), findsNothing);
  });

  testWidgets('GoTo dispatches a slew to the entered coordinates', (tester) async {
    final api = await _pump(tester, _status(canMoveAxis: true, axisRates: const [4.0]));
    await tester.enterText(find.widgetWithText(TextField, 'RA (h)'), '5.5');
    await tester.enterText(find.widgetWithText(TextField, 'Dec (°)'), '-12.25');
    await tester.tap(find.widgetWithText(FilledButton, 'GoTo'));
    await tester.pumpAndSettle();
    expect(api.calls.any((c) => c.startsWith('command:slew')), isTrue);
  });

  testWidgets('a held direction button stops the axis if the mount goes busy',
      (tester) async {
    await _wideSurface(tester);
    final api = _FakeMountApi(_status(canMoveAxis: true, axisRates: const [4.0]));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        mountApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(home: Scaffold(body: EquipmentMountPanel())),
    ));
    await tester.pumpAndSettle();
    // Press and hold the North button → starts a move at the picked rate.
    final hold = await tester.startGesture(tester.getCenter(find.byIcon(Icons.north)));
    await tester.pump();
    expect(api.calls.any((c) => c.startsWith('command:moveaxis') && c.endsWith('rate=4.0')),
        isTrue);
    // Mount goes busy from another source (a slew) while still held → pad disables,
    // and the held button must dispatch a stop (rate 0) on the enabled→false transition.
    api.status = _status(canMoveAxis: true, axisRates: const [4.0], runtimeState: 'slewing');
    await tester.pump(const Duration(seconds: 16)); // live poll picks up the new status
    await tester.pump();
    expect(api.calls.any((c) => c.startsWith('command:moveaxis') && c.endsWith('rate=0.0')),
        isTrue);
    await hold.up();
  });
}
