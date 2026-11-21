import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_advanced_settings.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_advanced_api.dart';
import 'package:openastroara/services/profile_api.dart'
    show SectionVersionMissingException;

/// A loopback daemon stand-in: answers every request 200 and records the
/// method and path, so the real Dio wiring of [GuiderAdvancedApi] (no
/// injectable Dio) is asserted on the wire.
Future<(HttpServer, List<(String, String)>)> _loopbackDaemon() async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  final seen = <(String, String)>[];
  server.listen((req) async {
    seen.add((req.method, req.uri.path));
    await req.drain<void>();
    req.response.statusCode = HttpStatus.ok;
    await req.response.close();
  });
  addTearDown(() => server.close(force: true));
  return (server, seen);
}

void main() {
  test('putProfile with no loaded version sends nothing and says the rig '
      'sent none (ADR 0015)', () async {
    final (server, seen) = await _loopbackDaemon();
    final api = GuiderAdvancedApi(
      AraServer(hostname: '127.0.0.1', port: server.port),
    );
    addTearDown(api.close);

    for (final tag in [null, '']) {
      await expectLater(
        api.putProfile(GuiderAdvancedSettings.empty, ifMatch: tag),
        throwsA(isA<SectionVersionMissingException>()),
      );
    }
    expect(seen, isEmpty, reason: 'no untagged write reaches the rig');
  });
}
