import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/tonight_sky_api.dart';

/// Stubs Dio's transport with a canned 200 body so `fetch()` can be exercised
/// without a live server.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody);
  final Object jsonBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    return ResponseBody.fromString(
      jsonEncode(jsonBody),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

TonightSkyApi _apiReturning(Object body) {
  final dio = Dio()..httpClientAdapter = _StubAdapter(body);
  return TonightSkyApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

void main() {
  group('TonightSkyObject.fromJson', () {
    Map<String, dynamic> body() => {
          'id': 'M31',
          'name': 'Andromeda Galaxy',
          'type': 'galaxy',
          'magnitude': 3.4,
          'ra_deg': 10.685,
          'dec_deg': 41.269,
          'altitude_deg': 62.5,
          'max_altitude_deg': 89.0,
        };

    test('parses a full object', () {
      final o = TonightSkyObject.fromJson(body());
      expect(o, isNotNull);
      expect(o!.id, 'M31');
      expect(o.name, 'Andromeda Galaxy');
      expect(o.type, 'galaxy');
      expect(o.magnitude, 3.4);
      expect(o.raDeg, 10.685);
      expect(o.decDeg, 41.269);
      expect(o.altitudeDeg, 62.5);
      expect(o.maxAltitudeDeg, 89.0);
    });

    test('missing/wrong-typed required string field → null (skip the row)', () {
      expect(TonightSkyObject.fromJson(body()..remove('id')), isNull);
      expect(TonightSkyObject.fromJson(body()..['name'] = 42), isNull);
    });

    test('missing/wrong-typed ra_deg or dec_deg → null (no (0,0) phantom)', () {
      expect(TonightSkyObject.fromJson(body()..remove('ra_deg')), isNull);
      expect(TonightSkyObject.fromJson(body()..['dec_deg'] = 'north'), isNull);
    });

    test('a missing/wrong-typed magnitude is null, not the real value 0.0', () {
      expect(TonightSkyObject.fromJson(body()..remove('magnitude'))!.magnitude, isNull);
      expect(TonightSkyObject.fromJson(body()..['magnitude'] = 'bright')!.magnitude, isNull);
    });

    test('a genuine magnitude of 0.0 is preserved, not treated as missing', () {
      expect(TonightSkyObject.fromJson(body()..['magnitude'] = 0.0)!.magnitude, 0.0);
    });

    test('missing/wrong-typed altitude → null (no misleading 0° on the horizon)', () {
      expect(TonightSkyObject.fromJson(body()..['altitude_deg'] = 'high'), isNull);
      expect(TonightSkyObject.fromJson(body()..remove('altitude_deg')), isNull);
      expect(TonightSkyObject.fromJson(body()..['max_altitude_deg'] = 'high'), isNull);
    });
  });

  group('TonightSkyApi.fetch', () {
    Map<String, dynamic> row(String id) => {
          'id': id,
          'name': id,
          'type': 'galaxy',
          'magnitude': 5.0,
          'ra_deg': 1.0,
          'dec_deg': 2.0,
          'altitude_deg': 50.0,
          'max_altitude_deg': 80.0,
        };

    test('parses a JSON array, dropping malformed rows', () async {
      final api = _apiReturning([
        row('M31'),
        row('M42')..remove('ra_deg'), // malformed → skipped
        row('M81'),
      ]);
      final out = await api.fetch();
      expect(out.map((o) => o.id), ['M31', 'M81']);
    });

    test('a non-array 200 body yields an empty list, not a throw', () async {
      final api = _apiReturning({'error': 'no site configured'});
      expect(await api.fetch(), isEmpty);
    });
  });
}
