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

/// Scriptable discovery fake: throws while [failing], returns an empty list
/// (a clean "bridge reachable, no gear on this scan" response) otherwise.
class _FakeDiscoveryApi implements EquipmentDiscoveryApi {
  bool failing;
  int scans = 0;
  _FakeDiscoveryApi({this.failing = false});

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
    return const <DiscoveredDevice>[];
  }
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
    expect(container.read(wizardStepValidProvider), isTrue);
    expect(find.textContaining('AlpacaBridge reachable'), findsOneWidget);
    expect(find.text('AlpacaBridge not detected.'), findsNothing);
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

  testWidgets('no active server: gated with the failure panel, no crash',
      (tester) async {
    final api = _FakeDiscoveryApi();
    final container = await _pump(tester, api, servers: const []);

    expect(api.scans, 0, reason: 'no server → nothing to probe');
    expect(container.read(wizardStepValidProvider), isFalse);
    expect(find.textContaining('No active server'), findsOneWidget);
  });
}
