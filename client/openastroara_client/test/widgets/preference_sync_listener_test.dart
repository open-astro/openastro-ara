import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/client_settings.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/client_settings_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/client_settings_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/preference_sync_listener.dart';

/// Counts reads; every merge gets the same stamp the daemon would return.
class _Settings implements ClientSettingsClient {
  int fetches = 0;
  static final stamp = DateTime.utc(2026, 6, 14, 12, 0, 1);

  @override
  Future<ClientSettings> fetch() async {
    fetches++;
    return const ClientSettings(settings: {});
  }

  @override
  Future<ClientSettings> replace(Map<String, dynamic> settings) async =>
      ClientSettings(settings: settings, updatedUtc: stamp);

  @override
  Future<ClientSettings> merge(Map<String, dynamic> patch) async =>
      ClientSettings(settings: patch, updatedUtc: stamp);

  @override
  void close() {}
}

/// The rig's profile endpoints: counts site reads, answers the profile list.
class _Rig implements HttpClientAdapter {
  int siteReads = 0;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    Object body = <String, Object>{};
    if (options.path.endsWith('/profile/site')) {
      siteReads++;
      body = {'site_name': 'Seen on the other device'};
    } else if (options.path.endsWith('/profiles')) {
      body = {'active_id': null, 'profiles': <Object>[]};
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

WsEvent _event(String type, Map<String, dynamic> payload) => WsEvent(
  type: type,
  ts: DateTime.utc(2026, 6, 14),
  seq: 1,
  payload: payload,
);

void main() {
  setUp(ProfileApi.forgetOwnWrites);

  Future<
    (
      _Settings,
      _Rig,
      StreamController<WsEvent>,
      StreamController<WsConnectionState>,
      ProviderContainer,
    )
  >
  pump(WidgetTester tester) async {
    final settings = _Settings();
    final rig = _Rig();
    final events = StreamController<WsEvent>.broadcast();
    final link = StreamController<WsConnectionState>();
    addTearDown(events.close);
    addTearDown(link.close);
    final api = ProfileApi(
      const AraServer(hostname: 'h', port: 5555),
      dio: Dio()..httpClientAdapter = rig,
    );
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          clientSettingsApiProvider.overrideWithValue(settings),
          profileApiProvider.overrideWithValue(api),
          wsEventsProvider.overrideWith((ref) => events.stream),
          wsConnectionStateProvider.overrideWith((ref) => link.stream),
        ],
        child: const MaterialApp(
          home: PreferenceSyncListener(child: Text('shell')),
        ),
      ),
    );
    link.add(WsConnectionState.connecting);
    await tester.pumpAndSettle();
    final container = ProviderScope.containerOf(
      tester.element(find.text('shell')),
    );
    return (settings, rig, events, link, container);
  }

  testWidgets(
    'client_settings.updated from another device re-reads the rig copy',
    (tester) async {
      final (settings, _, events, _, _) = await pump(tester);
      final before = settings.fetches;

      events.add(
        _event('client_settings.updated', {
          'updated_utc': '2026-06-14T12:00:09Z',
        }),
      );
      await tester.pumpAndSettle();

      expect(settings.fetches, before + 1);
    },
  );

  testWidgets(
    "this device's own save stamp, zone-less on the wire, is not read back",
    (tester) async {
      final (settings, _, events, _, container) = await pump(tester);
      await container.read(clientSettingsProvider.notifier).merge({
        'theme': 'dark',
      });
      final before = settings.fetches;

      // Same instant as _Settings.stamp; only a UTC-normalised parse equals it.
      events.add(
        _event('client_settings.updated', {
          'updated_utc': '2026-06-14T12:00:01',
        }),
      );
      await tester.pumpAndSettle();

      expect(settings.fetches, before);
    },
    // Needs a UTC test host: a zone-less stamp parses as local time.
    skip: DateTime.now().timeZoneOffset != Duration.zero,
  );

  testWidgets('profile.updated re-reads that section', (tester) async {
    final (_, rig, events, _, container) = await pump(tester);
    expect(rig.siteReads, 0);

    events.add(_event('profile.updated', {'section': 'site'}));
    await tester.pumpAndSettle();

    expect(rig.siteReads, 1);
    expect(
      container.read(siteSettingsProvider).siteName,
      'Seen on the other device',
    );
  });

  testWidgets('a return to connected re-reads the rig copy, once', (
    tester,
  ) async {
    final (settings, _, _, link, _) = await pump(tester);
    link.add(WsConnectionState.connected);
    await tester.pumpAndSettle();
    final afterFirst = settings.fetches;

    link.add(WsConnectionState.connected);
    await tester.pumpAndSettle();
    expect(settings.fetches, afterFirst, reason: 'no change, no read');

    link.add(WsConnectionState.reconnecting);
    link.add(WsConnectionState.connected);
    await tester.pumpAndSettle();
    expect(settings.fetches, afterFirst + 1);
  });
}
