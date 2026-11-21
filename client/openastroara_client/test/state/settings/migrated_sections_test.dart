import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_live_sync.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';
import 'package:openastroara/state/settings/diagnostics_mode_state.dart';
import 'package:openastroara/state/settings/dome_settings_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
import 'package:openastroara/state/settings/filter_wheel_policy_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/plate_solve_settings_state.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for the sections that load before they save: one list drives the
// reset and the live-sync table.

/// Every migrated section by the path the rig serves it under (the filter
/// wheel's two sections share the route `filter-wheel`).
const _paths = [
  'autofocus',
  'camera-electronics',
  'cooling-fan',
  'custom-horizon',
  'diagnostics-mode',
  'dome',
  'equipment-connection',
  'filenames',
  'filter-set',
  'filter-wheel/labels',
  'filter-wheel/policy',
  'imaging-defaults',
  'notifications',
  'optics',
  'phd2',
  'plate-solve',
  'polar-align',
  'safety',
  'site',
  'storage',
  'unguided',
];

final _selfLoading = {
  autofocusSettingsProvider,
  coolingFanProvider,
  domeSettingsProvider,
  equipmentConnectionProvider,
  filterWheelLabelsProvider,
  filterWheelPolicyProvider,
  imagingDefaultsProvider,
  opticsSettingsProvider,
  phd2SettingsProvider,
  polarAlignSettingsProvider,
  safetyPoliciesProvider,
  siteSettingsProvider,
};

String _routeOf(String path) => path.split('/').first;

/// The section the rig serves under [path].
MigratedSection _sectionAt(String path) => migratedSections.firstWhere(
  (s) => switch (path) {
    'filter-wheel/labels' => s.provider == filterWheelLabelsProvider,
    'filter-wheel/policy' => s.provider == filterWheelPolicyProvider,
    _ => s.route == path,
  },
);

FakeSectionRig _rig() => FakeSectionRig({
  for (final p in _paths) p: rigDefaults[p] ?? <String, Object?>{},
  'storage': {
    'save_directory': '/data',
    'file_format': 'fits',
    'compression': 'off',
    'filename_template': '{target}',
    'min_free_disk_warn_gb': 10,
    'min_free_disk_critical_gb': 5,
    'backup_retention_count': 3,
    'fault_log_retention_days': 30,
  },
});

ProviderContainer _container(FakeSectionRig rig) {
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  for (final s in migratedSections) {
    c.listen(s.provider, (_, _) {});
  }
  return c;
}

void main() {
  test('Migrated sections: every live-sync route loads exactly its own '
      'sections', () async {
    expect(
      {for (final s in migratedSections) s.route},
      {for (final p in _paths) _routeOf(p)},
    );
    final c = _container(_rig());
    for (final route in {for (final p in _paths) _routeOf(p)}) {
      await applyProfileUpdated(c, _routeOf(route));
      for (final s in migratedSections) {
        // The wheel's two sections load on their own (SelfLoadingSection).
        if (s.route != route && _selfLoading.contains(s.provider)) continue;
        expect(
          c.read(s.provider).canSave,
          s.route == route,
          reason: '${s.route} after profile.updated for $route',
        );
      }
      resetMigratedSections(c.read);
    }
  });

  test('Migrated sections: the reset sends every one back to not loaded, '
      'saved or not', () async {
    final c = _container(_rig());
    for (final route in {for (final p in _paths) _routeOf(p)}) {
      await applyProfileUpdated(c, _routeOf(route));
    }
    expect(migratedSections.every((s) => c.read(s.provider).canSave), isTrue);
    resetMigratedSections(c.read);
    for (final s in migratedSections) {
      expect(c.read(s.provider).canSave, isFalse, reason: s.route);
      expect(c.read(s.provider).value, isNull, reason: s.route);
    }
  });

  // One edit per section, made on a copy the rig served.
  final edits = <String, void Function(ProviderContainer)>{
    'unguided': (c) =>
        c.read(unguidedSettingsProvider.notifier).setGuardEnabled(false),
    'plate-solve': (c) =>
        c.read(plateSolveSettingsProvider.notifier).setCenterAfterSlew(false),
    'camera-electronics': (c) => c
        .read(cameraElectronicsProvider.notifier)
        .edit((v) => v.copyWith(readNoiseE: 1.5)),
    'custom-horizon': (c) =>
        c.read(customHorizonProvider.notifier).addPoint(90, 20),
    'cooling-fan': (c) => c
        .read(coolingFanProvider.notifier)
        .edit((_) => const CoolingFan(deviceId: 'sw-1', portId: 2)),
    'dome': (c) => c
        .read(domeSettingsProvider.notifier)
        .edit((v) => v.copyWith(domeRadiusMm: 1500)),
    'equipment-connection': (c) => c
        .read(equipmentConnectionProvider.notifier)
        .edit((v) => v.copyWithAutoConnect(EquipmentDeviceType.camera, false)),
    'notifications': (c) =>
        c.read(notificationsSettingsProvider.notifier).setPushoverToken('mine'),
    'filenames': (c) => c
        .read(filenamesSettingsProvider.notifier)
        .setCompressDarksAndBias(false),
    'diagnostics-mode': (c) => c
        .read(diagnosticsModeProvider.notifier)
        .edit((_) => DiagnosticsMode.values.last),
    'filter-set': (c) => c
        .read(filterSetProvider.notifier)
        .addFilter(const PlanningFilter(name: 'Ha', kind: FilterKind.ha)),
    'filter-wheel/labels': (c) =>
        c.read(filterWheelLabelsProvider.notifier).setLabel(1, 'Lum'),
    'filter-wheel/policy': (c) => c
        .read(filterWheelPolicyProvider.notifier)
        .edit((v) => v.copyWith(useFilterWheelOffsets: true)),
    'imaging-defaults': (c) =>
        c.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-20),
    'autofocus': (c) =>
        c.read(autofocusSettingsProvider.notifier).setExposureSeconds(8),
    'optics': (c) =>
        c.read(opticsSettingsProvider.notifier).setFocalLengthMm(382),
    'site': (c) =>
        c.read(siteSettingsProvider.notifier).setLatitudeDeg(-33.856784),
    'safety': (c) =>
        c.read(safetyPoliciesProvider.notifier).setMeridianPauseMin(12),
    'phd2': (c) => c.read(phd2SettingsProvider.notifier).setSettleTimeSec(15),
    'polar-align': (c) => c
        .read(polarAlignSettingsProvider.notifier)
        .edit((v) => v.copyWith(exposureSeconds: 4)),
  };

  for (final MapEntry(key: route, value: edit) in edits.entries) {
    final section = _sectionAt(route);

    test(
      '$route: Save after a failed load writes nothing to the rig',
      () async {
        final rig = _rig()..failGet.add(route);
        final c = _container(rig);
        await expectLater(
          c.read(section.provider.notifier).save(rig.api()),
          throwsA(isA<SectionNotLoadedException>()),
        );
        expect(rig.puts, isEmpty);
      },
    );

    test(
      '$route: Save over a copy another device replaced is refused',
      () async {
        final rig = _rig();
        final c = _container(rig);
        await applyProfileUpdated(c, _routeOf(route));
        await settle(() => c.read(section.provider).canSave);
        edit(c);
        rig.savedElsewhere(route);
        await expectLater(
          c.read(section.provider.notifier).save(rig.api()),
          throwsA(isA<SectionConflictException>()),
        );
        expect(rig.puts, isEmpty);
        expect(c.read(section.provider).conflict, isTrue);
      },
    );
  }

  // ADR 0015 rule 3: the rig's copy takes no client default. A reply with no
  // body, or without any one key (or with an engine the client does not
  // know), fails the load, so Save writes no default.
  List<Map<String, Object?>> without(Map<String, Object?> m) => [
    for (final k in m.keys) {...m}..remove(k),
  ];
  final partial = <String, List<Object?>>{
    for (final r in [
      'camera-electronics',
      'dome',
      'equipment-connection',
      'unguided',
    ])
      r: [null, ...without(rigDefaults[r]!)],
    // A null fan is "none"; a missing key is a bad reply.
    'cooling-fan': [
      null,
      <String, Object?>{},
      ...without(rigDefaults['cooling-fan']!),
    ],
    'plate-solve': [
      null,
      ...without(rigDefaults['plate-solve']!),
      {...rigDefaults['plate-solve']!, 'engine': 'bogus'},
    ],
    'filter-set': [
      null,
      <String, Object?>{},
      {
        'filters': [
          {'name': 'Ha', 'kind': 'bogus', 'bandwidth_nm': 7},
        ],
      },
      {
        'filters': [
          {'kind': 'ha', 'bandwidth_nm': 7},
        ],
      },
      {
        'filters': [
          {'name': 'Ha', 'kind': 'ha'},
        ],
      },
    ],
    'filter-wheel/labels': [
      null,
      <String, Object?>{},
      {'labels': <String>[]},
      {
        'labels': ['L', 3],
      },
    ],
    'filter-wheel/policy': [
      null,
      ...without(rigDefaults['filter-wheel/policy']!),
    ],
    'imaging-defaults': [
      null,
      ...without(rigDefaults['imaging-defaults']!),
      {...rigDefaults['imaging-defaults']!, 'frame_kind': 'bogus'},
    ],
    'autofocus': [
      null,
      ...without(rigDefaults['autofocus']!),
      {...rigDefaults['autofocus']!, 'method': 'bogus'},
      {...rigDefaults['autofocus']!, 'telescope_type': 'bogus'},
    ],
    'optics': [null, ...without(rigDefaults['optics']!)],
    'polar-align': [
      null,
      ...without(rigDefaults['polar-align']!),
      {...rigDefaults['polar-align']!, 'loop_mode': 'bogus'},
      {...rigDefaults['polar-align']!, 'camera': 'bogus'},
    ],
    'site': [
      null,
      ...without(rigDefaults['site']!),
      {...rigDefaults['site']!, 'twilight_definition': 'bogus'},
    ],
    'safety': [
      null,
      // Every key the client reads (not the daemon's fault policies).
      ...without(
        {...rigDefaults['safety']!}..removeWhere(
          (key, _) => const {
            'hot_reconnect_enabled',
            'on_camera_lost',
            'on_mount_lost',
            'on_tracking_lost',
            'switch_value_tolerance_pct',
          }.contains(key),
        ),
      ),
      for (final key in [
        'on_unsafe',
        'meridian_refocus',
        'meridian_guider_recal',
        'on_altitude_limit',
        'on_guider_lost',
        'on_disk_space_critical',
        'calibration_capture_default',
      ])
        {...rigDefaults['safety']!, key: 'bogus'},
    ],
    'phd2': [null, ...without(rigDefaults['phd2']!)],
    'custom-horizon': [
      null,
      <String, Object?>{},
      for (final p in without({'azimuth_deg': 90, 'altitude_deg': 20}))
        {
          'points': [p],
        },
    ],
  };
  for (final MapEntry(key: route, value: bodies) in partial.entries) {
    final section = _sectionAt(route);
    test('$route: a reply missing a key fails the load and Save writes '
        'nothing', () async {
      for (final body in bodies) {
        final rig = FakeSectionRig({route: body});
        final n = _container(rig).read(section.provider.notifier);
        expect(await n.load(rig.api()), isFalse, reason: '$body');
        await expectLater(
          n.save(rig.api()),
          throwsA(isA<SectionNotLoadedException>()),
        );
        expect(rig.puts, isEmpty, reason: '$body');
      }
    });
  }

  // The PUT reply is the rig's copy too: one without the edited key fails the
  // Save, which keeps the edit and the loaded tag (no default comes back).
  const replyDrops = {
    'autofocus': 'exposure_seconds',
    'camera-electronics': 'read_noise_e',
    'cooling-fan': 'port_id',
    'custom-horizon': 'points',
    'dome': 'dome_radius_mm',
    'equipment-connection': 'camera',
    'filter-set': 'filters',
    'filter-wheel/labels': 'labels',
    'filter-wheel/policy': 'use_filter_wheel_offsets',
    'imaging-defaults': 'cooler_target_c',
    'optics': 'focal_length_mm',
    'phd2': 'settle_time_sec',
    'plate-solve': 'center_after_slew',
    'polar-align': 'exposure_seconds',
    'safety': 'meridian_pause_min',
    'site': 'latitude_deg',
    'unguided': 'guard_enabled',
  };
  for (final MapEntry(key: route, value: key) in replyDrops.entries) {
    final section = _sectionAt(route);
    test('$route: a save reply missing a key fails the Save and keeps the '
        'edit', () async {
      final rig = _rig()..dropFromReply[route] = key;
      final c = _container(rig);
      await applyProfileUpdated(c, _routeOf(route));
      await settle(() => c.read(section.provider).canSave);
      final etag = c.read(section.provider).etag;
      edits[route]!(c);
      final edited = c.read(section.provider).value;
      await expectLater(
        c.read(section.provider.notifier).save(rig.api()),
        throwsA(anything),
      );
      expect(c.read(section.provider).value, same(edited));
      expect(c.read(section.provider).etag, etag);
    });
  }

  test('optics: a daemon write announced by profile.updated reloads the copy, '
      'so the next Save is not refused (ADR 0015)', () async {
    ProfileApi.forgetOwnWrites(); // an earlier case's own optics save
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(opticsSettingsProvider).canSave);
    // A camera connect fills the sensor on the rig; the daemon announces it.
    rig.daemonWrote('optics', {
      ...rigDefaults['optics']!,
      'sensor_width_px': 6248,
      'sensor_height_px': 4176,
      'pixel_size_um': 3.76,
    });
    await applyProfileUpdated(c, 'optics');
    c.read(opticsSettingsProvider.notifier).setFocalLengthMm(382);
    await c.read(opticsSettingsProvider.notifier).save(rig.api());
    final put = rig.puts.single;
    expect(put.ifMatch, isNot('"optics-v1"'), reason: 'the tag of the copy the daemon wrote');
    expect((put.body as Map)['sensor_width_px'], 6248, reason: 'the daemon\'s fill is kept');
    expect((put.body as Map)['focal_length_mm'], 382);
  });

  test('site: a time sync write announced by profile.updated reloads the '
      'copy, so the next Save is not refused (ADR 0015)', () async {
    ProfileApi.forgetOwnWrites(); // an earlier case's own site save
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(siteSettingsProvider).canSave);
    // A GPS fix at time sync fills the site on the rig; the daemon announces it.
    rig.daemonWrote('site', {
      ...rigDefaults['site']!,
      'latitude_deg': 51.477833,
      'longitude_deg': -0.0015,
      'elevation_m': 42.0,
    });
    await applyProfileUpdated(c, 'site');
    c.read(siteSettingsProvider.notifier).setObserverName('J. Troy');
    await c.read(siteSettingsProvider.notifier).save(rig.api());
    final put = rig.puts.single;
    expect(put.ifMatch, '"site-v2"', reason: 'the tag of the copy the daemon wrote');
    final body = put.body as Map;
    expect(body['latitude_deg'], 51.477833, reason: 'the daemon\'s fix is kept');
    expect(body['observer_name'], 'J. Troy');
  });

  test('site: the pier switch saves only the pier, with the loaded tag, and '
      'keeps an unsaved edit (ADR 0015)', () async {
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(siteSettingsProvider).canSave);
    final tag = rig.etagOf('site');
    // Typed in Settings > Location & sky; Save not pressed yet.
    c.read(siteSettingsProvider.notifier).setLatitudeDeg(51.477833);
    await c
        .read(siteSettingsProvider.notifier)
        .saveOnRigCopy((s) => s.copyWith(permanentPier: true));
    final put = rig.puts.single;
    expect(put.ifMatch, tag);
    expect((put.body as Map)['permanent_pier'], isTrue);
    expect((put.body as Map)['latitude_deg'], 0.0, reason: 'the edit is not sent');
    final shown = c.read(siteSettingsProvider);
    expect(shown.value!.latitudeDeg, 51.477833, reason: 'the edit stays');
    expect(shown.value!.permanentPier, isTrue);
    expect(shown.etag, rig.etagOf('site'), reason: 'the next Save is not refused');
  });

  test('safety: a meridian flip write announced by profile.updated reloads '
      'the copy, so the next Save is not refused (ADR 0015)', () async {
    ProfileApi.forgetOwnWrites(); // an earlier case's own safety save
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(safetyPoliciesProvider).canSave);
    // The flip executor records a completed flip on the rig and announces it.
    rig.daemonWrote('safety', {
      ...rigDefaults['safety']!,
      'first_flip_completed_utc': '2026-11-20T02:00:00Z',
      'first_flip_pier_side_changed': true,
    });
    await applyProfileUpdated(c, 'safety');
    c.read(safetyPoliciesProvider.notifier).setMeridianPauseMin(12);
    await c.read(safetyPoliciesProvider.notifier).save(rig.api());
    final put = rig.puts.single;
    expect(put.ifMatch, '"safety-v2"', reason: 'the tag of the copy the daemon wrote');
    expect((put.body as Map)['meridian_pause_min'], 12);
    expect(c.read(safetyPoliciesProvider).value!.firstFlipCompletedUtc,
        DateTime.utc(2026, 11, 20, 2));
  });

  test('phd2: a guide camera fill announced by profile.updated reloads the '
      'copy, so the next Save is not refused (ADR 0015)', () async {
    ProfileApi.forgetOwnWrites(); // an earlier case's own guider save
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(phd2SettingsProvider).canSave);
    // The guider connects and the daemon fills the guide camera's pixel size
    // and the guide train on the rig, then announces it.
    rig.daemonWrote('phd2', {
      ...rigDefaults['phd2']!,
      'guide_pixel_size': 3.75,
      'guide_focal_length': 128,
    });
    await applyProfileUpdated(c, 'phd2');
    c.read(phd2SettingsProvider.notifier).setSettleTimeSec(15);
    await c.read(phd2SettingsProvider.notifier).save(rig.api());
    final put = rig.puts.single;
    expect(put.ifMatch, '"phd2-v2"', reason: 'the tag of the copy the daemon wrote');
    final body = put.body as Map;
    expect(body['guide_pixel_size'], 3.75, reason: 'the daemon\'s fill is kept');
    expect(body['settle_time_sec'], 15);
  });

  test('phd2: a guide camera fill announced moments after this device\'s own '
      'guider save still lands, so the next Save is not refused (ADR 0015)', () async {
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(phd2SettingsProvider).canSave);
    final guider = c.read(phd2SettingsProvider.notifier);
    // Settings → Guider's Connect saves the pending edit, then connects.
    guider.setSettleTimeSec(12);
    await guider.save(rig.api());
    expect(ProfileApi.wroteRecently('phd2'), isTrue);
    await applyProfileUpdated(c, 'phd2'); // that save's own announcement
    // The guider connects within the same moments; the daemon fills the guide
    // camera's pixel size and the guide train, then announces it.
    rig.daemonWrote('phd2', {
      ...rig.sectionOf('phd2')! as Map,
      'guide_pixel_size': 3.75,
      'guide_focal_length': 128,
    });
    final filled = rig.etagOf('phd2');
    await applyProfileUpdated(c, 'phd2');
    expect(c.read(phd2SettingsProvider).value!.guidePixelSize, 3.75);
    guider.setSettleTimeSec(15);
    await guider.save(rig.api());
    expect(rig.puts.last.ifMatch, filled, reason: 'the tag of the copy the daemon wrote');
    expect((rig.puts.last.body as Map)['guide_focal_length'], 128);
  });

  test('phd2: a rig copy from before the Dec split reads Dec as RA, and an '
      'Automatic guider profile as 0 (ADR 0015)', () async {
    final rig = FakeSectionRig({
      'phd2': {...rigDefaults['phd2']!, 'minimum_move': 0.3},
    });
    final c = _container(rig);
    await settle(() => c.read(phd2SettingsProvider).canSave);
    final v = c.read(phd2SettingsProvider).value!;
    expect(v.decMinimumMove, 0.3);
    expect(v.guiderProfileId, 0);
  });

  // The re-arm POST answers the rig's policies with their new tag.
  Map<String, Object?> rearmed(Object? copy) => {
    ...copy! as Map<String, Object?>,
    'first_flip_confirmed': false,
  };

  test('safety: the first-flip re-arm keeps the tag it answers and an unsaved '
      'edit, so the next Save is not refused (ADR 0015)', () async {
    final rig = _rig()
      ..actions['safety/first-flip/rearm'] = (
        section: 'safety',
        change: rearmed,
      );
    rig.daemonWrote('safety', {
      ...rigDefaults['safety']!,
      'first_flip_confirmed': true,
    });
    final c = _container(rig);
    await settle(() => c.read(safetyPoliciesProvider).canSave);
    final n = c.read(safetyPoliciesProvider.notifier);
    n.setMeridianPauseMin(12); // typed in the panel; Save not pressed yet
    await n.rearmFirstFlip();
    final shown = c.read(safetyPoliciesProvider);
    expect(shown.value!.firstFlipConfirmed, isFalse);
    expect(shown.value!.meridianPauseMin, 12, reason: 'the edit stays');
    expect(shown.etag, rig.etagOf('safety'));
    expect(shown.conflict, isFalse);
    await n.save(rig.api());
    expect(rig.puts.single.ifMatch, shown.etag);
    expect((rig.puts.single.body as Map)['meridian_pause_min'], 12);
  });

  test('safety: a re-arm answered with a copy another device saved keeps the '
      'old tag and offers Reload (ADR 0015)', () async {
    final rig = _rig()
      ..actions['safety/first-flip/rearm'] = (
        section: 'safety',
        change: rearmed,
      );
    final c = _container(rig);
    await settle(() => c.read(safetyPoliciesProvider).canSave);
    final tag = c.read(safetyPoliciesProvider).etag;
    // Another device turned auto flip off; this one did not hear of it.
    rig.daemonWrote('safety', {
      ...rigDefaults['safety']!,
      'meridian_flip_auto': false,
      'first_flip_confirmed': true,
    });
    final n = c.read(safetyPoliciesProvider.notifier);
    await n.rearmFirstFlip();
    final shown = c.read(safetyPoliciesProvider);
    expect(shown.value!.firstFlipConfirmed, isFalse);
    expect(shown.etag, tag, reason: 'the copy shown is not the rig\'s');
    expect(shown.conflict, isTrue);
    await expectLater(
      n.save(rig.api()),
      throwsA(isA<SectionConflictException>()),
    );
    expect(rig.puts, isEmpty, reason: 'auto flip stays off on the rig');
  });

  // A self-loading section starts its own first read as it is built. A change
  // made at once loads too; that load is superseded by the first read and
  // lands nothing, so _loadOnce loads once more instead of refusing.
  test('site: a change made before its first read lands is saved, not '
      'refused', () async {
    final rig = _rig();
    final c = _container(rig);
    await c
        .read(siteSettingsProvider.notifier)
        .editAndSave((s) => s.copyWith(siteName: 'Ridge'));
    expect(rig.puts, hasLength(1));
    expect(c.read(siteSettingsProvider).value!.siteName, 'Ridge');
  });

  // The diagnostics radios save on each tap (replaceAndSave).
  const mode = 'diagnostics-mode', tapped = DiagnosticsMode.abortOnCritical;

  test('diagnostics-mode: a failed radio tap writes nothing and shows the '
      'loaded mode again', () async {
    final rig = _rig();
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    // Not loaded: the tap loads once more first; that failing too refuses it.
    rig.failGet.add(mode);
    await expectLater(
      n.replaceAndSave(tapped),
      throwsA(isA<SectionNotLoadedException>()),
    );
    rig.failGet.remove(mode);
    await n.load(rig.api());
    rig.failPut.add(mode);
    await expectLater(n.replaceAndSave(tapped), throwsException);
    expect(c.read(diagnosticsModeProvider).value, DiagnosticsMode.notifyOnly);
    expect(rig.puts, isEmpty);
  });

  test('diagnostics-mode: a tap after one failed load loads again and saves '
      'when the rig answers', () async {
    final rig = _rig()..failGet.add(mode);
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    expect(c.read(diagnosticsModeProvider).canSave, isFalse);
    rig.failGet.remove(mode);
    await n.replaceAndSave(tapped);
    expect(rig.puts, hasLength(1));
    expect(c.read(diagnosticsModeProvider).source, SectionSource.rig);
  });

  test('diagnostics-mode: a tap made from the offline cache copy is refused '
      'even when the rig answers, and nothing is sent', () async {
    final rig = _rig()..failGet.add(mode);
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    n.seed(DiagnosticsMode.notifyOnly);
    expect(c.read(diagnosticsModeProvider).source, SectionSource.cache);
    rig.failGet.remove(mode);
    await expectLater(
      n.replaceAndSave(tapped),
      throwsA(isA<SectionNotLoadedException>()),
    );
    expect(rig.puts, isEmpty);
  });

  test('diagnostics-mode: a radio tap refused as stale keeps the choice and '
      'flags the conflict', () async {
    final rig = _rig();
    final c = _container(rig);
    await c.read(diagnosticsModeProvider.notifier).load(rig.api());
    rig.savedElsewhere(mode);
    await expectLater(
      c.read(diagnosticsModeProvider.notifier).replaceAndSave(tapped),
      throwsA(isA<SectionConflictException>()),
    );
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(c.read(diagnosticsModeProvider).conflict, isTrue);
  });

  test('diagnostics-mode: a quick second tap is saved when the first one '
      'fails', () async {
    final rig = _rig()..failPut.add(mode);
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    final first = n.replaceAndSave(DiagnosticsMode.pauseOnCritical);
    final second = n.replaceAndSave(tapped);
    await expectLater(first, throwsException);
    await second;
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(rig.puts.single.body, {'mode': 'abort_on_critical'});
  });

  test('diagnostics-mode: two quick taps that both fail show the mode the '
      'rig holds', () async {
    final rig = _rig();
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    await n.replaceAndSave(tapped);
    rig.failPut.addAll([mode, mode]);
    final first = n.replaceAndSave(DiagnosticsMode.pauseOnCritical);
    final second = n.replaceAndSave(DiagnosticsMode.notifyOnly);
    await expectLater(first, throwsException);
    await expectLater(second, throwsException);
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(rig.puts.single.body, {'mode': 'abort_on_critical'});
  });
}
