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

/// Scriptable discovery fake: throws while [failing], returns
/// [devices] for that type (a clean "bridge reachable" response — empty
/// list or advertised slots) otherwise. Types in [failingTypes] always throw.
class _FakeDiscoveryApi implements EquipmentDiscoveryApi {
  bool failing;
  int scans = 0;
  final scannedTypes = <EquipmentDeviceType>{};
  Map<EquipmentDeviceType, List<DiscoveredDevice>> devices;
  Set<EquipmentDeviceType> failingTypes;
  _FakeDiscoveryApi({
    this.failing = false,
    this.devices = const {},
    this.failingTypes = const {},
  });

  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    if (type == EquipmentDeviceType.camera) scans++;
    scannedTypes.add(type);
    if (failing || failingTypes.contains(type)) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/equipment/discover/camera'),
        message: 'connection refused',
      );
    }
    return devices[type] ?? const [];
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
    expect(find.textContaining('AlpacaBridge found — no devices yet'),
        findsOneWidget);
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
    // HIG: a clean reachability result renders as SUCCESS (green check).
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
    expect(find.byIcon(Icons.warning_amber_rounded), findsNothing);
  });

  DiscoveredDevice device(EquipmentDeviceType type, String id) =>
      DiscoveredDevice(
        uniqueId: id,
        name: id,
        deviceType: type,
        hostName: '192.168.168.1',
        ipAddress: '192.168.168.1',
        ipPort: 6800,
        alpacaDeviceNumber: 0,
        useHttps: false,
      );

  testWidgets('the summary counts every device type, not just cameras, and '
      'reads as success', (tester) async {
    final api = _FakeDiscoveryApi(devices: {
      EquipmentDeviceType.camera: [
        device(EquipmentDeviceType.camera, 'main'),
        device(EquipmentDeviceType.camera, 'guide'),
      ],
      EquipmentDeviceType.mount: [device(EquipmentDeviceType.mount, 'eq6')],
      EquipmentDeviceType.focuser: [
        device(EquipmentDeviceType.focuser, 'eaf'),
      ],
      // The daemon's driverless manual wheel is not bridge gear.
      EquipmentDeviceType.filterWheel: [
        device(EquipmentDeviceType.filterWheel,
            DiscoveredDevice.manualFilterWheelId),
      ],
    });
    final container = await _pump(tester, api);

    expect(api.scannedTypes, isNot(contains(EquipmentDeviceType.guider)));
    expect(api.scannedTypes, contains(EquipmentDeviceType.switchDevice));
    expect(container.read(wizardStepValidProvider), isTrue);
    final message = tester
        .widget<Text>(find.textContaining('AlpacaBridge found'))
        .data;
    expect(message, contains('offering 2 cameras, 1 mount and 1 focuser'));
    expect(message, isNot(contains('filter wheel')));
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
    expect(find.byIcon(Icons.warning_amber_rounded), findsNothing);
  });

  testWidgets('an advertised phantom slot is counted as offered, never as '
      'connected gear', (tester) async {
    // A registered bridge slot with NO hardware behind it: the bridge
    // advertises a vendor name, /connected is false. The banner is green
    // (the bridge is reachable) but must say the device is unverified.
    final api = _FakeDiscoveryApi(devices: {
      EquipmentDeviceType.camera: [
        device(EquipmentDeviceType.camera, 'PLAYERONE_SN_0'),
      ],
    });
    final container = await _pump(tester, api);

    expect(container.read(wizardStepValidProvider), isTrue);
    final message = tester
        .widget<Text>(find.textContaining('AlpacaBridge found'))
        .data;
    expect(message, contains('offering 1 camera'));
    expect(message, contains('checked when you connect'));
    expect(message, isNot(contains('connected')));
    expect(message, isNot(contains('seen')));
    expect(find.byIcon(Icons.check_circle), findsOneWidget);
  });

  testWidgets('a non-camera type failing does not hide a reachable bridge',
      (tester) async {
    final api = _FakeDiscoveryApi(
      devices: {
        EquipmentDeviceType.camera: [
          device(EquipmentDeviceType.camera, 'main'),
        ],
      },
      failingTypes: {EquipmentDeviceType.dome},
    );
    final container = await _pump(tester, api);

    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.textContaining('AlpacaBridge found — offering 1 camera.'),
        findsOneWidget);
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
  });

  testWidgets('a failed camera handshake keeps Next locked even when other '
      'types answer', (tester) async {
    final api = _FakeDiscoveryApi(
      devices: {
        EquipmentDeviceType.mount: [device(EquipmentDeviceType.mount, 'eq6')],
      },
      failingTypes: {EquipmentDeviceType.camera},
    );
    final container = await _pump(tester, api);

    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.text('AlpacaBridge not detected.'), findsOneWidget);
    expect(find.textContaining('AlpacaBridge found'), findsNothing);
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
