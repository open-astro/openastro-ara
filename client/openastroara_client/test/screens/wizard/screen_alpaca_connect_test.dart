import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';
import 'package:openastroara/services/equipment_discovery_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/wizard_state.dart';

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

/// Scriptable discovery fake: throws while [failing], returns [devices]
/// (a clean "bridge reachable" response — empty list or advertised slots)
/// otherwise.
class _FakeDiscoveryApi implements EquipmentDiscoveryApi {
  bool failing;
  int scans = 0;
  List<DiscoveredDevice> devices;
  _FakeDiscoveryApi({this.failing = false, this.devices = const []});

  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    scans++;
    if (failing) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/equipment/discover/camera'),
        message: 'connection refused',
      );
    }
    return devices;
  }

  int closes = 0;
  @override
  void close() => closes++;
}

const _server = AraServer(hostname: 'h', port: 5555);

Future<ProviderContainer> _pump(WidgetTester tester, _FakeDiscoveryApi api,
    {List<AraServer> servers = const [_server]}) async {
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    equipmentDiscoveryApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(container.dispose);
  // Keep the autoDispose validity provider alive the way the live WizardShell
  // does (it watches it) — otherwise it resets across each pump.
  container.listen(wizardStepValidProvider, (_, _) {});
  // Pre-warm the saved servers so the screen's synchronous read sees data.
  await container.read(savedServersProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ScreenAlpacaConnect())),
  ));
  // First pump mounts + runs the post-frame gate/auto-probe; the second lets
  // the (synchronously-completing) fake probe's continuations apply.
  await tester.pump();
  await tester.pump();
  return container;
}

Finder _addressField() => find.ancestor(
      of: find.text('AlpacaBridge address'),
      matching: find.byType(TextField),
    );

void main() {
  testWidgets('§68.2 happy path: auto-probe succeeds and Next unblocks with '
      'zero clicks (empty device list still counts)', (tester) async {
    final api = _FakeDiscoveryApi();
    final container = await _pump(tester, api);

    expect(api.scans, 1, reason: 'the probe runs automatically on entry');
    expect(api.closes, 1, reason: 'the one-shot probe client is closed (r3)');
    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.textContaining('AlpacaBridge found'), findsOneWidget);
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
  });

  testWidgets('§68.2 advertised-but-unverified devices are reported honestly — '
      'reachability, not a connected count', (tester) async {
    // A registered bridge slot with NO hardware behind it (phantom device):
    // the bridge advertises a vendor name, /connected is false. The success
    // copy must not read as "cameras seen".
    const phantom = DiscoveredDevice(
      uniqueId: 'PLAYERONE_SN_0',
      name: 'Mars-C II',
      deviceType: EquipmentDeviceType.camera,
      hostName: '192.168.168.1',
      ipAddress: '192.168.168.1',
      ipPort: 6800,
      alpacaDeviceNumber: 1,
      useHttps: false,
    );
    final api = _FakeDiscoveryApi(devices: const [phantom]);
    final container = await _pump(tester, api);

    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'reachability gate still passes with advertised slots');
    final message = tester
        .widget<Text>(find.textContaining('AlpacaBridge found'))
        .data;
    expect(message, contains('AlpacaBridge found'));
    expect(message, contains('advertised'));
    expect(message, contains('connectivity not verified'));
    expect(message, isNot(contains('camera(s) seen')));
    expect(message, isNot(contains('seen on this scan')));
  });

  testWidgets('§68.2 missing bridge: Next stays blocked, the install command '
      'shows, and Retry detection recovers', (tester) async {
    final api = _FakeDiscoveryApi(failing: true);
    final container = await _pump(tester, api);

    expect(container.read(wizardStepValidProvider), isFalse,
        reason: 'no handshake → Next gated');
    expect(find.text('AlpacaBridge not detected.'), findsOneWidget);
    expect(find.text('sudo apt install alpaca-bridge'), findsOneWidget);

    // Bridge comes up; the user retries.
    api.failing = false;
    await tester.tap(find.text('Retry detection'));
    await tester.pump();
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
  });

  testWidgets('§68.2 non-standard-bridge skip: disabled until an address '
      'override is entered, then unblocks Next', (tester) async {
    final api = _FakeDiscoveryApi(failing: true);
    final container = await _pump(tester, api);

    final skip = find.widgetWithText(
        TextButton, 'Skip — I\'m using a non-standard bridge address');
    expect(skip, findsOneWidget);
    expect(tester.widget<TextButton>(skip).onPressed, isNull,
        reason: 'nothing to skip TO without an address override');
    expect(container.read(wizardStepValidProvider), isFalse);

    await tester.enterText(_addressField(), '10.0.0.5:11111');
    await tester.pump();
    expect(tester.widget<TextButton>(skip).onPressed, isNotNull);

    // The failure panel sits below the fold in the test viewport.
    await tester.ensureVisible(skip);
    await tester.pump();
    await tester.tap(skip);
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'explicit skip with an override unblocks Next');
    expect(find.text('Continuing with the address override.'), findsOneWidget);
  });

  testWidgets('§68.2 clearing the address override revokes a granted skip '
      '(r1 fix)', (tester) async {
    final api = _FakeDiscoveryApi(failing: true);
    final container = await _pump(tester, api);

    await tester.enterText(_addressField(), '10.0.0.5:11111');
    await tester.pump();
    final skip = find.widgetWithText(
        TextButton, 'Skip — I\'m using a non-standard bridge address');
    await tester.ensureVisible(skip);
    await tester.pump();
    await tester.tap(skip);
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isTrue);

    // The skip was granted FOR that override — clearing it re-gates Next.
    await tester.enterText(_addressField(), '');
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isFalse,
        reason: 'no handshake and no override left to skip to');
  });

  testWidgets('§68.2 a failed retry AFTER a success re-gates Next (r2 fix)',
      (tester) async {
    final api = _FakeDiscoveryApi();
    final container = await _pump(tester, api);
    expect(container.read(wizardStepValidProvider), isTrue,
        reason: 'auto-probe succeeded');

    // The bridge goes down; the user retries — Next must re-lock.
    api.failing = true;
    await tester.tap(find.text('Retry detection'));
    await tester.pump();
    await tester.pump();
    expect(container.read(wizardStepValidProvider), isFalse,
        reason: 'a failed retry must not inherit the earlier success');
    expect(find.text('AlpacaBridge not detected.'), findsOneWidget);
  });

  testWidgets('no active server: gated with the failure panel, no crash',
      (tester) async {
    final api = _FakeDiscoveryApi();
    final container = await _pump(tester, api, servers: const []);

    expect(api.scans, 0, reason: 'no server → nothing to probe');
    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.textContaining('Connect to your rig'), findsOneWidget);
    // r3: not a bridge problem — the install command would be a red herring.
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
    expect(find.text('sudo apt install alpaca-bridge'), findsNothing);
  });
}
