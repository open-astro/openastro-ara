import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/profile_live_sync.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for the sections that load before they save: one list drives the
// reset and the live-sync table, the offline cache seeds planning only, and a
// refused save writes nothing.

/// Every migrated section by the path the rig serves it under.
const _paths = {
  'camera-electronics': ['camera-electronics'],
  'cooling-fan': ['cooling-fan'],
  'custom-horizon': ['custom-horizon'],
  'diagnostics-mode': ['diagnostics-mode'],
  'dome': ['dome'],
  'equipment-connection': ['equipment-connection'],
  'filenames': ['filenames'],
  'filter-set': ['filter-set'],
  'filter-wheel': ['filter-wheel/labels', 'filter-wheel/policy'],
  'notifications': ['notifications'],
  'plate-solve': ['plate-solve'],
  'storage': ['storage'],
  'unguided': ['unguided'],
};

const _storage = {
  'save_directory': '/data',
  'file_format': 'fits',
  'compression': 'off',
  'filename_template': '{target}',
  'min_free_disk_warn_gb': 10,
  'min_free_disk_critical_gb': 5,
  'backup_retention_count': 3,
  'fault_log_retention_days': 30,
};

FakeSectionRig _rig() => FakeSectionRig({
  for (final p in _paths.values.expand((e) => e)) p: <String, Object?>{},
  'custom-horizon': {'points': []},
  'filter-set': {'filters': []},
  'filter-wheel/labels': {
    'labels': ['L'],
  },
  'storage': _storage,
});

/// A self-loading section has tried and failed; a panel section waits for its
/// panel or live sync and never tries on its own.
bool _failedOrWaiting(ProviderContainer c, MigratedSection s) =>
    c.read(s.provider).lastFailure != null ||
    c.read(s.provider.notifier) is! SelfLoadingSection;

class _Cache extends ProfileCacheService {
  _Cache(this.sections);
  final Map<String, Object?> sections;
  @override
  Future<Map<String, dynamic>> load() async => {
    'sections': {'p1': sections},
  };
}

void main() {
  test('Migrated sections: every live-sync route loads exactly its own '
      'sections', () async {
    expect({for (final s in migratedSections) s.route}, _paths.keys.toSet());
    final rig = _rig();
    // Every section's read fails, so the self-loading ones stay not loaded.
    final all = _paths.values.expand((e) => e).toSet();
    rig.failGet.addAll(all);
    final c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(rig.api())],
    );
    addTearDown(c.dispose);
    for (final s in migratedSections) {
      c.listen(s.provider, (_, _) {});
    }
    await settle(() => migratedSections.every((s) => _failedOrWaiting(c, s)));

    for (final route in _paths.keys) {
      rig.failGet.removeAll(_paths[route]!);
      await applyProfileUpdated(c, route);
      for (final s in migratedSections) {
        expect(
          c.read(s.provider).canSave,
          s.route == route,
          reason: '${s.route} after profile.updated for $route',
        );
      }
      rig.failGet.addAll(_paths[route]!);
      resetMigratedSections(c.read);
      await settle(() => migratedSections.every((s) => _failedOrWaiting(c, s)));
    }
  });

  test('Migrated sections: the reset sends every one back to not loaded, '
      'saved or not', () async {
    final rig = _rig();
    final c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(rig.api())],
    );
    addTearDown(c.dispose);
    for (final s in migratedSections) {
      c.listen(s.provider, (_, _) {});
    }
    await settle(
      () => migratedSections.every((s) => c.read(s.provider).canSave),
    );
    rig.failGet.addAll(_paths.values.expand((e) => e));
    resetMigratedSections(c.read);
    await settle(() => migratedSections.every((s) => _failedOrWaiting(c, s)));
    for (final s in migratedSections) {
      expect(c.read(s.provider).canSave, isFalse, reason: s.route);
      expect(c.read(s.provider).value, isNull, reason: s.route);
    }
  });

  test('Camera electronics seeded from the offline cache: planning has a '
      'value, and nothing can edit or save it', () async {
    final rig = _rig()..failGet.add('camera-electronics');
    final c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(rig.api()),
        profileManagementProvider.overrideWith(_Profiles.new),
        profileCacheServiceProvider.overrideWithValue(
          _Cache({
            'camera_electronics': {'sensor_name': 'IMX-test', 'gain': 120},
          }),
        ),
      ],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    final n = c.read(cameraElectronicsProvider.notifier);
    expect(c.read(cameraElectronicsProvider).source, SectionSource.cache);
    expect(c.read(cameraElectronicsProvider).value?.sensorName, 'IMX-test');
    n.setSensorName('edited');
    expect(c.read(cameraElectronicsProvider).value?.sensorName, 'IMX-test');
    await expectLater(
      n.save(rig.api()),
      throwsA(isA<SectionNotLoadedException>()),
    );
    expect(rig.puts, isEmpty);
  });

  test('A cache snapshot with no camera_electronics key leaves it not '
      'loaded', () async {
    final c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(null),
        profileCacheServiceProvider.overrideWithValue(
          _Cache({
            'filter_set': {'filters': []},
          }),
        ),
      ],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(cameraElectronicsProvider).source, SectionSource.notLoaded);
    expect(c.read(cameraElectronicsProvider).value, isNull);
  });

  test(
    'Cooling fan: choose throws when not loaded and writes nothing',
    () async {
      final rig = _rig()..failGet.add('cooling-fan');
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig.api())],
      );
      addTearDown(c.dispose);
      c.listen(coolingFanProvider, (_, _) {});
      await settle(() => c.read(coolingFanProvider).lastFailure != null);
      await expectLater(
        c
            .read(coolingFanProvider.notifier)
            .choose(const CoolingFan(deviceId: 'sw-0', portId: 1)),
        throwsA(isA<SectionNotLoadedException>()),
      );
      expect(rig.puts, isEmpty);
      expect(c.read(coolingFanProvider).value, isNull);
    },
  );
}

class _Profiles extends ProfileManagementNotifier {
  @override
  Future<ProfileList> build() async =>
      const ProfileList(activeId: 'p1', profiles: []);
}
