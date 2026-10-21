import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/library_api.dart';

/// #1437 — bulk delete asks for the files to go too by default: an older
/// daemon still honours `delete_from_disk: false`, and its startup scan
/// brings a catalog-only delete back.
class _Daemon implements HttpClientAdapter {
  RequestOptions? seen;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    seen = options;
    return ResponseBody.fromString(
      jsonEncode({'operation_id': 'x'}),
      202,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  test('bulkDelete without the flag deletes the files too', () async {
    final daemon = _Daemon();
    final api = LibraryApi(
      const AraServer(hostname: 'x', port: 80),
      dio: Dio()..httpClientAdapter = daemon,
    );

    await api.bulkDelete(['a', 'b']);

    expect(daemon.seen!.path, endsWith('/api/v1/frames/bulk/delete'));
    expect(daemon.seen!.data, {
      'frame_ids': ['a', 'b'],
      'delete_from_disk': true,
    });
  });
}
