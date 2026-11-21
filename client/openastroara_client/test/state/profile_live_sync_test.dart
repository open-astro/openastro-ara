import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/profile_live_sync.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/dome_settings_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

import '../support/fake_section_rig.dart';
import '../support/section_load.dart';

/// The rig's profile endpoints, as far as this test needs: the site section
/// (whatever name it currently holds), the profile list, and any other
/// section's stored JSON in [stored].
class _Rig implements HttpClientAdapter {
  /// The site name the rig holds; setting it is another device's save, so
  /// the copy's tag moves too.
  String get siteName => _siteName;
  set siteName(String name) {
    _siteName = name;
    siteVersion++;
  }

  String _siteName = 'Backyard';
  int siteReads = 0;

  /// The site copy's version, served as its `ETag`; each write moves it.
  int siteVersion = 1;

  /// Other sections' JSON by request path, and how often each path was read.
  final stored = <String, Object>{};
  final reads = <String, int>{};

  /// Holds each write's answer until completed: the daemon publishes
  /// `profile.updated` before it writes the HTTP response.
  Completer<void>? holdWrites;
  int writes = 0;

  /// Answer the next [refuseWrites] writes 409, like a session another device
  /// took over.
  int refuseWrites = 0;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    var status = 200;
    if (options.method != 'GET') {
      writes++;
      if (refuseWrites > 0) {
        refuseWrites--;
        status = 409;
      }
      await holdWrites?.future;
    }
    if (options.method == 'GET') {
      reads.update(options.path, (n) => n + 1, ifAbsent: () => 1);
    }
    Object body;
    final site = options.path.endsWith('/profile/site');
    if (site) {
      if (options.method == 'GET') siteReads++;
      if (options.method != 'GET' && status == 200) siteVersion++;
      // The whole section, as the rig serves it (ADR 0015: no client default).
      body = {...rigDefaults['site']!, 'site_name': siteName};
    } else if (options.path.endsWith('/profiles')) {
      body = {'active_id': null, 'profiles': <Object>[]};
    } else {
      body = stored[options.path] ?? <String, Object>{};
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
        if (site) 'etag': ['"site-v$siteVersion"'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

const _server = AraServer(hostname: 'host', port: 5555);

Future<void> _until(bool Function() done) async {
  while (!done()) {
    await Future<void>.delayed(Duration.zero);
  }
}

/// Until the rig has seen [n] writes (each one's request went out).
Future<void> _sent(_Rig rig, int n) => _until(() => rig.writes >= n);

class _Cache extends ProfileCacheService {
  int listSaves = 0;
  @override
  Future<void> saveList(
    String? activeId,
    List<({String id, String name})> profiles,
  ) async => listSaves++;
}

ProviderContainer _container(_Rig rig, {_Cache? cache}) {
  final api = ProfileApi(_server, dio: Dio()..httpClientAdapter = rig);
  final c = ProviderContainer(
    overrides: [
      profileApiProvider.overrideWithValue(api),
      if (cache != null) profileCacheServiceProvider.overrideWithValue(cache),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

void main() {
  setUp(ProfileApi.forgetOwnWrites);

  test('profile.updated for a section re-reads it, so an open screen never shows stale values', () async {
    final rig = _Rig();
    final cache = _Cache();
    final c = _container(rig, cache: cache);
    await settleLoaded(c, siteSettingsProvider);
    expect(c.read(siteSettingsProvider).value!.siteName, 'Backyard');
    rig.siteName = 'Seen on the other device';

    await applyProfileUpdated(c, 'site');

    expect(c.read(siteSettingsProvider).value!.siteName, 'Seen on the other device');
    expect(
      cache.listSaves,
      1,
      reason: 'the offline profile cache is refreshed too',
    );
  });

  test(
    "this device's own write is not read back over what it is still editing",
    () async {
      final rig = _Rig()..siteName = 'Saved';
      final c = _container(rig);
      await settleLoaded(c, siteSettingsProvider);
      // The write this device just made (the event for it comes back too).
      final site = c.read(siteSettingsProvider.notifier)..setSiteName('Saved');
      await site.save(c.read(profileApiProvider)!);
      site.setSiteName('Typing after the save');

      await applyProfileUpdated(c, 'site');

      expect(c.read(siteSettingsProvider).value!.siteName, 'Typing after the save');
    },
  );

  test(
    "this device's own event is skipped even when it beats the PUT's answer",
    () async {
      final rig = _Rig()
        ..siteName = 'Saved'
        ..holdWrites = Completer<void>();
      final c = _container(rig);
      await settleLoaded(c, siteSettingsProvider);
      final site = c.read(siteSettingsProvider.notifier)..setSiteName('Saved');
      final put = site.save(c.read(profileApiProvider)!);
      await _sent(rig, 1); // the answer is still held
      site.setSiteName('Typing while it saves');

      final event = applyProfileUpdated(c, 'site');
      rig.holdWrites!.complete();
      await put;
      await event;

      expect(c.read(siteSettingsProvider).value!.siteName, 'Typing while it saves');
    },
  );

  test(
    "a write the rig refused does not hide another device's change",
    () async {
      final rig = _Rig()..refuseWrites = 1;
      final c = _container(rig);
      await settleLoaded(c, siteSettingsProvider);
      rig.siteName = 'Set on the other device';
      await expectLater(
        c.read(profileApiProvider)!.putSiteSettings(const SiteSettings()),
        throwsA(isA<DioException>()),
      );

      await applyProfileUpdated(c, 'site');

      expect(c.read(siteSettingsProvider).value!.siteName, 'Set on the other device');
    },
  );

  test(
    'a refused write leaves the note of a later write to that section',
    () async {
      final rig = _Rig()
        ..holdWrites = Completer<void>()
        ..refuseWrites = 1;
      final c = _container(rig);
      final api = c.read(profileApiProvider)!;
      final refused = api.putSiteSettings(const SiteSettings());
      final saved = api.putSiteSettings(const SiteSettings(siteName: 'Saved'));
      await _sent(rig, 2);
      rig.holdWrites!.complete();
      await expectLater(refused, throwsA(isA<DioException>()));
      await saved;

      expect(ProfileApi.wroteRecently('site'), isTrue);
    },
  );

  test(
    'profile.updated for filter-wheel reads the slot labels again too',
    () async {
      const labels = '/api/v1/profile/filter-wheel/labels';
      final rig = _Rig()
        ..stored[labels] = {
          'labels': ['Ha', 'OIII', 'SII'],
        };
      final c = _container(rig);
      // A screen holds them; wait for their first read from the rig.
      await _until(
        () => c.read(filterWheelLabelsProvider).value?.labelAt(2) == 'OIII',
      );
      rig.stored[labels] = {
        'labels': ['Ha', 'Sulphur', 'SII'],
      };

      await applyProfileUpdated(c, 'filter-wheel');

      expect(c.read(filterWheelLabelsProvider).value!.labelAt(2), 'Sulphur');
    },
  );

  test('profile.updated for cooling-fan reads the chosen fan again', () async {
    const fan = '/api/v1/profile/cooling-fan';
    final rig = _Rig()..stored[fan] = {'device_id': 'switch-1', 'port_id': 0};
    final c = _container(rig);
    await settleLoaded(c, coolingFanProvider);
    expect(c.read(coolingFanProvider).value!.portId, 0);
    rig.stored[fan] = {'device_id': 'switch-1', 'port_id': 3};

    await applyProfileUpdated(c, 'cooling-fan');

    expect(c.read(coolingFanProvider).value!.portId, 3);
  });

  test('profile.updated for dome reads the dome geometry again', () async {
    const dome = '/api/v1/profile/dome';
    final rig = _Rig()
      ..stored[dome] = {...rigDefaults['dome']!, 'dome_radius_mm': 1000};
    final c = _container(rig);
    await settleLoaded(c, domeSettingsProvider);
    expect(c.read(domeSettingsProvider).value!.domeRadiusMm, 1000);
    rig.stored[dome] = {...rigDefaults['dome']!, 'dome_radius_mm': 1500};

    await applyProfileUpdated(c, 'dome');

    expect(c.read(domeSettingsProvider).value!.domeRadiusMm, 1500);
  });

  test(
    'profile.updated for polar-align reads the routine settings again',
    () async {
      const path = '/api/v1/profile/polar-align';
      final rig = _Rig()
        ..stored[path] = {...rigDefaults['polar-align']!, 'exposure_seconds': 2};
      final c = _container(rig);
      c.listen(polarAlignSettingsProvider, (_, _) {}); // a screen shows them
      await settleLoaded(c, polarAlignSettingsProvider);
      expect(c.read(polarAlignSettingsProvider).value!.exposureSeconds, 2);
      rig.stored[path] = {...rigDefaults['polar-align']!, 'exposure_seconds': 5};

      await applyProfileUpdated(c, 'polar-align');

      expect(c.read(polarAlignSettingsProvider).value!.exposureSeconds, 5);
    },
  );

  test(
    'a polar-align save from this device is not read back over it',
    () async {
      const path = '/api/v1/profile/polar-align';
      final rig = _Rig()
        ..stored[path] = {...rigDefaults['polar-align']!, 'exposure_seconds': 2};
      final c = _container(rig);
      c.listen(polarAlignSettingsProvider, (_, _) {}); // a screen shows them
      await settleLoaded(c, polarAlignSettingsProvider);
      rig.holdWrites = Completer<void>();
      final save = c
          .read(polarAlignSettingsProvider.notifier)
          .saveKnob((s) => s.copyWith(exposureSeconds: 5));
      await _sent(rig, 1); // the answer is still held
      // The daemon wrote it and announced it before answering the PUT.
      rig.stored[path] = {...rigDefaults['polar-align']!, 'exposure_seconds': 5};
      final reads = rig.reads[path];

      final event = applyProfileUpdated(c, 'polar-align');
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(rig.reads[path], reads, reason: 'nothing is read over a save in flight');
      rig.holdWrites!.complete();
      await save;
      await event;

      expect(c.read(polarAlignSettingsProvider).value!.exposureSeconds, 5);
    },
  );

  test('a section no screen holds is ignored without error', () async {
    final c = _container(_Rig());
    await applyProfileUpdated(c, 'something-new');
  });
}
