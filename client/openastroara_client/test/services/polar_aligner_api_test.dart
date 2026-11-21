import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/polar_align_api.dart';

/// #1971 — the Polar Aligner client against a stub daemon: the routes it
/// calls, the DTO it parses and 404 → a daemon without the Polar Aligner.
class _Daemon implements HttpClientAdapter {
  _Daemon(this.status, [this.body = const {}]);
  final int status;
  final Object body;
  final seen = <String>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    seen.add('${options.method} ${options.path}');
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

PolarAlignerApi _api(_Daemon d) => PolarAlignerApi(
  const AraServer(hostname: 'x', port: 80),
  dio: Dio()..httpClientAdapter = d,
);

void main() {
  test('PolarAlignerApi reads the device and sends the four commands to their routes', () async {
    final d = _Daemon(200, {
      'present': true,
      'name': 'Polar base',
      'drivable': true,
      'moving': false,
    });
    final api = _api(d);
    final device = await api.get();
    expect((device!.present, device.name), (true, 'Polar base'));
    await api.stop();
    await api.home();
    await api.stopInRoutine();
    await api.resumeInRoutine();
    expect(d.seen, [
      'GET /api/v1/equipment/polaraligner',
      'POST /api/v1/equipment/polaraligner/stop',
      'POST /api/v1/equipment/polaraligner/home',
      'POST /api/v1/equipment/polaralign/correction/stop',
      'POST /api/v1/equipment/polaralign/correction/resume',
    ]);
  });

  test('PolarAlignerApi: a daemon without the route reads as none; a refusal is thrown for the panel to word', () async {
    expect(await _api(_Daemon(404)).get(), isNull);
    expect(
      () =>
          _api(_Daemon(409, {'detail': 'the Polar Aligner is not stopped'}))
              .resumeInRoutine(),
      throwsA(isA<DioException>()),
    );
  });
}
