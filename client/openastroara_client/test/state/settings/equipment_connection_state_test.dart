import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';

import '../../support/fake_section_rig.dart';

import 'package:openastroara/state/settings/equipment_connection_state.dart';
import '../../support/settings_values.dart';

void main() {
  group('EquipmentConnectionNotifier', () {
    late ProviderContainer container;
    late FakeSectionRig rig;
    setUp(() async {
      rig = FakeSectionRig({
        // The class default: every type connects at boot.
        'equipment-connection': {
          for (final k in rigDefaults['equipment-connection']!.keys) k: true,
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

    test(
      'setAutoConnect writes the whole section against the loaded tag',
      () async {
        final tag = rig.etagOf('equipment-connection');
        await container
            .read(equipmentConnectionProvider.notifier)
            .setAutoConnect(EquipmentDeviceType.filterWheel, false);
        expect(rig.ifMatches, [tag]);
        expect(rig.puts.single.body, {
          ...rigDefaults['equipment-connection']!.map(
            (k, _) => MapEntry(k, true),
          ),
          'filter_wheel': false,
        });
      },
    );

    test(
      'setAutoConnect over a copy another device saved is refused',
      () async {
        rig.savedElsewhere('equipment-connection');
        await expectLater(
          container
              .read(equipmentConnectionProvider.notifier)
              .setAutoConnect(EquipmentDeviceType.camera, false),
          throwsA(isA<SectionConflictException>()),
        );
        expect(rig.puts, isEmpty);
        expect(container.read(equipmentConnectionProvider).conflict, isTrue);
      },
    );

    test('autoConnect returns true for any unmapped device type', () {
      // Sanity check the map fallback (the defaults cover all 10, so this
      // really verifies the `?? true` guard in case the map is ever
      // partially populated) — unmapped types follow the all-on default.
      const s = TestEquipmentConnectionSettings(
        autoConnectOnBoot: {EquipmentDeviceType.camera: false},
      );
      expect(s.autoConnect(EquipmentDeviceType.camera), isFalse);
      expect(s.autoConnect(EquipmentDeviceType.mount), isTrue);
    });
  });

  group('EquipmentConnectionNotifier, not loaded', () {
    test('no rig: nothing is loaded and setAutoConnect throws', () async {
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(null)],
      );
      addTearDown(c.dispose);
      expect(c.read(equipmentConnectionProvider).value, isNull);
      await expectLater(
        c
            .read(equipmentConnectionProvider.notifier)
            .setAutoConnect(EquipmentDeviceType.camera, false),
        throwsA(isA<SectionNotLoadedException>()),
      );
    });

    test(
      'a failed load: setAutoConnect throws and nothing is written',
      () async {
        final rig = FakeSectionRig({
          'equipment-connection': rigDefaults['equipment-connection'],
        })..failGet.add('equipment-connection');
        final c = ProviderContainer(
          overrides: [profileApiProvider.overrideWithValue(rig.api())],
        );
        addTearDown(c.dispose);
        c.listen(equipmentConnectionProvider, (_, _) {});
        await Future<void>.delayed(const Duration(milliseconds: 100));
        expect(c.read(equipmentConnectionProvider).canSave, isFalse);
        await expectLater(
          c
              .read(equipmentConnectionProvider.notifier)
              .setAutoConnect(EquipmentDeviceType.camera, false),
          throwsA(isA<SectionNotLoadedException>()),
        );
        expect(rig.puts, isEmpty);
      },
    );
  });
}
