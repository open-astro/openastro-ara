import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/equipment_dome_panel.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
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

class _FakeDomeApi implements EquipmentDeviceClient<DomeStatus> {
  _FakeDomeApi(this.status);
  DomeStatus? status;
  final List<String> calls = [];
  @override
  Future<DomeStatus?> getStatus() async => status;
  @override
  Future<void> connect(DiscoveredDevice device) async =>
      calls.add('connect:${device.name}');
  @override
  Future<void> disconnect() async => calls.add('disconnect');
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add('command:$subpath:${body?['target_azimuth_deg']}');
  @override
  void close() {}
}

DomeStatus _status({
  EquipmentConnectionState state = EquipmentConnectionState.connected,
  bool canSetShutter = true,
  bool canSetAzimuth = true,
  bool canPark = true,
  String runtimeState = 'idle',
}) =>
    DomeStatus(
      deviceId: 'dome-0',
      name: 'Observatory',
      connectionState: state,
      capabilities: DomeCapabilities(
        canSetShutter: canSetShutter,
        canSetAzimuth: canSetAzimuth,
        canSyncAzimuth: false,
        canPark: canPark,
        canFindHome: true,
      ),
      runtimeState: runtimeState,
      azimuthDeg: 180,
      shutterOpen: true,
      atHome: false,
      parked: false,
    );

Future<void> _wideSurface(WidgetTester tester) async {
  await tester.binding.setSurfaceSize(const Size(1200, 1200));
  addTearDown(() => tester.binding.setSurfaceSize(null));
}

Future<_FakeDomeApi> _pump(WidgetTester tester, DomeStatus? status) async {
  await _wideSurface(tester);
  final api = _FakeDomeApi(status);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => true),
      savedServerServiceProvider.overrideWithValue(
          _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
      domeApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(home: Scaffold(body: EquipmentDomePanel())),
  ));
  await tester.pumpAndSettle();
  return api;
}

void main() {
  testWidgets('renders live state + shutter/slew/park controls', (tester) async {
    await _pump(tester, _status());
    expect(find.text('Observatory'), findsOneWidget);
    expect(find.text('Shutter'), findsOneWidget);
    expect(find.text('180°'), findsWidgets);
    expect(find.widgetWithText(OutlinedButton, 'Open shutter'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Close shutter'), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Park'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Slew'), findsOneWidget);
  });

  testWidgets('Open shutter sends the command', (tester) async {
    final api = await _pump(tester, _status());
    await tester.tap(find.widgetWithText(OutlinedButton, 'Open shutter'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:shutter/open:null'));
  });

  testWidgets('Slew sends the target azimuth', (tester) async {
    final api = await _pump(tester, _status());
    await tester.enterText(find.byType(TextField), '90');
    await tester.tap(find.widgetWithText(FilledButton, 'Slew'));
    await tester.pumpAndSettle();
    expect(api.calls, contains('command:slew:90.0'));
  });

  testWidgets('controls hidden / disabled by capability + busy', (tester) async {
    // No shutter capability → no shutter buttons; slewing → Slew disabled.
    await _pump(tester,
        _status(canSetShutter: false, runtimeState: 'slewing'));
    expect(find.widgetWithText(OutlinedButton, 'Open shutter'), findsNothing);
    expect(find.text('Slewing…'), findsOneWidget);
    final slew = tester.widget<FilledButton>(find.widgetWithText(FilledButton, 'Slew'));
    expect(slew.onPressed, isNull);
  });

  testWidgets('no device connected shows the empty state + Connect…',
      (tester) async {
    await _pump(tester, null);
    expect(find.text('No dome connected.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Connect…'), findsOneWidget);
  });
}
