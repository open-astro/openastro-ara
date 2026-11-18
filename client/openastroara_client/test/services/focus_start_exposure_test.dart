import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/bahtinov_focus_api.dart';
import 'package:openastroara/services/guide_focus_api.dart';

/// A loopback daemon stand-in that records each request's JSON body.
Future<(AraServer, List<Map<String, dynamic>>)> _daemon() async {
  final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
  final bodies = <Map<String, dynamic>>[];
  server.listen((req) async {
    final text = await utf8.decoder.bind(req).join();
    bodies.add(jsonDecode(text) as Map<String, dynamic>);
    req.response.statusCode = HttpStatus.accepted;
    await req.response.close();
  });
  addTearDown(() => server.close(force: true));
  return (AraServer(hostname: '127.0.0.1', port: server.port), bodies);
}

void main() {
  test('Bahtinov start sends no exposure of its own (#1544)', () async {
    final (server, bodies) = await _daemon();
    final api = BahtinovFocusApi(server);
    addTearDown(api.close);
    await api.start();
    await api.start(exposureSec: 2);
    expect(bodies, [
      <String, dynamic>{},
      {'exposure_sec': 2.0},
    ]);
  });

  test('guide focus start sends no exposure of its own (#1544)', () async {
    final (server, bodies) = await _daemon();
    final api = GuideFocusApi(server);
    addTearDown(api.close);
    await api.start();
    await api.start(exposureSec: 3, binning: 2);
    expect(bodies, [
      <String, dynamic>{},
      {'exposure_sec': 3.0, 'binning': 2},
    ]);
  });
}
