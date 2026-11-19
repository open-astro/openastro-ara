import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/polar_align_api.dart';

/// #2048 — the routine's client against a stub daemon that records each
/// request's receive timeout.
class _Daemon implements HttpClientAdapter {
  final seen = <(String, Duration?)>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    seen.add(('${options.method} ${options.path}', options.receiveTimeout));
    return ResponseBody.fromString('', 202);
  }
}

void main() {
  test('Stop and Done wait for the daemon to confirm the mount stopped, past the 5 s default (#2048)', () async {
    final d = _Daemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    await api.stop();
    await api.complete();
    await api.next();
    expect(d.seen, [
      ('POST /api/v1/equipment/polaralign/stop', const Duration(seconds: 30)),
      ('POST /api/v1/equipment/polaralign/complete', const Duration(seconds: 30)),
      ('POST /api/v1/equipment/polaralign/next', null),
    ]);
  });
}
