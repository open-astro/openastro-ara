import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_devices_empty.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/equipment/equipment_discovery_provider.dart';
import 'package:openastroara/state/saved_server_state.dart';

import '../../support/alpacabridge_setup_fakes.dart';

const _server = AraServer(hostname: 'h', port: 5555);

Future<FakeBridgeClient> _pump(
  WidgetTester tester, {
  bool cameras = true,
}) async {
  tester.view.physicalSize = const Size(1024, 768);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final bridge = FakeBridgeClient(
    drivers: [zwoCameraDriver()],
    cameraList: cameras
        ? [camera(0, 'ZWO ASI294MC Pro', 'AAAA000000000001')]
        : const [],
  );
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(_server),
        alpacaBridgeSetupClientProvider.overrideWithValue(bridge),
        equipmentDiscoveryApiFactoryProvider.overrideWithValue(
          (_) => FakeConfiguredApi(),
        ),
      ],
      child: const MaterialApp(home: Scaffold(body: SimpleDevicesEmpty())),
    ),
  );
  for (var i = 0; i < 4; i++) {
    await tester.pump();
  }
  return bridge;
}

void main() {
  // ADR 0009: Simple Mode is a shell over the same rig, so its empty Devices section is the shared panel, not a second flow.
  testWidgets(
    'the empty Devices section lists what was found and adds it through the shared panel',
    (tester) async {
      final bridge = await _pump(tester);
      expect(find.text('Found on this rig'), findsOneWidget);
      expect(find.text('Add selected (1)'), findsOneWidget);
      await tester.tap(find.byKey(const Key('add-selected')));
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(bridge.calls, ['AAAA000000000001']);
      expect(find.text('Added'), findsOneWidget);
    },
  );

  testWidgets(
    'nothing found keeps Add a device by hand and its own Retry detection',
    (tester) async {
      await _pump(tester, cameras: false);
      expect(find.byKey(const Key('found-none')), findsOneWidget);
      expect(find.byKey(const Key('add-by-hand')), findsOneWidget);
      expect(find.byKey(const Key('retry-detection')), findsOneWidget);
    },
  );

  testWidgets('the Simple targets are at least 48 pt tall', (tester) async {
    await _pump(tester);
    expect(
      tester.getSize(find.byKey(const Key('add-selected'))).height,
      greaterThanOrEqualTo(48),
    );
    expect(
      tester.getSize(find.byKey(const Key('add-by-hand'))).height,
      greaterThanOrEqualTo(48),
    );
  });
}
