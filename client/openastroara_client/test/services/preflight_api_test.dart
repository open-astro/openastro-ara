import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/preflight_api.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.statusCode, this.body);
  final int statusCode;
  final Object? body;
  String? lastPath;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastPath = options.path;
    return ResponseBody.fromString(
      jsonEncode(body),
      statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  final server = AraServer(hostname: '127.0.0.1', port: 5000);

  PreflightApi api(_StubAdapter a) => PreflightApi(
    server,
    dio: Dio(BaseOptions(baseUrl: server.baseUrl))..httpClientAdapter = a,
  );

  test('#1138 fetchToday reads /api/v1/preflight/today', () async {
    final a = _StubAdapter(200, {
      'generated_utc': '2026-07-18T04:00:00Z',
      'items': [
        {
          'key': 'mount',
          'status': 'fail',
          'title': 'Mount safety',
          'detail': 'Mount is parked.',
        },
      ],
      'all_good': false,
    });
    final c = await api(a).fetchToday();
    expect(a.lastPath, '/api/v1/preflight/today');
    expect(c!.items.single.status, 'fail');
    expect(c.allGood, isFalse);
  });

  test(
    '#1138 a daemon predating the route (404) answers null, not an error',
    () async {
      expect(
        await api(_StubAdapter(404, {'title': 'Not Found'})).fetchToday(),
        isNull,
      );
    },
  );

  test('#1138 a server error surfaces as a DioException', () async {
    expect(
      api(_StubAdapter(500, {'title': 'boom'})).fetchToday(),
      throwsA(isA<DioException>()),
    );
  });
}
