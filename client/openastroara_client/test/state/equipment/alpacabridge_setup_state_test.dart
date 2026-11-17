import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/equipment/equipment_discovery_provider.dart';
import 'package:openastroara/state/saved_server_state.dart';

import '../../support/alpacabridge_setup_fakes.dart';

const _server = AraServer(hostname: 'h', port: 5555);

/// Three attached ZWO cameras with made-up serials (the cameras route is the only detection today).
final _three = [
  camera(0, 'ZWO ASI294MC Pro', 'AAAA000000000001'),
  camera(1, 'ZWO ASI120MM Mini', 'BBBB000000000002'),
  camera(2, 'ZWO ASI533MC Pro', 'CCCC000000000003'),
];

({ProviderContainer c, FakeBridgeClient bridge, FakeConfiguredApi daemon})
_make({
  List<AttachedCamera>? cameras,
  List<BridgeDriver>? drivers,
  Map<String, AddOutcome> outcomes = const {},
  bool camerasThrow = false,
}) {
  final bridge = FakeBridgeClient(
    drivers: drivers ?? [zwoCameraDriver()],
    cameraList: cameras ?? _three,
    outcomes: outcomes,
    camerasThrow: camerasThrow,
  );
  final daemon = FakeConfiguredApi();
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(_server),
      alpacaBridgeSetupClientProvider.overrideWithValue(bridge),
      equipmentDiscoveryApiFactoryProvider.overrideWithValue((_) => daemon),
    ],
  );
  addTearDown(c.dispose);
  return (c: c, bridge: bridge, daemon: daemon);
}

Future<List<FoundRow>> _rows(ProviderContainer c) =>
    c.read(foundDevicesProvider.future);
FoundDevicesNotifier _n(ProviderContainer c) =>
    c.read(foundDevicesProvider.notifier);
List<FoundRow> _now(ProviderContainer c) =>
    c.read(foundDevicesProvider).requireValue;
FoundRow _row(ProviderContainer c, String serial) =>
    _now(c).singleWhere((r) => r.serial == serial);

void main() {
  test('each attached camera is a row, ticked, found, in the order the daemon lists them', () async {
    final t = _make();
    final rows = await _rows(t.c);
    expect(rows.map((r) => r.serial), _three.map((x) => x.serial));
    expect(
      rows.every((r) => r.ticked && r.status == FoundStatus.found),
      isTrue,
    );
    expect(rows.first.name, 'ZWO ASI294MC Pro');
    expect(rows.first.deviceType, 'camera');
    expect(rows.first.vendor, 'zwo');
    expect(selectedCount(rows), 3);
  });

  test(
    'nothing attached is an empty list, not an error (the nothing-found state)',
    () async {
      final t = _make(cameras: const []);
      expect(await _rows(t.c), isEmpty);
    },
  );

  test('with no catalog (not an AlpacaBridge, or too old) there are no rows even with cameras attached', () async {
    final t = _make(drivers: const []);
    expect(await _rows(t.c), isEmpty);
  });

  test('a camera the bridge already offers (same name) is not a row', () async {
    final t = _make();
    t.daemon.configured = [configuredCamera('ZWO ASI120MM Mini')];
    expect((await _rows(t.c)).map((r) => r.serial), [
      'AAAA000000000001',
      'CCCC000000000003',
    ]);
  });

  test(
    'a failed camera read is an error the screen can show, not an empty list',
    () async {
      final t = _make(camerasThrow: true);
      await expectLater(_rows(t.c), throwsA(anything));
      expect(t.c.read(foundDevicesProvider).hasError, isTrue);
    },
  );

  test(
    'unticking a row leaves it out of Add selected and out of the count',
    () async {
      final t = _make();
      await _rows(t.c);
      _n(t.c).toggle(_row(t.c, 'BBBB000000000002').id);
      expect(selectedCount(_now(t.c)), 2);
      await _n(t.c).addSelected();
      expect(t.bridge.calls, ['AAAA000000000001', 'CCCC000000000003']);
      expect(
        _row(t.c, 'BBBB000000000002').status,
        FoundStatus.found,
        reason: 'an unticked row stays as it was',
      );
      _n(t.c).toggle(_row(t.c, 'BBBB000000000002').id);
      expect(_row(t.c, 'BBBB000000000002').ticked, isTrue);
    },
  );

  test('a row sends the flat body: vendor, deviceType and the attached camera\'s serial and name', () async {
    final t = _make(cameras: [_three.first]);
    await _rows(t.c);
    await _n(t.c).addSelected();
    expect(
      t.bridge.bodies.single,
      {
        'vendor': 'zwo',
        'deviceType': 'camera',
        'serialNumber': 'AAAA000000000001',
        'cameraName': 'ZWO ASI294MC Pro',
      },
      reason: 'the serial binds the entry, not the index; the bridge applies the other defaults',
    );
  });

  test('Add selected sends one add per row, one after the other, and continues past a failure', () async {
    final t = _make(
      outcomes: {
        'BBBB000000000002': const AddRefused(
          'gpioChip is not in the board allowlist',
        ),
      },
    );
    await _rows(t.c);
    await _n(t.c).addSelected();
    expect(t.bridge.calls, [
      'AAAA000000000001',
      'BBBB000000000002',
      'CCCC000000000003',
    ], reason: 'in list order, none skipped');
    expect(_now(t.c).map((r) => r.status), [
      FoundStatus.added,
      FoundStatus.failed,
      FoundStatus.added,
    ]);
    expect(_row(t.c, 'BBBB000000000002').error, contains('allowlist'));
    expect(_row(t.c, 'BBBB000000000002').unconfirmed, isFalse);
  });

  test('an unconfirmed add (no answer) is a failure marked unconfirmed, so the screen warns it may exist', () async {
    final t = _make(
      cameras: [_three.first],
      outcomes: {'AAAA000000000001': const AddUnconfirmed()},
    );
    await _rows(t.c);
    await _n(t.c).addSelected();
    final r = _now(t.c).single;
    expect((r.status, r.unconfirmed), (FoundStatus.failed, true));
  });

  test('a warning from the bridge is kept on an added row', () async {
    final t = _make(
      cameras: [_three.first],
      outcomes: {
        'AAAA000000000001': const AddAdded('ok', 'no camera matched index 3'),
      },
    );
    await _rows(t.c);
    await _n(t.c).addSelected();
    expect(_now(t.c).single.warning, 'no camera matched index 3');
    expect(_now(t.c).single.status, FoundStatus.added);
  });

  test('the configured devices are re-read once after the last add, not after each one', () async {
    final t = _make();
    await _rows(t.c);
    final before = t.daemon.cameraScans;
    await _n(t.c).addSelected();
    expect(t.daemon.cameraScans - before, 1);
  });

  test('a row that needs a required value stops the sequence at itself, and Add selected resumes after its details', () async {
    final t = _make(
      drivers: [
        zwoCameraDriver(extraFields: [requiredNoDefault]),
      ],
    );
    await _rows(t.c);
    expect(
      _now(t.c).every((r) => r.status == FoundStatus.needsDetails),
      isTrue,
    );
    // Give the first row its detail, leave the second without.
    _n(t.c).setDetails(_now(t.c).first.id, {'usbPath': '/dev/bus/usb/001/002'});
    final before = t.daemon.cameraScans;
    await _n(t.c).addSelected();
    expect(t.bridge.calls, [
      'AAAA000000000001',
    ], reason: 'stops at the second row; the third waits behind it');
    expect(_row(t.c, 'BBBB000000000002').status, FoundStatus.needsDetails);
    expect(
      t.daemon.cameraScans,
      before,
      reason: 'the sequence is not finished, so no re-read yet',
    );
    _n(t.c).setDetails(_row(t.c, 'BBBB000000000002').id, {
      'usbPath': '/dev/bus/usb/001/003',
    });
    _n(t.c).setDetails(_row(t.c, 'CCCC000000000003').id, {
      'usbPath': '/dev/bus/usb/001/004',
    });
    await _n(t.c).addSelected();
    expect(t.bridge.calls, [
      'AAAA000000000001',
      'BBBB000000000002',
      'CCCC000000000003',
    ], reason: 'an added row is not sent twice');
    expect(t.daemon.cameraScans - before, 1);
    expect(t.bridge.bodies[1]['usbPath'], '/dev/bus/usb/001/003');
  });

  test(
    'Retry sends only that row again and an Added result replaces the failure',
    () async {
      final t = _make(outcomes: {'BBBB000000000002': const AddRefused('nope')});
      await _rows(t.c);
      await _n(t.c).addSelected();
      t.bridge.outcomes = const {};
      t.bridge.calls.clear();
      await _n(t.c).retry(_row(t.c, 'BBBB000000000002').id);
      expect(t.bridge.calls, ['BBBB000000000002']);
      final r = _row(t.c, 'BBBB000000000002');
      expect((r.status, r.error), (FoundStatus.added, null));
    },
  );

  test(
    'Edit changes the values of a failed row and the next add sends them',
    () async {
      final t = _make(
        drivers: [
          zwoCameraDriver(extraFields: [requiredNoDefault]),
        ],
        cameras: [_three.first],
        outcomes: {
          'AAAA000000000001': const AddRefused('usbPath is not a camera'),
        },
      );
      await _rows(t.c);
      final id = _now(t.c).single.id;
      _n(t.c).setDetails(id, {'usbPath': '/dev/bad'});
      await _n(t.c).addSelected();
      expect(_now(t.c).single.status, FoundStatus.failed);
      t.bridge.outcomes = const {};
      _n(t.c).setDetails(id, {'usbPath': '/dev/good'});
      expect(
        _now(t.c).single.status,
        FoundStatus.found,
        reason: 'an edited row is ready to send again',
      );
      await _n(t.c).retry(id);
      expect(t.bridge.bodies.last['usbPath'], '/dev/good');
    },
  );

  test('a secret value reaches the add body but never a state dump', () async {
    final t = _make(
      drivers: [
        zwoCameraDriver(
          extraFields: [
            {
              'key': 'password',
              'type': 'string',
              'default': '',
              'required': true,
              'secret': true,
              'role': 'Secret',
            },
          ],
        ),
      ],
      cameras: [_three.first],
    );
    await _rows(t.c);
    final id = _now(t.c).single.id;
    _n(t.c).setDetails(id, const {}, secrets: {'password': 'hunter2'});
    expect(_now(t.c).single.toString(), isNot(contains('hunter2')));
    expect(_now(t.c).toString(), isNot(contains('hunter2')));
    expect(
      _now(t.c).single.values.values.map((v) => '$v'),
      isNot(contains('hunter2')),
    );
    await _n(t.c).addSelected();
    expect(t.bridge.bodies.single['password'], 'hunter2');
    expect(
      _now(t.c).single.toString(),
      isNot(contains('hunter2')),
      reason: 'still out after the add',
    );
  });

  test('Retry detection reads again: an added device is now configured and gone, a failed row is back ticked', () async {
    final t = _make(
      cameras: [_three[0], _three[1]],
      outcomes: {'BBBB000000000002': const AddRefused('nope')},
    );
    await _rows(t.c);
    await _n(t.c).addSelected();
    t.daemon.configured = [
      configuredCamera('ZWO ASI294MC Pro'),
    ]; // what the bridge now offers
    await _n(t.c).redetect();
    final rows = _now(t.c);
    expect(rows.map((r) => r.serial), ['BBBB000000000002']);
    expect(
      (rows.single.status, rows.single.ticked, rows.single.error),
      (FoundStatus.found, true, null),
    );
    expect(t.bridge.cameraReads, 2);
  });

  test('a re-read after the adds never turns a failed row into added, and keeps the added rows listed', () async {
    final t = _make(
      cameras: [_three[0], _three[1]],
      outcomes: {'BBBB000000000002': const AddRefused('nope')},
    );
    await _rows(t.c);
    t.daemon.configured = [
      configuredCamera('ZWO ASI294MC Pro'),
    ]; // visible to the re-read inside addSelected
    await _n(t.c).addSelected();
    expect(_now(t.c).map((r) => (r.serial, r.status)), [
      ('AAAA000000000001', FoundStatus.added),
      ('BBBB000000000002', FoundStatus.failed),
    ]);
  });
}
