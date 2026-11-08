import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';

/// A loopback daemon stand-in that answers every request 202 with [body],
/// so the real Dio wiring of [GuiderApi] (no injectable Dio) is asserted.
Future<GuiderApi> _apiAnswering(String body) async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  server.listen((req) async {
    await req.drain<void>();
    req.response
      ..statusCode = HttpStatus.accepted
      ..headers.contentType = ContentType.json
      ..write(body);
    await req.response.close();
  });
  addTearDown(() => server.close(force: true));
  final api = GuiderApi(AraServer(hostname: '127.0.0.1', port: server.port));
  addTearDown(api.close);
  return api;
}

void main() {
  test('startGuiding returns the operation_id of the 202 (#1664)', () async {
    final api = await _apiAnswering('{"operation_id":"x"}');
    expect(await api.startGuiding(), 'x');
  });

  test(
    'startGuiding returns null when the 202 body is not an object',
    () async {
      final api = await _apiAnswering('"accepted"');
      expect(await api.startGuiding(), isNull);
    },
  );
}
