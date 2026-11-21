import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/apparent_place.dart';

// Reference apparent places from astropy 6.0.1: ICRS → TETE (true equator,
// true equinox of date, geocentric) at the given UTC instant — the same thing
// the daemon's SOFA `iauAtci13` + `ri − eo` chain calls JNow.
const _cases = [
  // name, ra2000, dec2000, utc, raApp, decApp
  ('M42', 83.82208, -5.39111, '2026-10-04T22:00:00Z', 84.154866, -5.370485),
  ('Polaris', 37.95456, 89.26411, '2026-10-04T22:00:00Z', 47.077181, 89.373951),
  ('AB Cas', 39.38133, 71.30444, '2026-03-01T03:00:00Z', 39.982116, 71.422501),
  ('southern', 201.29824, -43.01899, '2031-07-15T12:00:00Z', 201.768472,
      -43.186398),
  ('origin', 0.0, 0.0, '2000-01-01T12:00:00Z', 359.995493, -0.001960),
];

/// Great-circle separation in arcseconds (haversine).
double _sepArcsec(double ra1, double dec1, double ra2, double dec2) {
  const d2r = math.pi / 180;
  final a = dec1 * d2r, b = dec2 * d2r, dra = (ra1 - ra2) * d2r;
  final h = math.pow(math.sin((a - b) / 2), 2) +
      math.cos(a) * math.cos(b) * math.pow(math.sin(dra / 2), 2);
  return 2 * math.asin(math.sqrt(h)) / d2r * 3600;
}

void main() {
  group('j2000ToApparent', () {
    for (final c in _cases) {
      test('${c.$1} lands within 0.5″ of astropy', () {
        final out = j2000ToApparent(c.$2, c.$3, atUtc: DateTime.parse(c.$4));
        final sep = _sepArcsec(out.raDeg, out.decDeg, c.$5, c.$6);
        expect(sep, lessThan(0.5), reason: 'separation $sep″');
      });
    }
  });

  group('apparentToJ2000', () {
    for (final c in _cases) {
      test('${c.$1} round-trips back to J2000', () {
        final at = DateTime.parse(c.$4);
        final back = apparentToJ2000(c.$5, c.$6, atUtc: at);
        final sep = _sepArcsec(back.raDeg, back.decDeg, c.$2, c.$3);
        expect(sep, lessThan(0.5), reason: 'separation $sep″');
      });
    }

    test('is the exact inverse of the forward chain', () {
      final at = DateTime.utc(2027, 5, 20, 1);
      final app = j2000ToApparent(300.1, -20.2, atUtc: at);
      final back = apparentToJ2000(app.raDeg, app.decDeg, atUtc: at);
      expect(back.raDeg, closeTo(300.1, 1e-7));
      expect(back.decDeg, closeTo(-20.2, 1e-7));
    });

    test('a JNow pole maps back onto the JNow pole', () {
      final at = DateTime.utc(2026, 10, 4, 22);
      for (final dec in [90.0, -90.0, 89.99, -89.99]) {
        final back = apparentToJ2000(37.5, dec, atUtc: at);
        final app = j2000ToApparent(back.raDeg, back.decDeg, atUtc: at);
        final sep = _sepArcsec(app.raDeg, app.decDeg, 37.5, dec);
        expect(sep, lessThan(1e-3), reason: 'Dec $dec: separation $sep″');
      }
    });

    test('RA stays in [0, 360) across the wrap', () {
      final at = DateTime.utc(2026, 10, 4, 22);
      final back = apparentToJ2000(0.2, 10, atUtc: at);
      expect(back.raDeg, inInclusiveRange(0, 360));
      expect(back.raDeg, greaterThan(359));
    });
  });
}
