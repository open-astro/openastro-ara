import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/logs_api.dart';

/// Stubs Dio's transport with a canned 200 body so `tail()` can be exercised
/// without a live server (the TonightSkyApi test pattern).
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody);
  final Object jsonBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async =>
      ResponseBody.fromString(
        jsonEncode(jsonBody),
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
}

LogsApi _apiReturning(Object body) {
  final dio = Dio()..httpClientAdapter = _StubAdapter(body);
  return LogsApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  test('tail parses an array body into entries', () async {
    final api = _apiReturning([
      {'timestamp': '2026-07-10T04:00:00Z', 'level': 'info', 'message': 'hello'},
      {'timestamp': '2026-07-10T04:00:01Z', 'level': 'warn', 'message': 'careful'},
    ]);
    final entries = await api.tail();
    expect(entries, hasLength(2));
    expect(entries.first.message, 'hello');
  });

  test('tail throws on a non-array 2xx body instead of yielding an empty list', () async {
    // A 2xx error envelope (or a changed wire contract) used to read as "no log
    // entries" — the Support tab showed an empty list with no hint anything broke.
    final api = _apiReturning(const {'error': 'catalog unavailable'});
    await expectLater(api.tail(), throwsA(isA<FormatException>()));
  });

  test('tail drops non-object rows rather than throwing mid-parse', () async {
    final api = _apiReturning([
      {'timestamp': '2026-07-10T04:00:00Z', 'level': 'info', 'message': 'kept'},
      'garbage-row',
      42,
    ]);
    final entries = await api.tail();
    expect(entries, hasLength(1));
    expect(entries.single.message, 'kept');
  });
}
