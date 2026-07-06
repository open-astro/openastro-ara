import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_api.dart';

/// Canned HTTP layer: scripts one response per request and records what the
/// API sent, so the §27 wire shapes are tested without a daemon.
class _CannedAdapter implements HttpClientAdapter {
  final int statusCode;
  final Object? body;
  RequestOptions? lastRequest;
  String? lastBody;

  _CannedAdapter(this.statusCode, this.body);

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastRequest = options;
    if (requestStream != null) {
      final bytes = await requestStream.expand<int>((c) => c).toList();
      lastBody = utf8.decode(bytes);
    }
    return ResponseBody.fromString(
      body == null ? '' : jsonEncode(body),
      statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  const server = AraServer(hostname: 'host-a', port: 5555);

  ServerApi apiWith(_CannedAdapter adapter) {
    final dio = Dio(
      BaseOptions(
        baseUrl: server.baseUrl,
        // 4xx must surface as DioException like the production BaseOptions.
        validateStatus: (code) => code != null && code < 400,
      ),
    );
    dio.httpClientAdapter = adapter;
    return ServerApi(server, dio: dio);
  }

  group('ServerApi §27 connect/disconnect', () {
    test(
      'a 200 with a session id is granted, and the body carries hostname + prior id',
      () async {
        final adapter = _CannedAdapter(200, {
          'session_id': 'sid-1',
          'hostname': 'mac.local',
          'connected_at': '2026-07-06T02:00:00Z',
        });
        final claim = await apiWith(
          adapter,
        ).connectClient(hostname: 'mac.local', sessionId: 'sid-old');

        expect(claim.granted, isTrue);
        expect(claim.sessionId, 'sid-1');
        expect(adapter.lastRequest?.path, '/api/v1/server/connect');
        expect(jsonDecode(adapter.lastBody!), {
          'hostname': 'mac.local',
          'session_id': 'sid-old',
        });
      },
    );

    test('a first claim omits session_id from the body', () async {
      final adapter = _CannedAdapter(200, {'session_id': 'sid-1'});
      await apiWith(adapter).connectClient(hostname: 'mac.local');

      expect(jsonDecode(adapter.lastBody!), {'hostname': 'mac.local'});
    });

    test(
      'a malformed 200 without a session id is denied, not granted',
      () async {
        final adapter = _CannedAdapter(200, {'hostname': 'mac.local'});
        final claim = await apiWith(
          adapter,
        ).connectClient(hostname: 'mac.local');

        expect(
          claim.granted,
          isFalse,
          reason: 'an empty session id must never bind X-Ara-Session',
        );
        expect(claim.deniedReason, contains('no session id'));
      },
    );

    test('a 409 maps to denied with the server problem detail', () async {
      final adapter = _CannedAdapter(409, {
        'title': 'Connection rejected',
        'detail': 'Server in use by ipad.local.',
      });
      final claim = await apiWith(adapter).connectClient(hostname: 'mac.local');

      expect(claim.granted, isFalse);
      expect(claim.deniedReason, 'Server in use by ipad.local.');
    });

    test(
      'a 404 (older daemon without §27) throws for the caller to degrade on',
      () {
        final adapter = _CannedAdapter(404, null);
        expect(
          () => apiWith(adapter).connectClient(hostname: 'mac.local'),
          throwsA(isA<DioException>()),
        );
      },
    );

    test('disconnect: 204 → true, 404 (already gone) → false', () async {
      expect(
        await apiWith(_CannedAdapter(204, null)).disconnectClient('sid-1'),
        isTrue,
      );
      expect(
        await apiWith(_CannedAdapter(404, null)).disconnectClient('sid-1'),
        isFalse,
      );
    });
  });
}
