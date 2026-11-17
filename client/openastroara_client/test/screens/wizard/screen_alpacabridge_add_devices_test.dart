import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/wizard_state.dart';

import '../../support/alpacabridge_setup_fakes.dart';

const _server = AraServer(hostname: 'h', port: 5555);

class _Saved implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [_server];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

Future<FakeBridgeClient> _pump(
  WidgetTester tester, {
  List<AttachedCamera>? cameras,
  List<BridgeDriver>? drivers,
}) async {
  tester.view.physicalSize = const Size(900, 1400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final bridge = FakeBridgeClient(
    drivers: drivers ?? [zwoCameraDriver()],
    cameraList:
        cameras ??
        [
          camera(0, 'ZWO ASI294MC Pro', 'AAAA000000000001'),
          camera(1, 'ZWO ASI120MM Mini', 'BBBB000000000002'),
        ],
  );
  final c = ProviderContainer(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_Saved()),
      alpacaBridgeSetupClientProvider.overrideWithValue(bridge),
      equipmentDiscoveryApiFactoryProvider.overrideWithValue(
        (_) => FakeConfiguredApi(),
      ),
    ],
  );
  addTearDown(c.dispose);
  c.listen(wizardStepValidProvider, (_, _) {});
  await c.read(savedServersProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: ScreenAlpacaConnect())),
    ),
  );
  for (var i = 0; i < 4; i++) {
    await tester.pump();
  }
  return bridge;
}

void main() {
  testWidgets(
    'a rig with no devices leads with Found on this rig: ticked rows and one Add selected (2)',
    (tester) async {
      await _pump(tester);
      expect(find.text('Found on this rig'), findsOneWidget);
      for (final s in ['AAAA000000000001', 'BBBB000000000002']) {
        expect(
          tester
              .widget<Checkbox>(
                find.descendant(
                  of: find.byKey(Key('found-row-zwo:camera:$s')),
                  matching: find.byType(Checkbox),
                ),
              )
              .value,
          isTrue,
        );
      }
      expect(find.text('ZWO ASI294MC Pro'), findsOneWidget);
      expect(find.text('Add selected (2)'), findsOneWidget);
      expect(find.byKey(const Key('add-by-hand')), findsOneWidget);
      expect(find.text('Retry detection'), findsWidgets);
    },
  );

  testWidgets(
    'unticking a row updates the button; Add selected shows Added per row',
    (tester) async {
      final bridge = await _pump(tester);
      await tester.tap(
        find.byKey(const Key('found-row-zwo:camera:BBBB000000000002')),
      );
      await tester.pump();
      expect(find.text('Add selected (1)'), findsOneWidget);
      await tester.tap(find.byKey(const Key('add-selected')));
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(bridge.calls, ['AAAA000000000001']);
      expect(
        find.byKey(const Key('found-status-zwo:camera:AAAA000000000001')),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('found-status-zwo:camera:AAAA000000000001')),
          matching: find.text('Added'),
        ),
        findsOneWidget,
      );
    },
  );

  testWidgets('a failed row shows Failed with Retry and Edit', (tester) async {
    final bridge = await _pump(tester);
    bridge.outcomes = {
      'AAAA000000000001': const AddRefused('not in the allowlist'),
    };
    await tester.tap(find.byKey(const Key('add-selected')));
    for (var i = 0; i < 4; i++) {
      await tester.pump();
    }
    expect(find.text('Failed'), findsOneWidget);
    expect(find.textContaining('not in the allowlist'), findsOneWidget);
    expect(
      find.byKey(const Key('row-retry-zwo:camera:AAAA000000000001')),
      findsOneWidget,
    );
    expect(
      find.byKey(const Key('row-edit-zwo:camera:AAAA000000000001')),
      findsOneWidget,
    );
  });

  testWidgets(
    'nothing found says so and keeps Add a device by hand and Retry detection',
    (tester) async {
      await _pump(tester, cameras: const []);
      expect(find.byKey(const Key('found-none')), findsOneWidget);
      expect(find.byKey(const Key('add-selected')), findsNothing);
      expect(find.byKey(const Key('add-by-hand')), findsOneWidget);
      expect(find.text('Retry detection'), findsWidgets);
    },
  );

  testWidgets(
    'Add a device by hand opens the catalog form for a chosen driver',
    (tester) async {
      await _pump(tester);
      await tester.tap(find.byKey(const Key('add-by-hand')));
      await tester.pump();
      await tester.pump();
      expect(
        find.text('ZWO ASI camera'),
        findsOneWidget,
        reason: 'the driver list comes from the catalog',
      );
    },
  );

  testWidgets(
    'with no catalog the screen is today\'s: no Found list, no by-hand button',
    (tester) async {
      await _pump(tester, drivers: const []);
      expect(find.text('Found on this rig'), findsNothing);
      expect(find.byKey(const Key('add-by-hand')), findsNothing);
    },
  );
}
