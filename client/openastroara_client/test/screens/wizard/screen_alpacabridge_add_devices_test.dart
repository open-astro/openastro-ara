import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/guider/guider_choice_state.dart';
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
  bool gated = false,
  bool camerasThrow = false,
  FakeConfiguredApi? daemon,
}) async {
  tester.view.physicalSize = const Size(900, 1400);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final bridge = FakeBridgeClient(
    drivers: drivers ?? [zwoCameraDriver()],
    gated: gated,
    camerasThrow: camerasThrow,
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
      alpacaBridgeSetupClientFactoryProvider.overrideWithValue(bridge.factory),
      alpacaDeviceNamesApiProvider.overrideWithValue(FakeNames()),
      bridgeAddressOverrideProvider.overrideWithValue(null),
      equipmentDiscoveryApiFactoryProvider.overrideWithValue(
        (_) => daemon ?? FakeConfiguredApi(),
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
    'Retry detection on the wizard screen also reads the cameras again',
    (tester) async {
      final bridge = await _pump(tester);
      expect(bridge.cameraReads, 1);
      await tester.tap(find.text('Retry detection').first);
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(bridge.cameraReads, 2);
    },
  );

  testWidgets(
    'while the daemon cannot read the cameras there is an error line and Retry, and no By hand',
    (tester) async {
      await _pump(tester, camerasThrow: true);
      expect(find.byKey(const Key('found-error')), findsOneWidget);
      expect(find.byKey(const Key('add-by-hand')), findsNothing);
      expect(find.text('Retry detection'), findsWidgets);
    },
  );

  testWidgets(
    'during a sequence the row says Adding…, the rows behind say Waiting, and the buttons are off',
    (tester) async {
      final bridge = await _pump(tester, gated: true);
      await tester.tap(find.byKey(const Key('add-selected')));
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(
        find.descendant(
          of: find.byKey(const Key('found-status-zwo:camera:AAAA000000000001')),
          matching: find.text('Adding…'),
        ),
        findsOneWidget,
      );
      expect(
        find.descendant(
          of: find.byKey(const Key('found-status-zwo:camera:BBBB000000000002')),
          matching: find.text('Waiting'),
        ),
        findsOneWidget,
      );
      expect(
        tester
            .widget<FilledButton>(find.byKey(const Key('add-selected')))
            .onPressed,
        isNull,
      );
      expect(
        tester
            .widget<ButtonStyleButton>(
              find.ancestor(
                of: find.text('Retry detection').first,
                matching: find.byWidgetPredicate((w) => w is ButtonStyleButton),
              ),
            )
            .onPressed,
        isNull,
      );
      bridge.release();
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      bridge.release();
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(bridge.calls.length, 2);
    },
  );

  testWidgets(
    'Add a device by hand lists the catalog drivers and opens the form of the chosen one',
    (tester) async {
      await _pump(tester, drivers: [zwoCameraDriver(), qhyCameraDriver()]);
      expect(
        find.text('ZWO ASI camera'),
        findsNothing,
        reason: 'not before the tap',
      );
      await tester.tap(find.byKey(const Key('add-by-hand')));
      await tester.pump();
      await tester.pump();
      final list = find.byKey(const Key('by-hand-list'));
      expect(
        find.descendant(of: list, matching: find.text('ZWO ASI camera')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: list, matching: find.text('QHY camera')),
        findsOneWidget,
      );
      await tester.tap(find.byKey(const Key('by-hand-driver-qhy-camera')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('form-submit')), findsOneWidget);
      expect(find.byKey(const Key('form-field-cameraIndex')), findsOneWidget);
    },
  );

  testWidgets(
    'by hand, a camera driver asks Which camera? and sends the picked camera\'s serial and name',
    (tester) async {
      final bridge = await _pump(tester);
      await tester.tap(find.byKey(const Key('add-by-hand')));
      await tester.pump();
      await tester.pump();
      await tester.tap(find.byKey(const Key('by-hand-driver-zwo-camera')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Which camera?'), findsOneWidget);
      expect(
        find.byKey(const Key('form-field-serialNumber')),
        findsNothing,
        reason: 'the pick fills it',
      );
      expect(find.byKey(const Key('form-field-cameraName')), findsNothing);
      await tester.tap(
        find.byKey(const Key('by-hand-camera-BBBB000000000002')),
      );
      await tester.pump();
      await tester.tap(find.byKey(const Key('form-submit')));
      for (var i = 0; i < 4; i++) {
        await tester.pump();
      }
      expect(bridge.bodies.single['serialNumber'], 'BBBB000000000002');
      expect(bridge.bodies.single['cameraName'], 'ZWO ASI120MM Mini');
    },
  );

  testWidgets(
    'by hand, a camera list that cannot be read opens the form to type the camera in',
    (tester) async {
      final bridge = await _pump(tester);
      bridge.camerasThrow = true;
      await tester.tap(find.byKey(const Key('add-by-hand')));
      await tester.pump();
      await tester.pump();
      await tester.tap(find.byKey(const Key('by-hand-driver-zwo-camera')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('form-field-serialNumber')), findsOneWidget);
    },
  );

  Future<void> fill(WidgetTester tester, String value) async {
    await tester.enterText(
      find.descendant(
        of: find.byKey(const Key('form-field-usbPath')),
        matching: find.byType(TextField),
      ),
      value,
    );
  }

  testWidgets(
    'Add selected walks the needs-details forms one by one; the last Continue starts the one sequence and one re-read',
    (tester) async {
      final daemon = FakeConfiguredApi();
      final bridge = await _pump(
        tester,
        drivers: [
          zwoCameraDriver(extraFields: [requiredNoDefault]),
        ],
        daemon: daemon,
      );
      final scansBefore = daemon.cameraScans;
      await tester.tap(find.byKey(const Key('add-selected')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('form-field-usbPath')), findsOneWidget);
      expect(bridge.calls, isEmpty);
      await fill(tester, '/dev/a');
      await tester.tap(find.byKey(const Key('form-submit')));
      await tester.pump();
      await tester.pump();
      expect(
        find.byKey(const Key('form-field-usbPath')),
        findsOneWidget,
        reason: 'the second row has its own form',
      );
      expect(bridge.calls, isEmpty, reason: 'nothing is sent between forms');
      await fill(tester, '/dev/b');
      await tester.tap(find.byKey(const Key('form-submit')));
      for (var i = 0; i < 6; i++) {
        await tester.pump();
      }
      expect(bridge.calls, ['AAAA000000000001', 'BBBB000000000002']);
      expect(bridge.bodies.map((b) => b['usbPath']), ['/dev/a', '/dev/b']);
      expect(daemon.cameraScans - scansBefore, 1, reason: 'one re-read');
      expect(find.byKey(const Key('form-field-usbPath')), findsNothing);
    },
  );

  testWidgets(
    'Back on a form returns to the list with nothing sent; details already entered are kept',
    (tester) async {
      final bridge = await _pump(
        tester,
        drivers: [
          zwoCameraDriver(extraFields: [requiredNoDefault]),
        ],
      );
      await tester.tap(find.byKey(const Key('add-selected')));
      await tester.pump();
      await tester.pump();
      await fill(tester, '/dev/a');
      await tester.tap(find.byKey(const Key('form-submit')));
      await tester.pump();
      await tester.pump();
      await tester.tap(find.byKey(const Key('form-back')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Found on this rig'), findsOneWidget);
      expect(find.byKey(const Key('form-field-usbPath')), findsNothing);
      expect(bridge.calls, isEmpty);
      await tester.tap(find.byKey(const Key('add-selected')));
      await tester.pump();
      await tester.pump();
      expect(
        find.byKey(const Key('form-field-usbPath')),
        findsOneWidget,
        reason: 'only the second row still needs its details',
      );
      await fill(tester, '/dev/b');
      await tester.tap(find.byKey(const Key('form-submit')));
      for (var i = 0; i < 6; i++) {
        await tester.pump();
      }
      expect(bridge.bodies.map((b) => b['usbPath']), ['/dev/a', '/dev/b']);
    },
  );

  testWidgets('by-hand Add tapped twice sends one add', (tester) async {
    final bridge = await _pump(
      tester,
      drivers: [zwoFocuserDriver()],
      gated: true,
    );
    await tester.tap(find.byKey(const Key('add-by-hand')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('by-hand-driver-zwo-focuser')));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('form-submit')));
    await tester.pump();
    final submit = find.byKey(const Key('form-submit'));
    expect(tester.widget<FilledButton>(submit).onPressed, isNull);
    expect(
      find.descendant(of: submit, matching: find.text('Adding…')),
      findsOneWidget,
    );
    expect(
      tester
          .widget<ButtonStyleButton>(
            find.ancestor(
              of: find.text('Retry detection').first,
              matching: find.byWidgetPredicate((w) => w is ButtonStyleButton),
            ),
          )
          .onPressed,
      isNull,
      reason: 'the wizard\'s Retry detection waits too',
    );
    await tester.tap(submit, warnIfMissed: false);
    await tester.pump();
    expect(bridge.calls.length, 1);
    bridge.release();
    for (var i = 0; i < 6; i++) {
      await tester.pump();
    }
    expect(find.byKey(const Key('by-hand-result')), findsOneWidget);
  });

  testWidgets('By hand is disabled while Add selected runs', (tester) async {
    final bridge = await _pump(tester, gated: true);
    await tester.tap(find.byKey(const Key('add-selected')));
    await tester.pump();
    await tester.pump();
    expect(
      tester
          .widget<OutlinedButton>(find.byKey(const Key('add-by-hand')))
          .onPressed,
      isNull,
    );
    final c = ProviderScope.containerOf(
      tester.element(find.byType(ScreenAlpacaConnect)),
    );
    final byHand = c.read(foundDevicesProvider.notifier).addByHand({
      'vendor': 'zwo',
      'deviceType': 'focuser',
    });
    await tester.pump();
    expect(bridge.maxInFlight, 1);
    expect(await byHand, isNull, reason: 'refused while a sequence runs');
    for (var i = 0; i < 2; i++) {
      bridge.release();
      for (var j = 0; j < 4; j++) {
        await tester.pump();
      }
    }
    expect(bridge.calls, ['AAAA000000000001', 'BBBB000000000002']);
    expect(bridge.maxInFlight, 1);
  });

  testWidgets(
    'a found row reads its device name and its tick to a screen reader',
    (tester) async {
      final semantics = tester.ensureSemantics();
      await _pump(tester);
      final node = tester.getSemantics(
        find.descendant(
          of: find.byKey(const Key('found-row-zwo:camera:AAAA000000000001')),
          matching: find.byType(MergeSemantics),
        ),
      );
      expect(node.label, contains('ZWO ASI294MC Pro'));
      expect(node, isSemantics(hasCheckedState: true, isChecked: true));
      semantics.dispose();
    },
  );

  testWidgets('a refusal with no detail shows a translated sentence', (
    tester,
  ) async {
    final bridge = await _pump(tester);
    bridge.outcomes = {'AAAA000000000001': const AddRefused('')};
    await tester.tap(find.byKey(const Key('add-selected')));
    for (var i = 0; i < 4; i++) {
      await tester.pump();
    }
    expect(find.text('Ara refused this device.'), findsOneWidget);
  });

  testWidgets(
    'with no catalog the screen is today\'s: no Found list, no by-hand button',
    (tester) async {
      await _pump(tester, drivers: const []);
      expect(find.text('Found on this rig'), findsNothing);
      expect(find.byKey(const Key('add-by-hand')), findsNothing);
    },
  );
}
