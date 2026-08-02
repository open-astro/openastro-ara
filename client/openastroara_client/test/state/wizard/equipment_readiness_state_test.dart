import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_readiness.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/camera_geometry_api.dart';
import 'package:openastroara/services/device_facts_source.dart';
import 'package:openastroara/services/filter_wheel_names_api.dart';
import 'package:openastroara/services/focuser_props_api.dart';
import 'package:openastroara/services/rotator_props_api.dart';
import 'package:openastroara/services/telescope_optics_api.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/wizard/equipment_readiness_state.dart';

const _server = AraServer(hostname: 'pi-test', port: 5555);

DiscoveredDevice _device(EquipmentDeviceType type, String id, String name) =>
    DiscoveredDevice(
      uniqueId: id,
      name: name,
      deviceType: type,
      hostName: 'bridge.lan',
      ipAddress: '10.0.0.9',
      ipPort: 11111,
      alpacaDeviceNumber: 0,
      useHttps: false,
    );

/// Pure fake: devices on the "bridge", per-type read results, optional
/// failure knobs. No sockets, no delays.
class _FakeSource implements DeviceFactsSource {
  final Map<String, DiscoveredDevice> devices;
  final CameraGeometry? camera;
  final TelescopeOptics? optics;
  final MountProps? mount;
  final FilterWheelSlots? wheel;
  FocuserProps? focuser;
  RotatorProps? rotator;
  final bool connectSucceeds;
  final bool resolveThrows;
  int closes = 0;

  _FakeSource({
    this.devices = const {},
    this.camera,
    this.optics,
    this.mount,
    this.wheel,
    this.connectSucceeds = true,
    this.resolveThrows = false,
  });

  @override
  Future<DiscoveredDevice?> resolve(
      EquipmentDeviceType type, String assignedId) async {
    if (resolveThrows) throw Exception('bridge unreachable');
    return devices[assignedId];
  }

  @override
  Future<bool> connect(DiscoveredDevice device) async => connectSucceeds;

  @override
  Future<CameraGeometry?> cameraGeometry() async => camera;
  @override
  Future<TelescopeOptics?> telescopeOptics() async => optics;
  @override
  Future<MountProps?> mountProps() async => mount;
  @override
  Future<FilterWheelSlots?> filterWheelSlots() async => wheel;
  @override
  Future<FocuserProps?> focuserProps() async => focuser;
  @override
  Future<RotatorProps?> rotatorProps() async => rotator;

  @override
  void close() => closes++;
}

ProviderContainer _container(DeviceFactsSource source) {
  final c = ProviderContainer(overrides: [
    deviceFactsSourceFactoryProvider.overrideWithValue((_) => source),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  test('readAll reads every assigned type in parallel and lands per-type '
      'results', () async {
    final source = _FakeSource(
      devices: {
        'cam-1': _device(EquipmentDeviceType.camera, 'cam-1', 'ASI2600MM'),
        'mnt-1': _device(EquipmentDeviceType.mount, 'mnt-1', 'CEM70'),
      },
      camera: const CameraGeometry(
          sensorWidthPx: 6248, sensorHeightPx: 4176, pixelSizeUm: 3.76),
      optics: const TelescopeOptics(focalLengthMm: 530, apertureMm: 106),
      mount: const MountProps(name: 'iOptron CEM70'),
    );
    final c = _container(source);
    final slots = EquipmentSlots()
      ..cameraDeviceId = 'cam-1'
      ..mountDeviceId = 'mnt-1';

    await c
        .read(wizardEquipmentReadinessProvider.notifier)
        .readAll(_server, slots);

    final map = c.read(wizardEquipmentReadinessProvider);
    expect(map.keys,
        unorderedEquals([EquipmentDeviceType.camera, EquipmentDeviceType.mount]));
    expect(map[EquipmentDeviceType.camera]!.state, ReadinessState.ready);
    expect(map[EquipmentDeviceType.camera]!.label, 'ASI2600MM');
    expect(map[EquipmentDeviceType.mount]!.state, ReadinessState.ready);
    expect(map[EquipmentDeviceType.mount]!.label, 'iOptron CEM70');
    // Unassigned types never appear — no phantom cards.
    expect(map.containsKey(EquipmentDeviceType.rotator), isFalse);
    expect(source.closes, 1);
  });

  test('an assigned device missing from the bridge is unreachable with a '
      'blocking gap and no setup link', () async {
    final c = _container(_FakeSource());
    final slots = EquipmentSlots()..cameraDeviceId = 'gone-1';

    await c
        .read(wizardEquipmentReadinessProvider.notifier)
        .readAll(_server, slots);

    final r =
        c.read(wizardEquipmentReadinessProvider)[EquipmentDeviceType.camera]!;
    expect(r.state, ReadinessState.unreachable);
    expect(r.hasBlockingGap, isTrue);
    expect(r.setupUri, isNull);
  });

  test('a connect that never completes is unreachable but keeps the setup '
      'deep link (the fix lives in AlpacaBridge)', () async {
    final c = _container(_FakeSource(
      devices: {
        'foc-1': _device(EquipmentDeviceType.focuser, 'foc-1', 'ZWO EAF'),
      },
      connectSucceeds: false,
    ));
    final slots = EquipmentSlots()..focuserDeviceId = 'foc-1';

    await c
        .read(wizardEquipmentReadinessProvider.notifier)
        .readAll(_server, slots);

    final r =
        c.read(wizardEquipmentReadinessProvider)[EquipmentDeviceType.focuser]!;
    expect(r.state, ReadinessState.unreachable);
    expect(r.label, 'ZWO EAF');
    expect(r.setupUri.toString(),
        'http://10.0.0.9:11111/setup/v1/focuser/0/setup');
  });

  test('a transport failure lands as unreachable, never throws out of '
      'readAll', () async {
    final c = _container(_FakeSource(resolveThrows: true));
    final slots = EquipmentSlots()..rotatorDeviceId = 'rot-1';

    await c
        .read(wizardEquipmentReadinessProvider.notifier)
        .readAll(_server, slots);

    final r =
        c.read(wizardEquipmentReadinessProvider)[EquipmentDeviceType.rotator]!;
    expect(r.state, ReadinessState.unreachable);
    expect(r.gaps.single.hint, contains('Recheck to retry'));
  });

  test('recheck replaces exactly one card and leaves the rest alone',
      () async {
    // First pass: wheel reports nothing → gap.
    final c = _container(_FakeSource(
      devices: {
        'fw-1': _device(EquipmentDeviceType.filterWheel, 'fw-1', 'ZWO EFW'),
        'cam-1': _device(EquipmentDeviceType.camera, 'cam-1', 'ASI2600MM'),
      },
      camera: const CameraGeometry(
          sensorWidthPx: 100, sensorHeightPx: 100, pixelSizeUm: 2.9),
    ));
    final notifier = c.read(wizardEquipmentReadinessProvider.notifier);
    final slots = EquipmentSlots()
      ..cameraDeviceId = 'cam-1'
      ..filterWheelDeviceId = 'fw-1';
    await notifier.readAll(_server, slots);
    expect(
        c
            .read(wizardEquipmentReadinessProvider)[
                EquipmentDeviceType.filterWheel]!
            .state,
        ReadinessState.gaps);

    // User names the filters in AlpacaBridge, hits Recheck on that card.
    // (recheck builds a fresh source through the factory — point it at the
    // fixed bridge.)
    final fixed = _FakeSource(
      devices: {
        'fw-1': _device(EquipmentDeviceType.filterWheel, 'fw-1', 'ZWO EFW'),
      },
      wheel: const FilterWheelSlots([
        FilterWheelSlot(name: 'L', focusOffset: 0),
        FilterWheelSlot(name: 'R', focusOffset: 0),
      ]),
    );
    c.updateOverrides([
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => fixed),
    ]);
    await notifier.recheck(_server, EquipmentDeviceType.filterWheel, 'fw-1');

    final map = c.read(wizardEquipmentReadinessProvider);
    expect(map[EquipmentDeviceType.filterWheel]!.state, ReadinessState.ready);
    expect(map[EquipmentDeviceType.filterWheel]!.facts.first.value, 'L · R');
    // The camera card was not re-read or disturbed.
    expect(map[EquipmentDeviceType.camera]!.state, ReadinessState.ready);
    expect(fixed.closes, 1);
  });

  test('a recheck started DURING an in-flight readAll owns the card — the '
      'readAll landing for that type defers to it', () async {
    // readAll's source parks (would eventually report the device missing);
    // the recheck fired mid-flight answers immediately with a ready wheel.
    final slow = _SlowSource();
    final c = ProviderContainer(overrides: [
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => slow),
    ]);
    addTearDown(c.dispose);
    final notifier = c.read(wizardEquipmentReadinessProvider.notifier);
    final slots = EquipmentSlots()..filterWheelDeviceId = 'fw-1';

    final readAll = notifier.readAll(_server, slots);
    c.updateOverrides([
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => _FakeSource(
            devices: {
              'fw-1':
                  _device(EquipmentDeviceType.filterWheel, 'fw-1', 'ZWO EFW'),
            },
            wheel: const FilterWheelSlots(
                [FilterWheelSlot(name: 'L', focusOffset: 0)]),
          )),
    ]);
    await notifier.recheck(_server, EquipmentDeviceType.filterWheel, 'fw-1');
    expect(
        c
            .read(wizardEquipmentReadinessProvider)[
                EquipmentDeviceType.filterWheel]!
            .state,
        ReadinessState.ready);

    // The parked readAll now finishes with its stale unreachable result —
    // the recheck's newer card must survive.
    slow.release.complete();
    await readAll;
    expect(
        c
            .read(wizardEquipmentReadinessProvider)[
                EquipmentDeviceType.filterWheel]!
            .state,
        ReadinessState.ready);
  });

  test('a double-tapped recheck resolves by START order — the slow first '
      'request cannot overwrite the newer result', () async {
    // First recheck's source parks until released and would report a gap;
    // second recheck's source answers immediately with a ready wheel.
    final slowGap = _SlowSource();
    final c = ProviderContainer(overrides: [
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => slowGap),
    ]);
    addTearDown(c.dispose);
    final notifier = c.read(wizardEquipmentReadinessProvider.notifier);

    final first =
        notifier.recheck(_server, EquipmentDeviceType.filterWheel, 'fw-1');
    c.updateOverrides([
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => _FakeSource(
            devices: {
              'fw-1':
                  _device(EquipmentDeviceType.filterWheel, 'fw-1', 'ZWO EFW'),
            },
            wheel: const FilterWheelSlots(
                [FilterWheelSlot(name: 'L', focusOffset: 0)]),
          )),
    ]);
    await notifier.recheck(_server, EquipmentDeviceType.filterWheel, 'fw-1');
    expect(
        c
            .read(wizardEquipmentReadinessProvider)[
                EquipmentDeviceType.filterWheel]!
            .state,
        ReadinessState.ready);

    // The stale first tap finishes last — its unreachable result must be
    // dropped, not regress the card.
    slowGap.release.complete();
    await first;
    expect(
        c
            .read(wizardEquipmentReadinessProvider)[
                EquipmentDeviceType.filterWheel]!
            .state,
        ReadinessState.ready);
  });

  test('a second readAll supersedes a stale in-flight one (generation guard)',
      () async {
    final slow = _SlowSource();
    final c = ProviderContainer(overrides: [
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => slow),
    ]);
    addTearDown(c.dispose);
    final notifier = c.read(wizardEquipmentReadinessProvider.notifier);
    final slots = EquipmentSlots()..cameraDeviceId = 'cam-1';

    final first = notifier.readAll(_server, slots);
    // Second run starts while the first's resolve is parked.
    c.updateOverrides([
      deviceFactsSourceFactoryProvider.overrideWithValue((_) => _FakeSource(
            devices: {
              'cam-1':
                  _device(EquipmentDeviceType.camera, 'cam-1', 'ASI2600MM'),
            },
            camera: const CameraGeometry(
                sensorWidthPx: 100, sensorHeightPx: 100, pixelSizeUm: 2.9),
          )),
    ]);
    await notifier.readAll(_server, slots);
    expect(
        c.read(wizardEquipmentReadinessProvider)[EquipmentDeviceType.camera]!
            .state,
        ReadinessState.ready);

    // Now let the stale run finish — its unreachable result must NOT clobber
    // the fresh ready card.
    slow.release.complete();
    await first;
    expect(
        c.read(wizardEquipmentReadinessProvider)[EquipmentDeviceType.camera]!
            .state,
        ReadinessState.ready);
  });
}

/// Resolve parks until [release] completes, then reports the device missing.
class _SlowSource implements DeviceFactsSource {
  final release = Completer<void>();

  @override
  Future<DiscoveredDevice?> resolve(
      EquipmentDeviceType type, String assignedId) async {
    await release.future;
    return null;
  }

  @override
  Future<bool> connect(DiscoveredDevice device) async => false;
  @override
  Future<CameraGeometry?> cameraGeometry() async => null;
  @override
  Future<TelescopeOptics?> telescopeOptics() async => null;
  @override
  Future<MountProps?> mountProps() async => null;
  @override
  Future<FilterWheelSlots?> filterWheelSlots() async => null;
  @override
  Future<FocuserProps?> focuserProps() async => null;
  @override
  Future<RotatorProps?> rotatorProps() async => null;
  @override
  void close() {}
}
