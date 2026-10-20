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

/// Like [_StubAdapter] but keeps the last request so the wire body can be asserted.
class _CapturingAdapter extends _StubAdapter {
  _CapturingAdapter() : super(const <Object>[]);
  RequestOptions? last;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) {
    last = options;
    return super.fetch(options, requestStream, cancelFuture);
  }
}

LogsApi _apiReturning(Object body) {
  final dio = Dio()..httpClientAdapter = _StubAdapter(body);
  return LogsApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  test('tail sends the before cursor as a UTC ISO-8601 instant, and omits it when null', () async {
    // #1185 — the page-back cursor's key and format are the wire contract with LogService.
    final adapter = _CapturingAdapter();
    final api = LogsApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = adapter);

    await api.tail(maxLines: 50, before: DateTime(2026, 6, 20, 12, 30, 0, 0, 500).toUtc());
    final body = adapter.last!.data as Map<String, dynamic>;
    expect(adapter.last!.path, '/api/v1/server/logs/tail');
    expect(body['max_lines'], 50);
    expect(body['before'], DateTime(2026, 6, 20, 12, 30, 0, 0, 500).toUtc().toIso8601String());
    expect((body['before'] as String).endsWith('Z'), isTrue, reason: 'always UTC');

    await api.tail();
    expect((adapter.last!.data as Map<String, dynamic>).containsKey('before'), isFalse);
  });

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
