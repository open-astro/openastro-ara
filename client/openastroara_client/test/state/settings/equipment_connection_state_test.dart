import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/state/profile_management_state.dart';

import '../../support/fake_section_rig.dart';

import 'package:openastroara/state/settings/equipment_connection_state.dart';

void main() {
  group('EquipmentConnectionNotifier', () {
    late ProviderContainer container;
    setUp(() async {
      final rig = FakeSectionRig({
        // The class default: every type connects at boot.
        'equipment-connection': {
          for (final k in ['guider', 'dome', 'weather']) k: true,
        },
      });
      container = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig.api())],
      );
      // Self-loading: wait for the rig's copy.
      container.read(equipmentConnectionProvider);
      await settle(() => container.read(equipmentConnectionProvider).canSave);
    });
    tearDown(() => container.dispose());

    test('defaults match playbook §52.1', () {
      final s = container.read(equipmentConnectionProvider).value!;
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

    test('setAutoConnect toggles independently per device type', () async {
      final n = container.read(equipmentConnectionProvider.notifier);
      await n.setAutoConnect(EquipmentDeviceType.dome, false);
      await n.setAutoConnect(EquipmentDeviceType.camera, false);
      final s = container.read(equipmentConnectionProvider).value!;
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
