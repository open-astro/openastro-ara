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
import 'package:openastroara/state/settings/site_settings_state.dart';

/// The rig's profile endpoints, as far as this test needs: the site section
/// (whatever name it currently holds) and the profile list.
class _Rig implements HttpClientAdapter {
  String siteName = 'Backyard';
  int siteReads = 0;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    Object body;
    if (options.path.endsWith('/profile/site')) {
      if (options.method == 'GET') siteReads++;
      body = {'site_name': siteName};
    } else if (options.path.endsWith('/profiles')) {
      body = {'active_id': null, 'profiles': <Object>[]};
    } else {
      body = <String, Object>{};
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

const _server = AraServer(hostname: 'host', port: 5555);

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
  test('profile.updated for a section re-reads it, so an open screen never shows stale values', () async {
    final rig = _Rig()..siteName = 'Seen on the other device';
    final cache = _Cache();
    final c = _container(rig, cache: cache);
    expect(c.read(siteSettingsProvider).siteName, 'Backyard');

    await applyProfileUpdated(c, 'site');

    expect(c.read(siteSettingsProvider).siteName, 'Seen on the other device');
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
      // The write this device just made (the event for it comes back too).
      await c
          .read(profileApiProvider)!
          .putSiteSettings(const SiteSettings(siteName: 'Saved'));
      c
          .read(siteSettingsProvider.notifier)
          .setSiteName('Typing after the save');
      final reads = rig.siteReads;

      await applyProfileUpdated(c, 'site');

      expect(c.read(siteSettingsProvider).siteName, 'Typing after the save');
      expect(rig.siteReads, reads);
    },
  );

  test('a section no screen holds is ignored without error', () async {
    final c = _container(_Rig());
    await applyProfileUpdated(c, 'something-new');
  });
}
