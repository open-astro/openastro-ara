import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/data_manager_api.dart';

/// Stubs Dio's transport with a canned status code so `delete()`'s status
/// mapping (204 freed / 404 already-gone / 409 blocked) can be exercised
/// without a live daemon.
class _StatusAdapter implements HttpClientAdapter {
  _StatusAdapter(this.status);
  final int status;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    return ResponseBody.fromString('', status);
  }
}

DataManagerApi _api(int status) {
  final dio = Dio()..httpClientAdapter = _StatusAdapter(status);
  return DataManagerApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  test('204 → true (files freed)', () async {
    expect(await _api(204).delete('hyg-stars'), isTrue);
  });

  test('404 → false (already gone, idempotent — not an error)', () async {
    expect(await _api(404).delete('hyg-stars'), isFalse);
  });

  test('409 → PackageDeleteBlockedException (files still on disk, retry)',
      () async {
    // The daemon's Blocked outcome must never read as "removed": neither true
    // nor false — a typed throw whose message is fit for the modal's SnackBar.
    await expectLater(
      _api(409).delete('hyg-stars'),
      throwsA(isA<PackageDeleteBlockedException>()),
    );
    expect(
      const PackageDeleteBlockedException().toString(),
      contains('in use or protected'),
    );
  });

  test('an unexpected status still propagates as a DioException', () async {
    await expectLater(
      _api(500).delete('hyg-stars'),
      throwsA(isA<DioException>()),
    );
  });
}
