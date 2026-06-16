import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';

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
      expect(o.altitudeDeg, 62.5);
      expect(o.maxAltitudeDeg, 89.0);
    });

    test('missing/wrong-typed required string field → null (skip the row)', () {
      expect(TonightSkyObject.fromJson(body()..remove('id')), isNull);
      expect(TonightSkyObject.fromJson(body()..['name'] = 42), isNull);
    });

    test('a wrong-typed numeric field falls back to 0, not a throw', () {
      final o = TonightSkyObject.fromJson(body()..['altitude_deg'] = 'high');
      expect(o, isNotNull);
      expect(o!.altitudeDeg, 0.0);
    });
  });
}
