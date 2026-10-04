import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_equipment_api.dart';

/// A loopback daemon stand-in: answers every request 202 and records the
/// method + URI, so the real Dio wiring of [GuiderEquipmentApi] (no injectable
/// Dio) is asserted on the wire.
Future<(HttpServer, List<(String, Uri)>)> _loopbackDaemon() async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  final seen = <(String, Uri)>[];
  server.listen((req) async {
    seen.add((req.method, req.uri));
    await req.drain<void>();
    req.response.statusCode = HttpStatus.accepted;
    await req.response.close();
  });
  addTearDown(() => server.close(force: true));
  return (server, seen);
}

void main() {
  test('pushProfile(tuningOnly: true) POSTs the push with ?scope=tuning — '
      'the runtime-safe push that keeps guiding running', () async {
    final (server, seen) = await _loopbackDaemon();
    final api = GuiderEquipmentApi(
        AraServer(hostname: '127.0.0.1', port: server.port));
    addTearDown(api.close);

    await api.pushProfile(tuningOnly: true);

    expect(seen, hasLength(1));
    final (method, uri) = seen.single;
    expect(method, 'POST');
    expect(uri.path, '/api/v1/equipment/guider/profile/push');
    expect(uri.queryParameters, {'scope': 'tuning'});
  });

  test('pushProfile() (full push) sends no scope parameter', () async {
    final (server, seen) = await _loopbackDaemon();
    final api = GuiderEquipmentApi(
        AraServer(hostname: '127.0.0.1', port: server.port));
    addTearDown(api.close);

    await api.pushProfile();

    expect(seen, hasLength(1));
    final (method, uri) = seen.single;
    expect(method, 'POST');
    expect(uri.path, '/api/v1/equipment/guider/profile/push');
    expect(uri.queryParameters, isEmpty,
        reason: 'the full push must not ask for the tuning-only scope');
  });
}
