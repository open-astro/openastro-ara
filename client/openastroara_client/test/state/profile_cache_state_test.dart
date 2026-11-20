import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

import '../support/fake_section_rig.dart';
import '../support/section_load.dart';

/// The rig this device talks to, null until it connects.
class _Api extends Notifier<ProfileApi?> {
  @override
  ProfileApi? build() => null;
  void connect(ProfileApi api) => state = api;
}

final _apiOf = NotifierProvider<_Api, ProfileApi?>(_Api.new);

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
  Future<UnguidedSettings> getUnguidedSettings() async =>
      const UnguidedSettings(stepDownAfter: 3);

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

  @override
  Future<List<CustomHorizonPoint>> getCustomHorizon() async => const [
        CustomHorizonPoint(azimuthDeg: 90, altitudeDeg: 25),
        CustomHorizonPoint(azimuthDeg: 180, altitudeDeg: 12),
      ];
}

class _FailingUnguidedApi extends _FakeApi {
  @override
  Future<UnguidedSettings> getUnguidedSettings() async =>
      throw StateError('500 from /profile/unguided');
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
    // The staleness stamp the offline picker surfaces ("gear cached N days ago").
    expect(
        DateTime.tryParse(
            (sections['p1'] as Map)['captured_utc']?.toString() ?? ''),
        isNotNull);
  });

  test('#1137: the custom horizon is captured and seeds the offline notifier', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final stored = await cache.load();
    final horizon = ((stored['sections'] as Map)['p1'] as Map)['custom_horizon'] as Map;
    expect((horizon['points'] as List), hasLength(2));

    final container = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(container.dispose);
    expect(await seedPlanningFromCache(container, 'p1'), isTrue);
    final seeded = container.read(customHorizonProvider).value!;
    expect(seeded.map((p) => p.azimuthDeg), [90, 180]);
    expect(seeded.map((p) => p.altitudeDeg), [25, 12]);
  });

  test('#1264: a failed unguided read is best-effort — the rest is captured, no unguided key', () async {
    await captureProfileCache(cache, _FailingUnguidedApi(), list);
    final stored = await cache.load();
    final p1 = (stored['sections'] as Map)['p1'] as Map;
    expect(p1['optics'], containsPair('focal_length_mm', 382));
    expect(p1.containsKey('unguided'), isFalse);
  });

  test('a snapshot without camera electronics leaves the section not loaded, never defaults', () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {'optics': ProfileApi.opticsToJson(const OpticsSettings())});
    final c = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(cameraElectronicsProvider).value, isNull);
    expect(c.read(cameraElectronicsProvider).canSave, isFalse);
  });

  test('#1264: a snapshot without an unguided section leaves a staged knob alone', () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {'optics': ProfileApi.opticsToJson(const OpticsSettings())});
    final container = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(container.dispose);
    await loadSection(container, unguidedSettingsProvider, 'unguided');
    container.read(unguidedSettingsProvider.notifier).setPriorToleranceBasis('pixel');
    expect(await seedPlanningFromCache(container, 'p1'), isTrue);
    expect(container.read(unguidedSettingsProvider).value!.priorToleranceBasis, 'pixel',
        reason: 'an older snapshot must not reset the knob to defaults');
  });

  test('#1137: a snapshot without a horizon leaves the notifier alone', () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {'optics': ProfileApi.opticsToJson(const OpticsSettings())});
    final container = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(container.dispose);
    await loadSection(container, customHorizonProvider, 'custom-horizon', {'points': []});
    container.read(customHorizonProvider.notifier).addPoint(45, 30);
    expect(await seedPlanningFromCache(container, 'p1'), isTrue);
    expect(container.read(customHorizonProvider).value!, hasLength(1), reason: 'an older snapshot must not wipe a staged skyline');
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
    expect(c.read(opticsSettingsProvider).value!.focalLengthMm, 382);
    expect(c.read(imagingDefaultsProvider).value!.defaultGain, 101);
    expect(c.read(autofocusSettingsProvider).value!.steps, 9);
    expect(c.read(siteSettingsProvider).value!.siteName, 'South GA');
    expect(c.read(filterSetProvider).value!.filters.single.name, 'Ha');
    expect(c.read(cameraElectronicsProvider).value!.readNoiseE, 1.5);
  });

  test(
    'a snapshot without a filter set leaves it not loaded, never a default a '
    'Save could send',
    () async {
      await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
      await cache.saveSections('p1', {
        'optics': ProfileApi.opticsToJson(const OpticsSettings()),
      });
      final c = ProviderContainer(
        overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
      );
      addTearDown(c.dispose);
      expect(await seedPlanningFromCache(c, 'p1'), isTrue);
      expect(c.read(filterSetProvider).source, SectionSource.notLoaded);
      expect(c.read(filterSetProvider).value, isNull);
    },
  );

  test('a filter set from the snapshot is a cache copy: readable, not '
      'saveable', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(filterSetProvider).source, SectionSource.cache);
    expect(c.read(filterSetProvider).canSave, isFalse);
    c
        .read(filterSetProvider.notifier)
        .addFilter(const PlanningFilter(name: 'OIII', kind: FilterKind.oiii));
    expect(c.read(filterSetProvider).value!.filters.map((f) => f.name), [
      'Ha',
    ], reason: 'an edit on a cache copy is ignored');
  });

  test('imaging defaults and autofocus from the snapshot are cache copies: '
      'readable, never saved (ADR 0015)', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final rig = FakeSectionRig({
      'imaging-defaults': rigDefaults['imaging-defaults'],
      'autofocus': rigDefaults['autofocus'],
    })..failGet.addAll(['imaging-defaults', 'autofocus']);
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(imagingDefaultsProvider).source, SectionSource.cache);
    expect(c.read(autofocusSettingsProvider).source, SectionSource.cache);
    c.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-25);
    expect(c.read(imagingDefaultsProvider).value!.coolerTargetC, -10,
        reason: 'an edit on a cache copy is ignored');
    await expectLater(
      c.read(autofocusSettingsProvider.notifier).changeAndSave(
        (s) => s.copyWith(exposureSeconds: 8),
      ),
      throwsA(isA<SectionNotLoadedException>()),
    );
    for (final SectionNotifier<Object?> n in [
      c.read(imagingDefaultsProvider.notifier),
      c.read(autofocusSettingsProvider.notifier),
    ]) {
      await expectLater(
        n.save(rig.api()),
        throwsA(isA<SectionNotLoadedException>()),
      );
    }
    expect(rig.puts, isEmpty);
  });

  test('a snapshot without imaging defaults or autofocus leaves them not '
      'loaded (ADR 0015)', () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {
      'optics': ProfileApi.opticsToJson(const OpticsSettings()),
    });
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(imagingDefaultsProvider).value, isNull);
    expect(c.read(autofocusSettingsProvider).value, isNull);
  });

  test('a cache copy gives way to the rig copy once the rig connects '
      '(ADR 0015)', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final rig = FakeSectionRig({
      'imaging-defaults': {...rigDefaults['imaging-defaults']!, 'gain': 250},
    });
    final api = _Api();
    final c = ProviderContainer(
      overrides: [
        profileCacheServiceProvider.overrideWithValue(cache),
        profileApiProvider.overrideWith((ref) => ref.watch(_apiOf)),
        _apiOf.overrideWith(() => api),
      ],
    );
    addTearDown(c.dispose);
    c.listen(imagingDefaultsProvider, (_, _) {});
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(imagingDefaultsProvider).source, SectionSource.cache);
    api.connect(rig.api());
    await settle(() => c.read(imagingDefaultsProvider).canSave);
    expect(c.read(imagingDefaultsProvider).value!.defaultGain, 250);
    expect(c.read(imagingDefaultsProvider).etag, rig.etagOf('imaging-defaults'));
  });

  test('the optics from the snapshot are a cache copy: readable, never saved '
      '(ADR 0015)', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final rig = FakeSectionRig({'optics': rigDefaults['optics']})
      ..failGet.add('optics');
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(opticsSettingsProvider).source, SectionSource.cache);
    c.read(opticsSettingsProvider.notifier).setSensorWidthPx(0);
    expect(c.read(opticsSettingsProvider).value!.sensorWidthPx, 6248);
    await expectLater(
      c.read(opticsSettingsProvider.notifier).save(rig.api()),
      throwsA(isA<SectionNotLoadedException>()),
    );
    expect(rig.puts, isEmpty);
  });

  test('a snapshot without the optics leaves them not loaded (ADR 0015)',
      () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {
      'site': ProfileApi.siteSettingsToJson(const SiteSettings()),
    });
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(opticsSettingsProvider).value, isNull);
  });

  test('the site from the snapshot is a cache copy: readable, never saved '
      '(ADR 0015)', () async {
    await captureProfileCache(cache, _FakeApi(), list);
    final rig = FakeSectionRig({'site': rigDefaults['site']})
      ..failGet.add('site');
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(siteSettingsProvider).source, SectionSource.cache);
    c.read(siteSettingsProvider.notifier).setSkyBortle(9);
    expect(c.read(siteSettingsProvider).value!.bortleClass, 4);
    await expectLater(
      c.read(siteSettingsProvider.notifier).save(rig.api()),
      throwsA(isA<SectionNotLoadedException>()),
    );
    expect(rig.puts, isEmpty);
  });

  test('a snapshot without the site leaves it not loaded (ADR 0015)',
      () async {
    await cache.saveList('p1', [(id: 'p1', name: 'RedCat 91')]);
    await cache.saveSections('p1', {
      'optics': ProfileApi.opticsToJson(const OpticsSettings()),
    });
    final c = ProviderContainer(
      overrides: [profileCacheServiceProvider.overrideWithValue(cache)],
    );
    addTearDown(c.dispose);
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    expect(c.read(siteSettingsProvider).source, SectionSource.notLoaded);
    expect(c.read(siteSettingsProvider).value, isNull);
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
