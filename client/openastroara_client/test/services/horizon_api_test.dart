import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/horizon_api.dart';

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

HorizonApi _apiReturning(Object body) {
  final dio = Dio()..httpClientAdapter = _StubAdapter(body);
  return HorizonApi(const AraServer(hostname: 'x', port: 80), dio: dio);
}

Map<String, dynamic> _body() => {
      'at_utc': '2026-06-16T08:00:00Z',
      'horizon_altitude_deg': 20.0,
      'local_sidereal_time_deg': 100.0,
      'zenith': {'ra_deg': 100.0, 'dec_deg': 47.6, 'azimuth_deg': 0.0},
      'points': [
        {'ra_deg': 10.0, 'dec_deg': -5.0, 'azimuth_deg': 0.0},
        {'ra_deg': 20.0, 'dec_deg': -4.0, 'azimuth_deg': 2.0},
      ],
      'cardinals': [
        {'label': 'N', 'ra_deg': 280.0, 'dec_deg': 17.6},
        {'label': 'E', 'ra_deg': 190.0, 'dec_deg': 0.0},
      ],
      'custom_horizon_ignored': false,
    };

void main() {
  group('Horizon.fromJson', () {
    test('parses a full horizon', () {
      final h = Horizon.fromJson(_body());
      expect(h, isNotNull);
      expect(h!.horizonAltitudeDeg, 20.0);
      expect(h.zenith.raDeg, 100.0);
      expect(h.zenith.decDeg, 47.6);
      expect(h.points, hasLength(2));
      expect(h.points.first.raDeg, 10.0);
      expect(h.cardinals.map((c) => c.label), ['N', 'E']);
      expect(h.customHorizonIgnored, isFalse);
    });

    test('reads the custom-horizon-ignored flag', () {
      final h = Horizon.fromJson({..._body(), 'custom_horizon_ignored': true});
      expect(h!.customHorizonIgnored, isTrue);
    });

    test('returns null when the zenith is missing (no usable geometry)', () {
      final body = _body()..remove('zenith');
      expect(Horizon.fromJson(body), isNull);
    });

    test('returns null when the curve is empty', () {
      final body = _body()..['points'] = const <dynamic>[];
      expect(Horizon.fromJson(body), isNull);
    });

    test('skips a malformed point rather than failing the whole parse', () {
      final body = _body()
        ..['points'] = [
          {'ra_deg': 10.0, 'dec_deg': -5.0},
          {'ra_deg': 'bad', 'dec_deg': -4.0},
        ];
      final h = Horizon.fromJson(body);
      expect(h, isNotNull);
      expect(h!.points, hasLength(1));
    });
  });

  group('HorizonApi.fetch', () {
    test('returns the parsed horizon on a 200 object body', () async {
      final h = await _apiReturning(_body()).fetch();
      expect(h, isNotNull);
      expect(h!.points, hasLength(2));
    });

    test('returns null on a 200 whose body is not a horizon object', () async {
      expect(await _apiReturning(const <dynamic>[]).fetch(), isNull);
    });
  });
}
