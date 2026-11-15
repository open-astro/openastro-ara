import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

void main() {
  group('EquipmentConnectionNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §52.1', () {
      final s = container.read(equipmentConnectionProvider);
      // Connect-by-default for core imaging chain.
      expect(s.autoConnect(EquipmentDeviceType.camera), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.mount), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.focuser), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.filterWheel), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.rotator), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.flatPanel), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.safetyMonitor), isTrue);
      // Every type auto-connects by default — boot auto-connect only touches
      // devices the user actually connected before, so all-on is safe.
      expect(s.autoConnect(EquipmentDeviceType.guider), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.dome), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.weather), isTrue);
    });

    test('setAutoConnect toggles independently per device type', () {
      final n = container.read(equipmentConnectionProvider.notifier);
      n.setAutoConnect(EquipmentDeviceType.dome, false);
      n.setAutoConnect(EquipmentDeviceType.camera, false);
      final s = container.read(equipmentConnectionProvider);
      expect(s.autoConnect(EquipmentDeviceType.dome), isFalse);
      expect(s.autoConnect(EquipmentDeviceType.camera), isFalse);
      // Other device types unchanged.
      expect(s.autoConnect(EquipmentDeviceType.mount), isTrue);
      expect(s.autoConnect(EquipmentDeviceType.guider), isTrue);
    });

    test('autoConnect returns true for any unmapped device type', () {
      // Sanity check the map fallback (the defaults cover all 10, so this
      // really verifies the `?? true` guard in case the map is ever
      // partially populated) — unmapped types follow the all-on default.
      const s = EquipmentConnectionSettings(
          autoConnectOnBoot: {EquipmentDeviceType.camera: false});
      expect(s.autoConnect(EquipmentDeviceType.camera), isFalse);
      expect(s.autoConnect(EquipmentDeviceType.mount), isTrue);
    });
  });
}
