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
      calls.add('command:$subpath:enabled=${body?['enabled']}');
  @override
  void close() {}
}

MountStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canSetTracking = true,
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
        canFindHome: false,
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
    expect(find.text('-12° 15′'), findsOneWidget);
    expect(find.byType(Switch), findsNWidgets(2)); // tracking + auto-connect
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Stop'), findsOneWidget);
  });

  testWidgets('toggling tracking sends the tracking command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byType(Switch).first); // tracking (above auto-connect)
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:tracking:enabled=true'));
  });

  testWidgets('Park sends the park command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.widgetWithText(OutlinedButton, 'Park'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:park:enabled=null'));
  });

  testWidgets('parked mount shows Unpark and hides Park; tracking disabled',
      (tester) async {
    await _pump(tester, _status(parked: true));
    expect(find.widgetWithText(OutlinedButton, 'Unpark'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsNothing);
    final tracking = tester.widget<Switch>(find.byType(Switch).first);
    expect(tracking.onChanged, isNull); // disabled while parked
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No mount connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
