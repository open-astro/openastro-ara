import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';

/// A controllable stand-in for the live wheel poller: it only moves when the
/// test says so (the real notifier's async poll cycle would clobber direct
/// state writes).
class _FakeWheelNotifier extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => null;

  void park(FilterWheelStatus status) => state = AsyncData(status);
}

ProviderContainer _container() => ProviderContainer(overrides: [
      filterWheelProvider.overrideWith(_FakeWheelNotifier.new),
    ]);

/// Initializes the wheel provider and lets its async build complete, so a
/// subsequent [park] is never overwritten by the pending build result.
Future<_FakeWheelNotifier> _initWheel(ProviderContainer container) async {
  container.read(filterWheelProvider);
  await pumpEventQueue();
  return container.read(filterWheelProvider.notifier) as _FakeWheelNotifier;
}

Future<void> _settle() async {
  // Let build-time microtasks (initial wheel sync) and listener callbacks run.
  await pumpEventQueue();
}

FilterWheelStatus _wheelAt(int position, {bool connected = true}) =>
    FilterWheelStatus(
      deviceId: 'fw',
      name: 'FILTERWHEEL',
      connectionState: connected
          ? EquipmentConnectionState.connected
          : EquipmentConnectionState.disconnected,
      runtimeState: 'idle',
      currentSlot: position,
      slots: const [
        FilterSlot(position: 0, name: 'L', focusOffset: 0),
        FilterSlot(position: 1, name: 'R', focusOffset: 0),
        FilterSlot(position: 2, name: 'G', focusOffset: 0),
        FilterSlot(position: 3, name: 'B', focusOffset: 0),
      ],
    );

void main() {
  group('ExposureController', () {
    late ProviderContainer container;

    setUp(() => container = _container());
    tearDown(() => container.dispose());

    test('starts with sane defaults', () {
      final params = container.read(exposureControllerProvider);
      expect(params.exposure, const Duration(seconds: 5));
      expect(params.gain, 100);
      expect(params.bin, 1);
      expect(params.filterSlot, 'L');
      expect(params.frameKind, FrameKind.light);
    });

    test('setExposure rejects zero or negative durations', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setExposure(Duration.zero);
      notifier.setExposure(const Duration(seconds: -1));
      expect(container.read(exposureControllerProvider).exposure,
          const Duration(seconds: 5));
    });

    test('setExposure accepts a positive duration', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setExposure(const Duration(seconds: 30));
      expect(container.read(exposureControllerProvider).exposure,
          const Duration(seconds: 30));
    });

    test('setGain + setOffset reject negative', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setGain(-1);
      notifier.setOffset(-100);
      expect(container.read(exposureControllerProvider).gain, 100);
      expect(container.read(exposureControllerProvider).offset, 10);
    });

    test('setBin rejects values below 1', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setBin(0);
      notifier.setBin(-1);
      expect(container.read(exposureControllerProvider).bin, 1);
      notifier.setBin(2);
      expect(container.read(exposureControllerProvider).bin, 2);
    });

    test('setFilterSlot rejects empty string', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setFilterSlot('');
      expect(container.read(exposureControllerProvider).filterSlot, 'L');
      notifier.setFilterSlot('Hα');
      expect(container.read(exposureControllerProvider).filterSlot, 'Hα');
    });

    test('setFrameKind accepts any enum value', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setFrameKind(FrameKind.dark);
      expect(container.read(exposureControllerProvider).frameKind,
          FrameKind.dark);
    });

    test('follows the physical wheel slot into filterSlot', () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);

      // Wheel parks on slot 2 (G) — the picker snaps to its name.
      wheel.park(_wheelAt(2));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'G');
    });

    test('initial sync: wheel already parked when Imaging builds', () async {
      final wheel = await _initWheel(container);
      // Wheel is already on slot 3 (B) before exposureController builds.
      wheel.park(_wheelAt(3));
      container.read(exposureControllerProvider);
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'B');
    });

    test('a manual picker choice is not clobbered until the wheel moves',
        () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);
      final notifier = container.read(exposureControllerProvider.notifier);

      wheel.park(_wheelAt(2));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'G');

      // Manual override (wheel still on slot 2) — survives further polls.
      notifier.setFilterSlot('Ha');
      expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
      wheel.park(_wheelAt(2));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'Ha');

      // The wheel actually moves to slot 3 (B) — the picker follows again.
      wheel.park(_wheelAt(3));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'B');
    });

    test('a disconnected wheel never touches filterSlot', () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);
      wheel.park(_wheelAt(2, connected: false));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'L');
    });

    test('an unnamed current slot leaves filterSlot alone — until a name arrives',
        () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);
      final unnamed = FilterWheelStatus(
        deviceId: 'fw',
        name: 'FILTERWHEEL',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'idle',
        currentSlot: 1,
        slots: const [
          FilterSlot(position: 0, name: 'L', focusOffset: 0),
          FilterSlot(position: 1, name: '', focusOffset: 0),
        ],
      );
      wheel.park(unnamed);
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'L');

      // Same position, same physical slot — but the driver now reports its
      // name. The picker must sync even though the wheel never "moved" (the
      // latch may only engage once a named slot was actually found).
      wheel.park(FilterWheelStatus(
        deviceId: 'fw',
        name: 'FILTERWHEEL',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'idle',
        currentSlot: 1,
        slots: const [
          FilterSlot(position: 0, name: 'L', focusOffset: 0),
          FilterSlot(position: 1, name: 'Ha', focusOffset: 0),
        ],
      ));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'Ha');
    });
    test('a pick made while disconnected re-syncs after reconnect', () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);
      final notifier = container.read(exposureControllerProvider.notifier);

      // Wheel parked on L → picker follows.
      wheel.park(_wheelAt(0));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'L');

      // Wheel goes offline; the user picks Ha while disconnected (tag only,
      // no wheel to move).
      wheel.park(_wheelAt(0, connected: false));
      await _settle();
      notifier.setFilterSlot('Ha');
      expect(container.read(exposureControllerProvider).filterSlot, 'Ha');

      // Wheel reconnects, still physically parked on L — the picker must snap
      // back to the truth (L) instead of keeping the stale offline pick.
      wheel.park(_wheelAt(0));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'L');
    });

    test('a different wheel device resets the latch too', () async {
      container.read(exposureControllerProvider);
      final wheel = await _initWheel(container);

      wheel.park(_wheelAt(0));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'L');

      // A second wheel (different device id), parked at the same position 0
      // with a different slot name — the latch must not carry over.
      wheel.park(FilterWheelStatus(
        deviceId: 'fw2',
        name: 'FILTERWHEEL2',
        connectionState: EquipmentConnectionState.connected,
        runtimeState: 'idle',
        currentSlot: 0,
        slots: const [
          FilterSlot(position: 0, name: 'UV', focusOffset: 0),
        ],
      ));
      await _settle();
      expect(container.read(exposureControllerProvider).filterSlot, 'UV');
    });
  });
}
