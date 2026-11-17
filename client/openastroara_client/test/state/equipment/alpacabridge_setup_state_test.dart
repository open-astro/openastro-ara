import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/guider/guider_choice_state.dart';
import 'package:openastroara/state/equipment/equipment_discovery_provider.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

import '../../support/alpacabridge_setup_fakes.dart';

const _server = AraServer(hostname: 'h', port: 5555);

/// Three attached ZWO cameras with made-up serials (the cameras route is the only detection today).
final _three = [
  camera(0, 'ZWO ASI294MC Pro', 'AAAA000000000001'),
  camera(1, 'ZWO ASI120MM Mini', 'BBBB000000000002'),
  camera(2, 'ZWO ASI533MC Pro', 'CCCC000000000003'),
];

({
  ProviderContainer c,
  FakeBridgeClient bridge,
  FakeConfiguredApi daemon,
  FakeNames names,
})
_make({
  List<AttachedCamera>? cameras,
  List<BridgeDriver>? drivers,
  Map<String, List<AttachedCamera>> otherVendors = const {},
  Map<String, AddOutcome> outcomes = const {},
  bool camerasThrow = false,
  bool gated = false,
  String? addressOverride,
}) {
  final bridge = FakeBridgeClient(
    drivers: drivers ?? [zwoCameraDriver()],
    cameraList: cameras ?? _three,
    otherVendors: otherVendors,
    outcomes: outcomes,
    camerasThrow: camerasThrow,
    gated: gated,
  );
  final daemon = FakeConfiguredApi();
  final names = FakeNames();
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(_server),
      alpacaBridgeSetupClientFactoryProvider.overrideWithValue(bridge.factory),
      alpacaDeviceNamesApiProvider.overrideWithValue(names),
      bridgeAddressOverrideProvider.overrideWithValue(addressOverride),
      equipmentDiscoveryApiFactoryProvider.overrideWithValue((_) => daemon),
    ],
  );
  addTearDown(c.dispose);
  return (c: c, bridge: bridge, daemon: daemon, names: names);
}

/// Lets a started (not awaited) sequence run to its next await.
Future<void> _settle() async {
  for (var i = 0; i < 5; i++) {
    await Future<void>.delayed(Duration.zero);
  }
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

  test('a camera the bridge already offers (same serial) is not a row, whatever name the bridge stored', () async {
    final t = _make();
    t.names.devices = {
      'camera/0': configuredInfo(
        'Renamed in the bridge',
        serial: 'BBBB000000000002',
      ),
    };
    expect((await _rows(t.c)).map((r) => r.serial), [
      'AAAA000000000001',
      'CCCC000000000003',
    ]);
  });

  test('two cameras of one model: only the one whose serial is configured is hidden', () async {
    final t = _make(
      cameras: [
        camera(0, 'ZWO ASI2600MM Pro', 'AAAA000000000001'),
        camera(1, 'ZWO ASI2600MM Pro', 'BBBB000000000002'),
      ],
    );
    t.names.devices = {
      'camera/0': configuredInfo(
        'ZWO ASI2600MM Pro',
        serial: 'AAAA000000000001',
      ),
    };
    expect((await _rows(t.c)).map((r) => r.serial), ['BBBB000000000002']);
  });

  test('the name decides only when either side has no serial', () async {
    final t = _make();
    t.names.devices = {'camera/0': configuredInfo('ZWO ASI120MM Mini')};
    expect((await _rows(t.c)).map((r) => r.serial), [
      'AAAA000000000001',
      'CCCC000000000003',
    ]);
    final u = _make();
    u.names.devices = {
      'camera/0': configuredInfo(
        'ZWO ASI120MM Mini',
        serial: 'ZZZZ000000000009',
      ),
    };
    expect(
      (await _rows(u.c)).length,
      3,
      reason:
          'both sides have a serial and they differ: same name does not hide',
    );
  });

  test('two cameras with no serial are two independent rows', () async {
    final t = _make(
      cameras: [
        camera(0, 'ZWO ASI120MM Mini', ''),
        camera(1, 'ZWO ASI120MM Mini', ''),
      ],
    );
    final rows = await _rows(t.c);
    expect(rows.map((r) => r.id), ['zwo:camera:#0', 'zwo:camera:#1']);
    _n(t.c).toggle('zwo:camera:#0');
    expect(_now(t.c).map((r) => r.ticked), [false, true]);
  });

  test(
    'cameras are asked for every vendor with a camera driver, and only those',
    () async {
      final t = _make(
        drivers: [zwoCameraDriver(), qhyCameraDriver(), zwoFocuserDriver()],
        cameras: [_three.first],
        otherVendors: {
          'qhy': [camera(0, 'QHY268M', 'QQQQ000000000009')],
        },
      );
      final rows = await _rows(t.c);
      expect(t.bridge.vendorsAsked.toSet(), {'zwo', 'qhy'});
      expect(rows.map((r) => (r.vendor, r.serial)), [
        ('zwo', 'AAAA000000000001'),
        ('qhy', 'QQQQ000000000009'),
      ]);
    },
  );

  test('while the cameras are being read the provider is loading', () async {
    final t = _make();
    expect(t.c.read(foundDevicesProvider).isLoading, isTrue);
    await _rows(t.c);
    expect(t.c.read(foundDevicesProvider).isLoading, isFalse);
  });

  group('the bridge address (the daemon dials it)', () {
    test('an empty rig with no override is localhost:6800', () async {
      final t = _make();
      await _rows(t.c);
      expect(
        t.bridge.addresses,
        everyElement(const BridgeAddress('localhost', 6800)),
      );
      expect(t.names.reads, [const BridgeAddress('localhost', 6800)]);
    });

    test(
      'with no override the host and port of a discovered device are used',
      () async {
        final t = _make();
        t.daemon.configured = [configuredCamera('Some camera')];
        await _rows(t.c);
        expect(
          t.bridge.addresses,
          everyElement(const BridgeAddress('192.0.2.10', 32323)),
        );
        expect(t.bridge.addresses, isNotEmpty);
        expect(t.names.reads, [const BridgeAddress('192.0.2.10', 32323)]);
      },
    );

    test('the draft override is host:port, and a host alone gets 6800; it beats a discovered device', () async {
      final t = _make(addressOverride: 'bridge.example:7000');
      t.daemon.configured = [configuredCamera('Some camera')];
      await _rows(t.c);
      expect(
        t.bridge.addresses,
        everyElement(const BridgeAddress('bridge.example', 7000)),
      );
      expect(t.bridge.addresses, isNotEmpty);
      expect(t.names.reads, [const BridgeAddress('bridge.example', 7000)]);
      final u = _make(addressOverride: ' bridge.example ');
      await _rows(u.c);
      expect(
        u.bridge.addresses,
        everyElement(const BridgeAddress('bridge.example', 6800)),
      );
      expect(u.bridge.addresses, isNotEmpty);
      expect(u.names.reads, [const BridgeAddress('bridge.example', 6800)]);
    });

    test('a bad port in the override is an error state and no call', () async {
      for (final bad in [
        'bridge.example:nope',
        'bridge.example:0',
        'bridge.example:70000',
      ]) {
        final t = _make(addressOverride: bad);
        await expectLater(
          _rows(t.c),
          throwsA(isA<FormatException>()),
          reason: bad,
        );
        expect(t.bridge.addresses, isEmpty, reason: bad);
        expect(t.bridge.cameraReads, 0, reason: bad);
        expect(t.names.reads, isEmpty, reason: bad);
      }
    });
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
    expect(
      _row(t.c, 'BBBB000000000002').ticked,
      isFalse,
      reason: 'a failed row is greyed: only Retry and Edit send it again',
    );
    expect(selectedCount(_now(t.c)), 0);
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
    final scansBefore = t.daemon.scans.length;
    await _n(t.c).addSelected();
    expect(t.daemon.cameraScans - before, 1);
    final reread = t.daemon.scans.skip(scansBefore).toList();
    expect(
      reread.map((s) => s.$2),
      everyElement(isTrue),
      reason: 'a cached answer from before the adds is stale, for every type',
    );
    expect(reread.map((s) => s.$1).toSet(), {
      EquipmentDeviceType.camera,
      EquipmentDeviceType.mount,
      EquipmentDeviceType.focuser,
      EquipmentDeviceType.filterWheel,
      EquipmentDeviceType.rotator,
      EquipmentDeviceType.flatPanel,
      EquipmentDeviceType.dome,
      EquipmentDeviceType.weather,
      EquipmentDeviceType.safetyMonitor,
      EquipmentDeviceType.switchDevice,
    }, reason: 'the types the Connect screen probes (_summaryTypes)');
    expect(reread.length, 10, reason: 'one scan per type, once');
  });

  test('Add selected does not start while a ticked row still needs details; then one sequence and one re-read', () async {
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
    _n(t.c).setDetails('zwo:camera:AAAA000000000001', {
      'usbPath': '/dev/bus/usb/001/002',
    });
    _n(t.c).setDetails('zwo:camera:CCCC000000000003', {
      'usbPath': '/dev/bus/usb/001/004',
    });
    final before = t.daemon.cameraScans;
    await _n(t.c).addSelected();
    expect(
      t.bridge.calls,
      isEmpty,
      reason: 'the second row still needs details: nothing is sent',
    );
    expect(
      t.daemon.cameraScans,
      before,
      reason: 'no sequence ran, so no re-read',
    );
    expect(_now(t.c).map((r) => r.status), [
      FoundStatus.found,
      FoundStatus.needsDetails,
      FoundStatus.found,
    ]);
    _n(t.c).setDetails('zwo:camera:BBBB000000000002', {
      'usbPath': '/dev/bus/usb/001/003',
    });
    await _n(t.c).addSelected();
    expect(t.bridge.calls, [
      'AAAA000000000001',
      'BBBB000000000002',
      'CCCC000000000003',
    ], reason: 'one uninterrupted sequence, in list order');
    expect(t.daemon.cameraScans - before, 1);
    expect(t.bridge.bodies[1]['usbPath'], '/dev/bus/usb/001/003');
  });

  test(
    'unticking the row that needs details lets Add selected send the others',
    () async {
      final t = _make(
        drivers: [
          zwoCameraDriver(extraFields: [requiredNoDefault]),
        ],
        cameras: [_three[0], _three[1]],
      );
      await _rows(t.c);
      _n(t.c).setDetails('zwo:camera:AAAA000000000001', {
        'usbPath': '/dev/bus/usb/001/002',
      });
      _n(t.c).toggle('zwo:camera:BBBB000000000002');
      await _n(t.c).addSelected();
      expect(t.bridge.calls, ['AAAA000000000001']);
    },
  );

  test(
    'a failed row is not sent again by Add selected, even when unconfirmed',
    () async {
      final t = _make(
        cameras: [_three[0], _three[1]],
        outcomes: {'AAAA000000000001': const AddUnconfirmed()},
      );
      await _rows(t.c);
      await _n(t.c).addSelected();
      expect(t.bridge.calls, ['AAAA000000000001', 'BBBB000000000002']);
      _n(t.c).toggle('zwo:camera:AAAA000000000001');
      expect(
        _row(t.c, 'AAAA000000000001').ticked,
        isFalse,
        reason: 'toggle does nothing on a failed row',
      );
      await _n(t.c).addSelected();
      expect(t.bridge.calls.where((c) => c == 'AAAA000000000001').length, 1);
    },
  );

  test('toggle does nothing on an added row', () async {
    final t = _make(cameras: [_three.first]);
    await _rows(t.c);
    await _n(t.c).addSelected();
    _n(t.c).toggle(_now(t.c).single.id);
    expect(_now(t.c).single.ticked, isTrue);
    expect(_now(t.c).single.status, FoundStatus.added);
  });

  test('a second Add selected, Retry detection and toggle do nothing while a sequence runs; adds never overlap', () async {
    final t = _make(gated: true);
    await _rows(t.c);
    // Back to back, no await between: the rows are `waiting` before the first await.
    final first = _n(t.c).addSelected();
    expect(isRunning(_now(t.c)), isTrue);
    final second = _n(t.c).addSelected();
    await _settle();
    expect(t.bridge.calls, ['AAAA000000000001']);
    expect(_now(t.c).map((r) => r.status), [
      FoundStatus.adding,
      FoundStatus.waiting,
      FoundStatus.waiting,
    ]);
    expect(isRunning(_now(t.c)), isTrue);
    _n(t.c).toggle('zwo:camera:CCCC000000000003');
    final redo = _n(t.c).redetect();
    await _settle();
    expect(t.bridge.calls, [
      'AAAA000000000001',
    ], reason: 'the second call did not start a second sequence');
    expect(
      _row(t.c, 'CCCC000000000003').ticked,
      isTrue,
      reason: 'a waiting row cannot be unticked',
    );
    expect(
      t.bridge.cameraReads,
      1,
      reason: 'Retry detection waits: it did not read again',
    );
    for (var i = 0; i < 3; i++) {
      t.bridge.release();
      await _settle();
    }
    await Future.wait([first, second, redo]);
    expect(t.bridge.calls, [
      'AAAA000000000001',
      'BBBB000000000002',
      'CCCC000000000003',
    ], reason: 'each row sent once');
    expect(
      t.bridge.maxInFlight,
      1,
      reason: 'one add at a time: an add can open a vendor SDK',
    );
    expect(_now(t.c).map((r) => r.status), everyElement(FoundStatus.added));
  });

  test(
    'Retry sends only that row again and an Added result replaces the failure',
    () async {
      final t = _make(outcomes: {'BBBB000000000002': const AddRefused('nope')});
      await _rows(t.c);
      await _n(t.c).addSelected();
      t.bridge.outcomes = const {};
      t.bridge.calls.clear();
      final scansBeforeRetry = t.daemon.cameraScans;
      await _n(t.c).retry(_row(t.c, 'BBBB000000000002').id);
      expect(t.bridge.calls, ['BBBB000000000002']);
      final r = _row(t.c, 'BBBB000000000002');
      expect((r.status, r.error), (FoundStatus.added, null));
      expect(
        t.daemon.cameraScans - scansBeforeRetry,
        1,
        reason: 'the one re-read of this Retry',
      );
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
      expect(_now(t.c).single.ticked, isTrue);
      await _n(t.c).retry(id);
      expect(t.bridge.bodies.last['usbPath'], '/dev/good');
    },
  );

  test('an unconfirmed row whose device did land becomes Added on Retry with no second POST', () async {
    final t = _make(
      cameras: [_three.first],
      outcomes: {'AAAA000000000001': const AddUnconfirmed()},
    );
    await _rows(t.c);
    await _n(t.c).addSelected();
    expect(_now(t.c).single.unconfirmed, isTrue);
    t.names.devices = {
      'camera/0': configuredInfo(
        'Whatever the bridge calls it',
        serial: 'AAAA000000000001',
      ),
    };
    final scans = t.daemon.cameraScans;
    await _n(t.c).retry(_now(t.c).single.id);
    expect(t.bridge.calls, [
      'AAAA000000000001',
    ], reason: 'no second POST: no duplicate');
    expect(_now(t.c).single.status, FoundStatus.added);
    expect(t.daemon.cameraScans - scans, 1);
  });

  test(
    'an unconfirmed row whose device did not land is sent again on Retry',
    () async {
      final t = _make(
        cameras: [_three.first],
        outcomes: {'AAAA000000000001': const AddUnconfirmed()},
      );
      await _rows(t.c);
      await _n(t.c).addSelected();
      t.bridge.outcomes = const {};
      await _n(t.c).retry(_now(t.c).single.id);
      expect(t.bridge.calls, ['AAAA000000000001', 'AAAA000000000001']);
      expect(_now(t.c).single.status, FoundStatus.added);
    },
  );

  group('secrets', () {
    final driver = zwoCameraDriver(
      extraFields: [
        requiredNoDefault,
        {
          'key': 'password',
          'type': 'string',
          'default': '',
          'required': true,
          'secret': true,
          'role': 'Secret',
        },
      ],
    );

    test(
      'a secret value reaches the add body but never a state dump',
      () async {
        final t = _make(drivers: [driver], cameras: [_three.first]);
        await _rows(t.c);
        final id = _now(t.c).single.id;
        _n(t.c).setDetails(
          id,
          {'usbPath': '/dev/bus/usb/001/002'},
          secrets: {'password': 'hunter2'},
        );
        final dump = _now(t.c).single.toString();
        expect(
          dump,
          contains('/dev/bus/usb/001/002'),
          reason: 'the dump does print values',
        );
        expect(dump, isNot(contains('hunter2')));
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
      },
    );

    test('Retry detection drops the secret: the row needs it again and the dump stays clean', () async {
      final t = _make(
        drivers: [driver],
        cameras: [_three.first],
        outcomes: {'AAAA000000000001': const AddRefused('nope')},
      );
      await _rows(t.c);
      final id = _now(t.c).single.id;
      _n(t.c).setDetails(
        id,
        {'usbPath': '/dev/bus/usb/001/002'},
        secrets: {'password': 'hunter2'},
      );
      await _n(t.c).addSelected();
      expect(_now(t.c).single.status, FoundStatus.failed);
      await _n(t.c).redetect();
      expect(
        _now(t.c).single.status,
        FoundStatus.needsDetails,
        reason:
            'the required secret is gone, so the row asks for details again',
      );
      expect(_now(t.c).toString(), isNot(contains('hunter2')));
      expect(t.bridge.calls.length, 1);
      await _n(t.c).addSelected();
      expect(
        t.bridge.calls.length,
        1,
        reason: 'a row that needs details is not sent',
      );
    });
  });

  test('Retry detection reads again: an added device is configured and gone, a failed row is back ticked and found', () async {
    final t = _make(
      cameras: [_three[0], _three[1], _three[2]],
      outcomes: {'BBBB000000000002': const AddRefused('nope')},
    );
    await _rows(t.c);
    _n(t.c).toggle('zwo:camera:CCCC000000000003');
    await _n(t.c).addSelected();
    t.names.devices = {
      'camera/0': configuredInfo(
        'ZWO ASI294MC Pro',
        serial: 'AAAA000000000001',
      ),
    }; // what the bridge now offers
    await _n(t.c).redetect();
    final rows = _now(t.c);
    expect(rows.map((r) => r.serial), ['BBBB000000000002', 'CCCC000000000003']);
    expect(
      rows.map((r) => (r.status, r.ticked, r.error)),
      everyElement((FoundStatus.found, true, null)),
      reason: 'ticks reset, errors cleared, failed back to found',
    );
    expect(t.bridge.cameraReads, 2);
  });

  test('a re-read after the adds never turns a failed row into added, and keeps the added rows listed', () async {
    final t = _make(
      cameras: [_three[0], _three[1]],
      outcomes: {'BBBB000000000002': const AddUnconfirmed()},
    );
    await _rows(t.c);
    t.names.devices = {
      'camera/0': configuredInfo(
        'ZWO ASI294MC Pro',
        serial: 'AAAA000000000001',
      ),
      'camera/1': configuredInfo(
        'ZWO ASI120MM Mini',
        serial: 'BBBB000000000002',
      ),
    }; // both are configured by the time of the re-read
    t.daemon.configured = [
      configuredCamera('ZWO ASI294MC Pro'),
      configuredCamera('ZWO ASI120MM Mini'),
    ]; // set after detection, so the address stays localhost:6800; the re-read lists both
    await _n(t.c).addSelected();
    expect(_now(t.c).map((r) => (r.serial, r.status)), [
      ('AAAA000000000001', FoundStatus.added),
      ('BBBB000000000002', FoundStatus.failed),
    ]);
  });
}
