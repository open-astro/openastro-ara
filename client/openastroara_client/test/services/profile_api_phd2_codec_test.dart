import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody);
  final Object jsonBody;
  Object? lastBody;
  @override
  void close({bool force = false}) {}
  @override
  Future<ResponseBody> fetch(RequestOptions options,
      Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    lastBody = options.data;
    return ResponseBody.fromString(jsonEncode(jsonBody), 200,
        headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});
  }
}

void main() {
  // #1308 — the guide camera's Alpaca UniqueID must survive the wire both
  // ways; a misspelled key would store "" on the daemon and silently skip
  // the pin check.
  test('guider_camera_alpaca_unique_id is parsed from GET and sent on PUT', () async {
    final adapter = _StubAdapter(const {
      'host': 'rc91.lan',
      'port': 4400,
      'phd2_profile': 'main',
      'guider_camera': 'Alpaca Camera [127.0.0.1:6800/1]',
      'guider_camera_alpaca_unique_id': 'ZWO_0',
    });
    final api = ProfileApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    final got = await api.getPhd2Settings();
    expect(got.guiderCameraAlpacaUniqueId, 'ZWO_0');

    await api.putPhd2Settings(const Phd2Settings(
        guiderCamera: 'Alpaca Camera [127.0.0.1:6800/1]',
        guiderCameraAlpacaUniqueId: 'ZWO_SN_abc'));
    final sent = adapter.lastBody as Map<String, dynamic>;
    expect(sent['guider_camera_alpaca_unique_id'], 'ZWO_SN_abc');
  });

  test('an older daemon without the key reads as unset', () async {
    final api = ProfileApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = _StubAdapter(const {'host': 'h', 'port': 4400}));
    expect((await api.getPhd2Settings()).guiderCameraAlpacaUniqueId, '');
  });
}
