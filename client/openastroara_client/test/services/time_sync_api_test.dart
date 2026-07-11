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
}
