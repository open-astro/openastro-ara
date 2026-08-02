import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/wizard/screens/screen_equipment_discovery.dart';
import 'package:openastroara/screens/wizard/screens/screen_your_equipment.dart';
import 'package:openastroara/services/camera_geometry_api.dart';
import 'package:openastroara/services/device_facts_source.dart';
import 'package:openastroara/services/equipment_discovery_api.dart';
import 'package:openastroara/services/filter_wheel_names_api.dart';
import 'package:openastroara/services/focuser_props_api.dart';
import 'package:openastroara/services/rotator_props_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/telescope_optics_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/wizard/equipment_readiness_state.dart';
import 'package:openastroara/state/wizard_state.dart';

const _server = AraServer(hostname: 'h', port: 5555);

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [_server];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

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

/// Discovery fake serving a fixed per-type device list.
class _FakeDiscoveryApi implements EquipmentDiscoveryApi {
  _FakeDiscoveryApi(this.byType);
  final Map<EquipmentDeviceType, List<DiscoveredDevice>> byType;
  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async =>
      byType[type] ?? const [];
  @override
  void close() {}
}

/// Facts fake mirroring the discovery fake's devices.
class _FakeFactsSource implements DeviceFactsSource {
  _FakeFactsSource(this.byType, {this.camera, this.optics, this.mount});
  final Map<EquipmentDeviceType, List<DiscoveredDevice>> byType;
  final CameraGeometry? camera;
  final TelescopeOptics? optics;
  final MountProps? mount;

  @override
  Future<DiscoveredDevice?> resolve(
      EquipmentDeviceType type, String assignedId) async {
    for (final d in byType[type] ?? const <DiscoveredDevice>[]) {
      if (d.uniqueId == assignedId) return d;
    }
    return null;
  }

  @override
  Future<bool> connect(DiscoveredDevice device) async => true;
  @override
  Future<CameraGeometry?> cameraGeometry() async => camera;
  @override
  Future<TelescopeOptics?> telescopeOptics() async => optics;
  @override
  Future<MountProps?> mountProps() async => mount;
  @override
  Future<FilterWheelSlots?> filterWheelSlots() async => null;
  @override
  Future<FocuserProps?> focuserProps() async => null;
  @override
  Future<RotatorProps?> rotatorProps() async => null;
  @override
  void close() {}
}

Future<ProviderContainer> _pump(
  WidgetTester tester, {
  required Map<EquipmentDeviceType, List<DiscoveredDevice>> byType,
  CameraGeometry? camera,
  TelescopeOptics? optics,
  MountProps? mount,
}) async {
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
    equipmentDiscoveryApiFactoryProvider
        .overrideWithValue((_) => _FakeDiscoveryApi(byType)),
    deviceFactsSourceFactoryProvider.overrideWithValue(
        (_) => _FakeFactsSource(byType, camera: camera, optics: optics, mount: mount)),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: ScreenYourEquipment())),
  ));
  // Let the saved-server load, the post-frame prepare, and the parallel reads
  // all land.
  await tester.pumpAndSettle();
  return container;
}

void main() {
  testWidgets(
      'a single discovered device auto-assigns, verifies, and writes its '
      'facts into the draft', (tester) async {
    final byType = {
      EquipmentDeviceType.camera: [
        _device(EquipmentDeviceType.camera, 'cam-1', 'ZWO ASI2600MM Pro'),
      ],
    };
    final container = await _pump(tester,
        byType: byType,
        camera: const CameraGeometry(
            sensorWidthPx: 6248,
            sensorHeightPx: 4176,
            pixelSizeUm: 3.76,
            maxBin: 4));

    // Card shows the REAL device name + read facts, no typing anywhere.
    expect(find.text('ZWO ASI2600MM Pro'), findsOneWidget);
    expect(find.text('Pixel size: 3.76 µm'), findsOneWidget);
    expect(find.text('Sensor: 6248×4176'), findsOneWidget);

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.equipment.cameraDeviceId, 'cam-1');
    expect(draft.camera.pixelSizeMicrons, 3.76);
    expect(draft.equipmentAutoAssigned, isTrue);
  });

  testWidgets('two cameras never auto-assign — ambiguity is a question',
      (tester) async {
    final byType = {
      EquipmentDeviceType.camera: [
        _device(EquipmentDeviceType.camera, 'cam-1', 'Main'),
        _device(EquipmentDeviceType.camera, 'cam-2', 'Guide'),
      ],
    };
    final container = await _pump(tester, byType: byType);

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.equipment.cameraDeviceId, isNull);
    expect(find.text('Camera — none'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Choose'), findsWidgets);
  });

  testWidgets(
      'a mount that reports no optics goes amber with the AlpacaBridge deep '
      'link, Recheck, and the manual fallback field in Details',
      (tester) async {
    final byType = {
      EquipmentDeviceType.mount: [
        _device(EquipmentDeviceType.mount, 'mnt-1', 'iOptron CEM70'),
      ],
    };
    await _pump(tester, byType: byType, mount: const MountProps());

    expect(find.textContaining('Focal length — '), findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Open in AlpacaBridge'),
        findsOneWidget);
    expect(find.widgetWithText(OutlinedButton, 'Recheck'), findsOneWidget);

    // The escape hatch for non-reporting gear lives in the Details
    // disclosure, visually secondary.
    await tester.ensureVisible(find.text('Details').first);
    await tester.tap(find.text('Details').first);
    await tester.pumpAndSettle();
    expect(find.textContaining('Focal length (mm) — manual fallback'),
        findsOneWidget);
  });

  testWidgets('re-entry does not re-assign a slot the user explicitly cleared',
      (tester) async {
    final byType = {
      EquipmentDeviceType.focuser: [
        _device(EquipmentDeviceType.focuser, 'foc-1', 'ZWO EAF'),
      ],
    };
    final container = await _pump(tester, byType: byType);
    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.equipment.focuserDeviceId, 'foc-1');

    // The user clears the slot (simulate the sheet's "— None" outcome), then
    // the screen is rebuilt (Back → forward).
    draft.equipment.focuserDeviceId = null;
    await tester.pumpWidget(const SizedBox());
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ScreenYourEquipment())),
    ));
    await tester.pumpAndSettle();

    expect(draft.equipment.focuserDeviceId, isNull,
        reason: 'auto-assign runs once per wizard session');
  });
}
