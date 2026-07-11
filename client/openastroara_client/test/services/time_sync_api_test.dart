import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/time_sync_api.dart';

/// Stubs Dio's transport (the TonightSkyApi test pattern) and records the last
/// request so the push body can be asserted.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody);
  final Object jsonBody;
  RequestOptions? lastRequest;
  Object? lastBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastRequest = options;
    lastBody = options.data;
    return ResponseBody.fromString(
      jsonEncode(jsonBody),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  test('getState parses the §31.3 wire shape', () async {
    final adapter = _StubAdapter(const {
      'synced': true,
      'source': 'client',
      'trust': 'medium',
      'current_time_utc': '2026-07-11T07:00:00Z',
    });
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    final state = await api.getState();
    expect(state.synced, isTrue);
    expect(state.source, 'client');
    expect(state.trust, 'medium');
  });

  test('getState degrades a malformed body to unsynced fields, not a crash', () async {
    final adapter = _StubAdapter(const {'synced': 'yes-ish', 'source': 42});
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    final state = await api.getState();
    expect(state.synced, isFalse, reason: 'wrong-typed synced reads as false → the client pushes');
    expect(state.source, 'none');
  });

  test('getState parses the full status fields the settings section renders', () async {
    final adapter = _StubAdapter(const {
      'synced': true,
      'source': 'gps-internal',
      'trust': 'high',
      'system_time_offset_seconds': -1.5,
      'location': {'lat': 30.27, 'lng': -97.74, 'alt': 165.0},
      'internet_available_on_pi': false,
      'internal_gps_available': true,
      'synced_at_utc': '2026-07-11T07:00:00Z',
    });
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    final state = await api.getState();
    expect(state.systemTimeOffsetSeconds, -1.5);
    expect(state.location!.lat, 30.27);
    expect(state.location!.lng, -97.74);
    expect(state.location!.alt, 165.0);
    expect(state.internalGpsAvailable, isTrue);
    expect(state.internetAvailableOnPi, isFalse);
    expect(state.syncedAtUtc, DateTime.utc(2026, 7, 11, 7));
  });

  test('getState tolerates a null-altitude location (RMC-only GPS fix)', () async {
    final adapter = _StubAdapter(const {
      'synced': true,
      'location': {'lat': 30.0, 'lng': -97.0},
    });
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    final state = await api.getState();
    expect(state.location, isNotNull);
    expect(state.location!.alt, isNull, reason: 'unknown altitude stays null, never 0');
  });

  test('pushClientTime sends a medium-trust client sync with an ISO instant', () async {
    final adapter = _StubAdapter(const {'location_updated': false});
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);

    await api.pushClientTime(DateTime.utc(2026, 7, 11, 7, 0, 0));

    expect(adapter.lastRequest!.method, 'POST');
    expect(adapter.lastRequest!.path, '/api/v1/server/time-sync');
    final body = adapter.lastBody as Map<String, dynamic>;
    expect(body['source'], 'client');
    expect(body['trust'], 'medium');
    expect(body['time_utc'], '2026-07-11T07:00:00.000Z');
  });

  test('pushManual sends a manual sync with location and parses the result', () async {
    final adapter = _StubAdapter(const {
      'location_updated': true,
      'clock_set': true,
    });
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);

    final result = await api.pushManual(
      timeUtc: DateTime.utc(2026, 7, 11, 4, 30),
      lat: 30.27,
      lng: -97.74,
      alt: 165.0,
    );

    final body = adapter.lastBody as Map<String, dynamic>;
    expect(body['source'], 'manual');
    expect(body.containsKey('trust'), isFalse,
        reason: 'the server clamps manual to low regardless — nothing to claim');
    expect(body['time_utc'], '2026-07-11T04:30:00.000Z');
    final loc = body['location'] as Map<String, dynamic>;
    expect(loc['lat'], 30.27);
    expect(loc['lng'], -97.74);
    expect(loc['alt'], 165.0);
    expect(result.locationUpdated, isTrue);
    expect(result.clockSet, isTrue);
  });

  test('pushManual without a position omits location entirely', () async {
    final adapter = _StubAdapter(const {
      'location_updated': false,
      'clock_set': false,
    });
    final api = TimeSyncApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);

    final result = await api.pushManual(timeUtc: DateTime.utc(2026, 7, 11));

    final body = adapter.lastBody as Map<String, dynamic>;
    expect(body.containsKey('location'), isFalse);
    expect(result.clockSet, isFalse,
        reason: 'clock_set false surfaces the offset-tracking message');
  });
}
