import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_api.dart';

class _JsonAdapter implements HttpClientAdapter {
  _JsonAdapter(this.body);
  final Map<String, Object?> body;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream,
          Future<void>? cancelFuture) async =>
      ResponseBody.fromString(jsonEncode(body), 200,
          headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});
}

Future<ServerInfo> infoFrom(Map<String, Object?> body) {
  final dio = Dio(BaseOptions(baseUrl: 'http://rig:5555'))..httpClientAdapter = _JsonAdapter(body);
  return ServerApi(const AraServer(hostname: 'rig', port: 5555), dio: dio).getInfo();
}

void main() {
  // #1129: getInfo must read the daemon's ServerInfoDto keys (nickname, api);
  // it read name/api_version, so every rig showed as "OpenAstro Ara".
  test('reads the daemon ServerInfoDto shape', () async {
    final info = await infoFrom({
      'server_uuid': '1f0c6e2a-0000-4000-8000-000000000001',
      'nickname': 'openastro',
      'version': '1.0.0.0',
      'api': 'v1',
      'mdns_service': '_openastroara._tcp',
      'tier': 'core',
    });
    expect(info.name, 'openastro');
    expect(info.version, '1.0.0.0');
    expect(info.apiVersion, 'v1');
  });

  test('falls back to the older name / api_version keys', () async {
    final info = await infoFrom({'name': 'old rig', 'version': '0.9', 'api_version': 'v0'});
    expect(info.name, 'old rig');
    expect(info.apiVersion, 'v0');
  });

  test('an empty nickname falls back rather than showing nothing', () async {
    final info = await infoFrom({'nickname': '', 'name': 'fallback'});
    expect(info.name, 'fallback');
  });
}
