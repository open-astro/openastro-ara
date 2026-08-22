import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/equipment_device_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// A mount whose reads the test can fail on demand — the outage this watchdog
/// exists for.
class _FlakyMountApi implements EquipmentDeviceClient<MountStatus> {
  bool failing = false;
  int statusReads = 0;

  @override
  Future<MountStatus?> getStatus() async {
    statusReads++;
    if (failing) throw Exception('rig unreachable');
    return MountStatus(
      deviceId: 'mnt-1',
      name: 'EQ6-R',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      runtimeState: 'idle',
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

void main() {
  group('a prolonged outage stops showing a stale connected device', () {
    late _FlakyMountApi api;
    late StreamController<WsEvent> events;
    late ProviderContainer container;

    setUp(() {
      api = _FlakyMountApi();
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
      container.listen(mountProvider, (_, _) {});
      await container.read(mountProvider.future);
      expect(container.read(mountProvider).value?.connectionState,
          EquipmentConnectionState.connected);
    }

    Future<void> failReads(int n) async {
      api.failing = true;
      for (var i = 0; i < n; i++) {
        await container.read(mountProvider.notifier).refresh();
        await pumpEventQueue();
      }
    }

    test('a blip short of the threshold keeps the live status', () async {
      await prime();
      await failReads(EquipmentDeviceNotifier.staleAfterFailures - 1);

      final state = container.read(mountProvider);
      expect(state.hasError, isFalse,
          reason: 'a couple of lost polls must not flip anything');
      expect(state.value?.connectionState, EquipmentConnectionState.connected);
    });

    test('the threshold flips it to an error instead of stale green', () async {
      await prime();
      await failReads(EquipmentDeviceNotifier.staleAfterFailures);

      expect(container.read(mountProvider).hasError, isTrue);
    });

    test('one good read restores the live status', () async {
      await prime();
      await failReads(EquipmentDeviceNotifier.staleAfterFailures);
      expect(container.read(mountProvider).hasError, isTrue);

      api.failing = false;
      await container.read(mountProvider.notifier).refresh();
      await pumpEventQueue();

      final state = container.read(mountProvider);
      expect(state.hasError, isFalse);
      expect(state.value?.connectionState, EquipmentConnectionState.connected);
    });

    test('the failure count resets, so blips never accumulate into an error',
        () async {
      await prime();
      for (var round = 0; round < 3; round++) {
        await failReads(EquipmentDeviceNotifier.staleAfterFailures - 1);
        api.failing = false;
        await container.read(mountProvider.notifier).refresh();
        await pumpEventQueue();
        expect(container.read(mountProvider).hasError, isFalse,
            reason: 'round $round: a recovered blip must clear the count');
      }
    });
  });
}
