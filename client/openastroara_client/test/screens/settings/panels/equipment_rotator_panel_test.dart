import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_rotator_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
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

class _FakeRotatorApi implements EquipmentDeviceClient<RotatorStatus> {
  _FakeRotatorApi(this.status);
  RotatorStatus? status;
  final List<String> calls = [];
  @override
  Future<RotatorStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath:$body');
  @override
  void close() {}
}

RotatorStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canReverse = true,
  bool reverse = false,
  String runtimeState = 'idle',
}) =>
    RotatorStatus(
      deviceId: 'rot-0',
      name: 'Pyxis',
      connectionState: state,
      capabilities:
          RotatorCapabilities(canReverse: canReverse, stepSize: 0.5),
      runtimeState: runtimeState,
      mechanicalAngleDeg: 12.5,
      skyAngleDeg: 100.0,
      reverse: reverse,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeRotatorApi> _pump(WidgetTester tester, RotatorStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeRotatorApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      rotatorApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentRotatorPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live angles + reverse toggle + move/sync', (tester) async {
    await _pump(tester, _status());
    expect(find.text('Pyxis'), findsOneWidget);
    expect(find.text('Sky angle'), findsOneWidget);
    expect(find.text('100.0°'), findsOneWidget);
    expect(find.text('12.5°'), findsOneWidget);
    expect(find.text('Reverse direction'), findsOneWidget);
    // Two switches: the reverse toggle (in the card) + auto-connect-on-boot.
    expect(find.byType(Switch), findsNWidgets(2));
    expect(find.widgetWithText(FilledButton, 'Move'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Sync'), findsOneWidget);
  });

  testWidgets('Move sends target_angle_deg + use_sky_angle', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '270');
    await tester.tap(find.widgetWithText(FilledButton, 'Move'));
    await tester.pumpAndSettle();
    expect(
        api.calls.any((c) =>
            c.startsWith('command:move:') &&
            c.contains('target_angle_deg: 270') &&
            c.contains('use_sky_angle: true')),
        isTrue,
        reason: api.calls.toString());
  });

  testWidgets('Sync sends sky_angle_deg', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '45');
    await tester.tap(find.widgetWithText(OutlinedButton, 'Sync'));
    await tester.pumpAndSettle();
    expect(api.calls.any((c) => c.startsWith('command:sync:') && c.contains('45')),
        isTrue,
        reason: api.calls.toString());
  });

  testWidgets('toggling Reverse sends the reverse command', (tester) async {
    final api = await _pump(tester, _status(reverse: false));
    // The reverse toggle is the card's Switch (first in tree order, before the
    // auto-connect-on-boot Switch).
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(api.calls.any((c) => c.startsWith('command:reverse:')), isTrue,
        reason: api.calls.toString());
  });

  testWidgets('no reverse toggle when the device cannot reverse', (tester) async {
    await _pump(tester, _status(canReverse: false));
    expect(find.text('Reverse direction'), findsNothing);
    // Only the auto-connect-on-boot switch remains.
    expect(find.byType(Switch), findsOneWidget);
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No rotator connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
