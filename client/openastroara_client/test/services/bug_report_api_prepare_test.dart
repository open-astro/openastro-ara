import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/bug_report_api.dart';

/// Captures the prepare request and answers a canned preparation, so the §54.3
/// mode's wire key can be asserted without a live daemon.
class _CapturingAdapter implements HttpClientAdapter {
  RequestOptions? last;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    last = options;
    return ResponseBody.fromString(
      jsonEncode({
        'preparation_id': 'p-1',
        'status': 'ready',
        'estimated_size_bytes': 4096,
        'sharing_mode': 'redacted',
      }),
      202,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  test('prepare sends the sharing mode in the body and reads it back', () async {
    final adapter = _CapturingAdapter();
    final api = BugReportApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = adapter);

    final prep = await api.prepare(mode: 'redacted');

    expect(adapter.last!.path, '/api/v1/bugreport/prepare');
    expect(adapter.last!.data, {'mode': 'redacted'});
    expect(prep.sharingMode, 'redacted');
  });

  test('prepare defaults to full', () async {
    final adapter = _CapturingAdapter();
    final api = BugReportApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = adapter);
    await api.prepare();
    expect(adapter.last!.data, {'mode': 'full'});
  });
}
