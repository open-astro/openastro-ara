import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// §60.9 client half — an `equipment.state_changed` WS event for THIS device
/// type triggers an immediate provider refresh (push replaces waiting out the
/// next poll tick); other types, alias events, and unknown tokens do not.
class _FakeMountApi implements EquipmentDeviceClient<MountStatus> {
  int statusReads = 0;

  /// When set, the FIRST read blocks on this — lets a test hold build()'s
  /// initial read in flight while a WS-push refresh overtakes it.
  Completer<void>? gateFirstRead;

  @override
  Future<MountStatus?> getStatus() async {
    final call = ++statusReads;
    if (call == 1 && gateFirstRead != null) {
      await gateFirstRead!.future;
    }
    return MountStatus(
      deviceId: 'mnt-1',
      name: 'EQ6-R',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      // Tags which read produced the visible status, so the race test can
      // assert the fresher read's result survived.
      runtimeState: 'read-$call',
      rightAscensionHours: null,
      declinationDegrees: null,
      tracking: true,
      parked: false,
      atHome: false,
    );
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {}

  @override
  Future<void> disconnect() async {}

  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {}

  @override
  void close() {}
}

WsEvent _stateChanged(
  String deviceType, {
  String type = 'equipment.state_changed',
}) => WsEvent(
  type: type,
  ts: DateTime.utc(2026, 7, 6),
  seq: 1,
  payload: {'device_type': deviceType, 'device_id': 'x', 'state': 'connected'},
);

void main() {
  group('equipment WS refresh (§60.9)', () {
    late _FakeMountApi api;
    late StreamController<WsEvent> events;
    late ProviderContainer container;

    setUp(() {
      api = _FakeMountApi();
      events = StreamController<WsEvent>.broadcast();
      container = ProviderContainer(
        overrides: [
          mountApiProvider.overrideWithValue(api),
          wsEventsProvider.overrideWith((ref) => events.stream),
        ],
      );
      addTearDown(() async {
        container.dispose();
        await events.close();
      });
    });

    Future<void> prime() async {
      // Keep the autoDispose family alive and complete the initial read.
      container.listen(mountProvider, (_, _) {});
      await container.read(mountProvider.future);
      expect(api.statusReads, 1, reason: 'the build-time read');
    }

    test(
      'a matching state_changed event triggers an immediate re-read',
      () async {
        await prime();

        events.add(_stateChanged('telescope'));
        await pumpEventQueue();

        expect(api.statusReads, 2, reason: 'push refresh, no poll wait');
      },
    );

    test('another device type does not refresh this provider', () async {
      await prime();

      events.add(_stateChanged('camera'));
      events.add(_stateChanged('filterwheel'));
      await pumpEventQueue();

      expect(api.statusReads, 1);
    });

    test(
      'alias events are ignored (state_changed already covered the transition)',
      () async {
        await prime();

        events.add(_stateChanged('telescope', type: 'equipment.connected'));
        events.add(_stateChanged('telescope', type: 'equipment.disconnected'));
        await pumpEventQueue();

        expect(api.statusReads, 1);
      },
    );

    test('a WS refresh landing during build()s in-flight read is not clobbered '
        'by the staler build result', () async {
      // Hold build()'s initial read open...
      api.gateFirstRead = Completer<void>();
      container.listen(mountProvider, (_, _) {});
      await pumpEventQueue();
      expect(api.statusReads, 1, reason: 'build read issued and held');

      // ...while a state transition arrives and its push refresh completes.
      events.add(_stateChanged('telescope'));
      await pumpEventQueue();
      expect(
        api.statusReads,
        2,
        reason: 'the WS refresh read ran to completion',
      );

      // The stale build read then resolves — it must NOT overwrite the fresher
      // refresh result (latest-issued read wins).
      api.gateFirstRead!.complete();
      final settled = await container.read(mountProvider.future);
      expect(
        settled!.runtimeState,
        'read-2',
        reason: 'the pre-transition build read would silently revert the panel',
      );
    });

    test(
      'an unknown device_type token from a newer daemon is ignored',
      () async {
        await prime();

        events.add(_stateChanged('hyperdrive'));
        await pumpEventQueue();

        expect(api.statusReads, 1);
      },
    );
  });

  group('DiscoveredDevice.tryParseDeviceType', () {
    test(
      'maps every wire token including the equipment-event flatdevice form',
      () {
        expect(
          DiscoveredDevice.tryParseDeviceType('telescope'),
          EquipmentDeviceType.mount,
        );
        expect(
          DiscoveredDevice.tryParseDeviceType('filterwheel'),
          EquipmentDeviceType.filterWheel,
        );
        expect(
          DiscoveredDevice.tryParseDeviceType('safetymonitor'),
          EquipmentDeviceType.safetyMonitor,
        );
        expect(
          DiscoveredDevice.tryParseDeviceType('flatdevice'),
          EquipmentDeviceType.flatPanel,
        );
        expect(
          DiscoveredDevice.tryParseDeviceType('observingconditions'),
          EquipmentDeviceType.weather,
        );
        expect(
          DiscoveredDevice.tryParseDeviceType('switch'),
          EquipmentDeviceType.switchDevice,
        );
      },
    );

    test('unknown or null tokens return null instead of asserting', () {
      expect(DiscoveredDevice.tryParseDeviceType('hyperdrive'), isNull);
      expect(DiscoveredDevice.tryParseDeviceType(null), isNull);
      expect(DiscoveredDevice.tryParseDeviceType(''), isNull);
    });
  });
}
