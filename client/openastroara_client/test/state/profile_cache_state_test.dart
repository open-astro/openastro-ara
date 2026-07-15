import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

/// ProfileApi double serving fixed section payloads for the capture path.
class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));

  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings(
      focalLengthMm: 382,
      reducerFactor: 1.0,
      sensorWidthPx: 6248,
      sensorHeightPx: 4176,
      pixelSizeUm: 3.76,
      apertureMm: 51);

  @override
  Future<ImagingDefaults> getImagingDefaults() async =>
      const ImagingDefaults(defaultGain: 101);

  @override
  Future<AutofocusSettings> getAutofocusSettings() async =>
      const AutofocusSettings(steps: 9);

  @override
  Future<SiteSettings> getSiteSettings() async =>
      const SiteSettings(siteName: 'South GA', bortleClass: 4);

  @override
  Future<FilterSetSettings> getFilterSet() async => const FilterSetSettings(
      filters: [
        PlanningFilter(name: 'Ha', kind: FilterKind.ha, bandwidthNm: 7)
      ]);

  @override
  Future<CameraElectronics> getCameraElectronics() async =>
      const CameraElectronics(
          sensorName: 'IMX571',
          readNoiseE: 1.5,
          fullWellE: 51000,
          quantumEfficiencyPeak: 0.8);
}

void main() {
  late Directory tmp;
  late ProfileCacheService cache;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('profile_cache_test');
    cache = ProfileCacheService(supportDir: () async => tmp);
  });

  tearDown(() async {
    await tmp.delete(recursive: true);
  });

  const list = ProfileList(activeId: 'p1', profiles: [
    ProfileMeta(id: 'p1', name: 'RedCat 91'),
    ProfileMeta(id: 'p2', name: 'Travel Rig'),
  ]);

  test('captureProfileCache stores the list and the active profile\'s sections',
      () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final stored = await cache.load();
    expect(stored['active_id'], 'p1');
    expect((stored['profiles'] as List), hasLength(2));
    final sections = stored['sections'] as Map;
    expect(sections.keys, ['p1']); // only the active profile is snapshotted
    expect((sections['p1'] as Map)['optics'],
        containsPair('focal_length_mm', 382));
  });

  test('cachedProfilesProvider reflects capture, including hasSections',
      () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final c = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(c.dispose);
    final cached = await c.read(cachedProfilesProvider.future);
    expect(cached.activeId, 'p1');
    expect(cached.profiles.map((p) => (p.id, p.hasSections)),
        [('p1', true), ('p2', false)]);
  });

  test('seedPlanningFromCache hydrates the planning notifiers', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final c = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(c.dispose);
    final seeded = await seedPlanningFromCache(c, 'p1');
    expect(seeded, isTrue);
    expect(c.read(opticsSettingsProvider).focalLengthMm, 382);
    expect(c.read(imagingDefaultsProvider).defaultGain, 101);
    expect(c.read(siteSettingsProvider).siteName, 'South GA');
    expect(c.read(filterSetProvider).filters.single.name, 'Ha');
    expect(c.read(cameraElectronicsProvider).readNoiseE, 1.5);
  });

  test('seedPlanningFromCache returns false (and keeps defaults) with no snapshot',
      () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final c = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(c.dispose);
    final before = c.read(opticsSettingsProvider);
    expect(await seedPlanningFromCache(c, 'p2'), isFalse);
    expect(c.read(opticsSettingsProvider), before);
  });

  test('saveList drops sections of profiles the daemon no longer has',
      () async {
    await captureProfileCache(cache, _FakeApi(), list);
    await cache.saveList('p2', [(id: 'p2', name: 'Travel Rig')]);
    final stored = await cache.load();
    expect((stored['sections'] as Map), isEmpty);
    expect(stored['active_id'], 'p2');
  });
}
