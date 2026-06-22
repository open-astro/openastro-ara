import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/safety_monitor_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_safety_monitor_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/safety_monitor_state.dart';
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

class _FakeSafetyApi implements EquipmentDeviceClient<SafetyMonitorStatus> {
  _FakeSafetyApi(this.status);
  SafetyMonitorStatus? status;
  final List<String> calls = [];
  int getCount = 0;

  @override
  Future<SafetyMonitorStatus?> getStatus() async {
    getCount++;
    return status;
  }
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  void close() {}
}

SafetyMonitorStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool safe = true,
}) =>
    SafetyMonitorStatus(
      deviceId: 'sm-0',
      name: 'CloudWatcher',
      connectionState: state,
      safe: safe,
      lastTransitionAt: '2026-06-22T04:00:00Z',
    );

// The settings panels are designed for the wide right-hand pane; give the test a
// generous surface so the shared SettingsSwitchRow doesn't overflow at 800px.
Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 900));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeSafetyApi> _pump(
    WidgetTester tester, SafetyMonitorStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeSafetyApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      safetyMonitorApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentSafetyMonitorPanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('a connected + safe monitor shows Safe and the Connected chip',
      (tester) async {
    await _pump(tester, _status(safe: true));
    expect(find.text('CloudWatcher'), findsOneWidget);
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Safe'), findsOneWidget);
    expect(find.text('Unsafe'), findsNothing);
  });

  testWidgets('a connected + unsafe monitor shows Unsafe', (tester) async {
    await _pump(tester, _status(safe: false));
    expect(find.text('Unsafe'), findsOneWidget);
    expect(find.text('Safe'), findsNothing);
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No safety monitor connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });

  testWidgets('a connecting monitor settles to connected via the poll',
      (tester) async {
    // The daemon's connect is 202 + background, so the first read shows
    // `connecting`. The generic engine polls while connecting; once the daemon
    // finishes, the card must settle to Connected/Safe without re-opening.
    await _wideSurface(tester);
    final api = _FakeSafetyApi(
        _status(state: EquipmentConnectionState.connecting, safe: false));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        safetyMonitorApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child:
          const MaterialApp(home: Scaffold(body: EquipmentSafetyMonitorPanel())),
    ));
    await tester.pump(); // build
    await tester.pump(); // resolve the initial getStatus
    expect(find.text('Connecting'), findsOneWidget);

    // Daemon finished connecting in the background.
    api.status = _status(state: EquipmentConnectionState.connected, safe: true);
    await tester.pump(const Duration(milliseconds: 1600)); // settle tick → refresh
    await tester.pump(); // resolve the refresh getStatus
    expect(find.text('Connected'), findsOneWidget);
    expect(find.text('Safe'), findsOneWidget);
    expect(find.text('Connecting'), findsNothing);
  });

  testWidgets('a device wedged in connecting stops polling at the cap',
      (tester) async {
    // A device stuck in `connecting` (e.g. network dropped after the 202) must
    // not poll forever — the settle-poll is capped at maxSettlePolls ticks.
    await _wideSurface(tester);
    final api = _FakeSafetyApi(
        _status(state: EquipmentConnectionState.connecting, safe: false));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        safetyMonitorApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child:
          const MaterialApp(home: Scaffold(body: EquipmentSafetyMonitorPanel())),
    ));
    await tester.pump(); // build
    await tester.pump(); // resolve the initial getStatus (getCount == 1)

    // Pump well past the cap; the device never settles in the fake.
    for (var i = 0; i < 60; i++) {
      await tester.pump(const Duration(milliseconds: 1600));
      await tester.pump();
    }
    // Initial read + at most maxSettlePolls (40) poll reads — NOT one per pump.
    expect(api.getCount, lessThanOrEqualTo(42));
    expect(find.text('Connecting'), findsOneWidget);
  });

  testWidgets('disconnect targets the device', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.byIcon(Icons.link_off));
    await tester.pumpAndSettle();
    expect(api.calls, contains('disconnect'));
  });
}
