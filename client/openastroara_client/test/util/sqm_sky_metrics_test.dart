import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/weather_status.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

void main() {
  group('sqmDerived', () {
    test('matches the WeeWX SQM extension published values for a live reading',
        () {
      // Reference values captured from Joey's GNTO feed (2026-08-03, SQM
      // 20.81): the panel's derived rows must agree with what the station's
      // own website publishes for the same reading.
      const sqm = 20.809999999999956;
      final d = sqmDerived(sqm);
      expect(d.nelm, closeTo(6.005897282222462, 1e-9));
      expect(d.luminanceCdM2, closeTo(0.0005121813440810642, 1e-12));
      expect(d.nsu, closeTo(2.0701413487910485, 1e-9));
    });

    test('clamps garbage readings instead of overflowing', () {
      // A bogus 0 reading must not propagate Infinity into the display.
      final zero = sqmDerived(0);
      expect(zero.nelm.isFinite, isTrue);
      expect(zero.luminanceCdM2.isFinite, isTrue);
      expect(zero.nsu.isFinite, isTrue);
      final huge = sqmDerived(99);
      expect(huge.nelm.isFinite, isTrue);
      // Darker sky → fainter limiting magnitude is HIGHER; monotonic check.
      expect(sqmDerived(21.5).nelm, greaterThan(sqmDerived(18.0).nelm));
    });
  });

  group('sunMoonAltitudeDeg', () {
    test('agrees with the WeeWX feed ephemeris within the low-precision '
        'model tolerance', () {
      // Same GNTO capture: solarAlt -28.198, lunarAlt 5.998 at the SQM
      // timestamp (2026-08-03 04:53:14 UTC) for site 34.53N, -106.85E.
      final at = DateTime.fromMillisecondsSinceEpoch(1785732794102, isUtc: true);
      final alt = sunMoonAltitudeDeg(at, 34.53, -106.85);
      expect(alt.sunAltDeg, closeTo(-28.198, 2.0));
      expect(alt.moonAltDeg, closeTo(5.998, 2.0));
    });

    test('sun is below the horizon at local solar midnight and above at noon',
        () {
      // Greenwich equator: solar noon ≈ 12:00 UTC, midnight ≈ 00:00 UTC.
      final noon = sunMoonAltitudeDeg(
          DateTime.utc(2026, 8, 3, 12), 0, 0);
      final midnight = sunMoonAltitudeDeg(
          DateTime.utc(2026, 8, 3, 0), 0, 0);
      expect(noon.sunAltDeg, greaterThan(50));
      expect(midnight.sunAltDeg, lessThan(-50));
    });
  });

  group('moonPhase', () {
    test('fraction stays in [0,1] and the name matches the fraction band', () {
      for (var day = 0; day < 30; day++) {
        final p = moonPhase(DateTime.utc(2026, 8, 1).add(Duration(days: day)));
        expect(p.illuminatedFraction, inInclusiveRange(0.0, 1.0));
        if (p.illuminatedFraction < 0.03) {
          expect(p.phaseName, 'new moon');
        } else if (p.illuminatedFraction > 0.97) {
          expect(p.phaseName, 'full moon');
        }
      }
    });
  });

  group('WeatherStatus.fromJson', () {
    test('parses the new SQM fields and tolerates their absence', () {
      final withSqm = WeatherStatus.fromJson(const {
        'device_id': 'WEEWX_OC_0',
        'name': 'WeeWX ObservingConditions',
        'state': 'connected',
        'sky_quality_mag_arcsec2': 20.81,
        'sky_temperature_c': 4.56,
      });
      expect(withSqm.skyQualityMagArcsec2, closeTo(20.81, 1e-9));
      expect(withSqm.skyTemperatureC, closeTo(4.56, 1e-9));

      final without = WeatherStatus.fromJson(const {
        'device_id': 'X',
        'name': 'Y',
        'state': 'connected',
      });
      expect(without.skyQualityMagArcsec2, isNull);
      expect(without.skyTemperatureC, isNull);
    });
  });
}
