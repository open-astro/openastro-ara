import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_filter_wheel_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
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

class _FakeFwApi implements EquipmentDeviceClient<FilterWheelStatus> {
  _FakeFwApi(this.status);
  FilterWheelStatus? status;
  final List<String> calls = [];
  @override
  Future<FilterWheelStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath:${body?['position']}');
  @override
  void close() {}
}

FilterWheelStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  int? currentSlot = 0,
  String runtimeState = 'idle',
}) =>
    FilterWheelStatus(
      deviceId: 'fw-0',
      name: 'EFW',
      connectionState: state,
      runtimeState: runtimeState,
      currentSlot: currentSlot,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'Hα', focusOffset: 12),
      ],
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeFwApi> _pump(WidgetTester tester, FilterWheelStatus? status,
    {bool settle = true}) async {
  await _wideSurface(tester);
  final api = _FakeFwApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      filterWheelApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentFilterWheelPanel())),
  ));
  if (settle) await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders the device slots with the active one marked', (tester) async {
    await _pump(tester, _status(currentSlot: 0));
    expect(find.text('EFW'), findsOneWidget);
    expect(find.text('L'), findsWidgets);
    expect(find.text('Hα'), findsWidgets);
    // Focus offset is shown because Hα has a non-zero offset.
    expect(find.text('focus offset 12'), findsOneWidget);
    // Active slot gets a green check; the selectable row a chevron.
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
    expect(find.byIcon(Icons.chevron_right), findsOneWidget);
  });

  testWidgets('shows the driver slot numbers (0-indexed)', (tester) async {
    await _pump(tester, _status(currentSlot: 0));
    expect(find.text('0'), findsOneWidget);
    expect(find.text('1'), findsOneWidget);
  });

  testWidgets('hides the focus-offset column when every slot is 0', (tester) async {
    await _pump(
        tester,
        FilterWheelStatus(
          deviceId: 'fw-0',
          name: 'EFW',
          connectionState: EquipmentConnectionState.connected,
          runtimeState: 'idle',
          currentSlot: 0,
          slots: const [
            FilterSlot(position: 0, name: 'L', focusOffset: 0),
            FilterSlot(position: 1, name: 'R', focusOffset: 0),
          ],
        ));
    expect(find.textContaining('focus offset'), findsNothing);
  });

  testWidgets('hides the Slot labels section while a wheel is connected',
      (tester) async {
    await _pump(tester, _status(currentSlot: 0));
    expect(find.text('Slot labels (for sequences)'), findsNothing);
  });

  testWidgets('shows the Slot labels section when no wheel is connected',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('Slot labels (for sequences)'), findsOneWidget);
  });

  testWidgets('tapping a row sends the change command', (tester) async {
    final api = await _pump(tester, _status(currentSlot: 0));
    // The whole row is the tap target — no far-right button to aim at.
    await tester.tap(find.text('Hα'));
    await tester.pump(const Duration(milliseconds: 50));
    expect(api.calls, contains('command:change:1'));
    // Let the backstop timers clear so nothing outlives the test.
    await tester.pump(const Duration(seconds: 11));
    await tester.pump();
  });

  testWidgets('selecting shows an instant spinner on the pending row',
      (tester) async {
    final api = await _pump(tester, _status(currentSlot: 0));
    await tester.tap(find.text('Hα'));
    await tester.pump(); // one frame — before any poll reports the move
    expect(find.byType(CircularProgressIndicator), findsWidgets,
        reason: 'instant busy feedback, not waiting for the live poll');
    expect(api.calls, contains('command:change:1'));
    await tester.pump(const Duration(seconds: 11)); // stall backstop clears
    await tester.pump();
  });

  testWidgets('a moving wheel is not selectable', (tester) async {
    final api = await _pump(
        tester, _status(currentSlot: null, runtimeState: 'moving'),
        settle: false);
    // Let the async status read land, then render the slot rows.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 100));
    expect(find.text('Hα'), findsOneWidget, reason: 'slots render while moving');
    await tester.tap(find.text('Hα'));
    await tester.pump();
    expect(api.calls.where((c) => c.startsWith('command:change')), isEmpty,
        reason: 'rows are disabled while the wheel is moving');
    await tester.pump(const Duration(seconds: 11));
    await tester.pump();
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No filter wheel connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
