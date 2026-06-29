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
          'size_maj_arcmin': 190.0,
          'size_min_arcmin': 60.0,
          'pos_angle_deg': 35.0,
          'surface_brightness': 13.5,
          'window_start_utc': '2026-06-29T20:14:00Z',
          'window_end_utc': '2026-06-30T04:32:00Z',
          'transit_utc': '2026-06-30T01:10:00Z',
          'integration_hours': 6.3,
          'remaining_hours': 3.2,
          'framing': 'good',
          'score': 88.0,
          'score_reasons': ['fills the frame (+35)', '6 h dark window (+25)'],
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

    test('parses the §36.8 planner fields', () {
      final o = TonightSkyObject.fromJson(body())!;
      expect(o.sizeMajArcmin, 190.0);
      expect(o.sizeMinArcmin, 60.0);
      expect(o.posAngleDeg, 35.0);
      expect(o.surfaceBrightness, 13.5);
      // Wire instants are UTC and normalised to UTC on parse.
      expect(o.windowStartUtc, DateTime.utc(2026, 6, 29, 20, 14));
      expect(o.windowStartUtc!.isUtc, isTrue);
      expect(o.windowEndUtc, DateTime.utc(2026, 6, 30, 4, 32));
      expect(o.transitUtc, DateTime.utc(2026, 6, 30, 1, 10));
      expect(o.integrationHours, 6.3);
      expect(o.remainingHours, 3.2);
      expect(o.framing, TonightFraming.good);
      expect(o.score, 88.0);
      expect(o.scoreReasons, [
        'fills the frame (+35)',
        '6 h dark window (+25)',
      ]);
    });

    test('framing enum maps each wire value, unknown for anything else', () {
      TonightFraming f(Object? v) =>
          TonightSkyObject.fromJson(body()..['framing'] = v)!.framing;
      expect(f('toosmall'), TonightFraming.tooSmall);
      expect(f('good'), TonightFraming.good);
      expect(f('toobig'), TonightFraming.tooBig);
      expect(f('unknown'), TonightFraming.unknown);
      expect(f('TOO_BIG'), TonightFraming.unknown); // unexpected casing/shape
      expect(f(null), TonightFraming.unknown);
      expect(f(42), TonightFraming.unknown);
    });

    test('a suffix-less timestamp is read as UTC wall-clock, not shifted', () {
      // A naive (no Z/offset) instant must be treated as UTC as-is, not run through
      // .toUtc() (which would shift it by the client's local offset). 01:10 stays 01:10.
      final o = TonightSkyObject.fromJson(
          body()..['transit_utc'] = '2026-06-30T01:10:00')!;
      expect(o.transitUtc!.isUtc, isTrue);
      expect(o.transitUtc, DateTime.utc(2026, 6, 30, 1, 10));
    });

    test('§36.8 fields tolerate missing/wrong types without throwing', () {
      // A pre-§36.8 server omits all of them — the required fields still parse.
      final bare = TonightSkyObject.fromJson(body()
        ..remove('size_maj_arcmin')
        ..remove('window_start_utc')
        ..remove('window_end_utc')
        ..remove('transit_utc')
        ..remove('integration_hours')
        ..remove('remaining_hours')
        ..remove('framing')
        ..remove('score')
        ..remove('score_reasons'));
      expect(bare, isNotNull);
      expect(bare!.sizeMajArcmin, isNull);
      expect(bare.windowStartUtc, isNull);
      expect(bare.transitUtc, isNull);
      expect(bare.integrationHours, 0); // default, never null
      expect(bare.remainingHours, 0);
      expect(bare.framing, TonightFraming.unknown);
      expect(bare.score, isNull); // omitted → null, not a misleading 0
      expect(bare.scoreReasons, isNull);

      // Wrong-typed values fall back to null/default rather than crashing.
      final junk = TonightSkyObject.fromJson(body()
        ..['size_maj_arcmin'] = 'big'
        ..['window_start_utc'] = 'not-a-date'
        ..['integration_hours'] = 'lots'
        ..['score'] = 'high'
        ..['score_reasons'] = 'one reason'); // not a list
      expect(junk, isNotNull);
      expect(junk!.sizeMajArcmin, isNull);
      expect(junk.windowStartUtc, isNull);
      expect(junk.integrationHours, 0);
      expect(junk.score, isNull); // wrong-typed → null, not 0
      expect(junk.scoreReasons, isNull);
    });

    test('score_reasons keeps only string entries; empty/all-junk → null', () {
      expect(
        TonightSkyObject.fromJson(body()..['score_reasons'] = ['a', 2, 'b'])!
            .scoreReasons,
        ['a', 'b'],
      );
      expect(
        TonightSkyObject.fromJson(body()..['score_reasons'] = [])!.scoreReasons,
        isNull,
      );
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
