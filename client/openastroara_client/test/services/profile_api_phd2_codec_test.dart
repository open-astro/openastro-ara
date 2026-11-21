import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import '../support/fake_section_rig.dart';
import '../support/settings_values.dart';

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
    // A PUT is echoed whole, as the daemon does.
    final reply = options.method == 'PUT'
        ? {...rigDefaults['phd2']!, ...options.data as Map<String, dynamic>}
        : jsonBody;
    return ResponseBody.fromString(jsonEncode(reply), 200,
        headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});
  }
}

void main() {
  // #1308 — the guide camera's Alpaca UniqueID must survive the wire both
  // ways; a misspelled key would store "" on the daemon and silently skip
  // the pin check.
  test('guider_camera_alpaca_unique_id is parsed from GET and sent on PUT', () async {
    final adapter = _StubAdapter({
      ...rigDefaults['phd2']!,
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

    await api.putPhd2Section(
        const TestPhd2Settings(
            guiderCamera: 'Alpaca Camera [127.0.0.1:6800/1]',
            guiderCameraAlpacaUniqueId: 'ZWO_SN_abc'),
        ifMatch: '"t"');
    final sent = adapter.lastBody as Map<String, dynamic>;
    expect(sent['guider_camera_alpaca_unique_id'], 'ZWO_SN_abc');
  });

  test('phd2_profile_id: a picked guider profile round-trips, Automatic (0) goes out as null', () async {
    final adapter = _StubAdapter({
      ...rigDefaults['phd2']!,
      'host': 'h',
      'phd2_profile_id': 3,
    });
    final api = ProfileApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = adapter);
    expect((await api.getPhd2Settings()).guiderProfileId, 3);

    await api.putPhd2Section(const TestPhd2Settings(guiderProfileId: 5), ifMatch: '"t"');
    expect((adapter.lastBody as Map<String, dynamic>)['phd2_profile_id'], 5);
    await api.putPhd2Section(const TestPhd2Settings(), ifMatch: '"t"');
    expect((adapter.lastBody as Map<String, dynamic>)['phd2_profile_id'], isNull,
        reason: 'Automatic = the guider profile named after the Ara profile');
  });
}
