import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/input_coordinates.dart';

void main() {
  group('inputCoordinatesFromDeg', () {
    test('carries the InputCoordinates \$type', () {
      final c = inputCoordinatesFromDeg(0, 0);
      expect(c[r'$type'], inputCoordinatesType);
    });

    test('0°/0° → all-zero HMS/DMS, positive dec', () {
      final c = inputCoordinatesFromDeg(0, 0);
      expect(c['RAHours'], 0);
      expect(c['RAMinutes'], 0);
      expect(c['RASeconds'], closeTo(0, 1e-9));
      expect(c['NegativeDec'], false);
      expect(c['DecDegrees'], 0);
      expect(c['DecMinutes'], 0);
      expect(c['DecSeconds'], closeTo(0, 1e-9));
    });

    test('180° RA → 12h exactly', () {
      final c = inputCoordinatesFromDeg(180, 0);
      expect(c['RAHours'], 12);
      expect(c['RAMinutes'], 0);
      expect(c['RASeconds'], closeTo(0, 1e-9));
    });

    test('RA wraps: 360° folds to 0h', () {
      final c = inputCoordinatesFromDeg(360, 0);
      expect(c['RAHours'], 0);
      expect(c['RAMinutes'], 0);
      expect(c['RASeconds'], closeTo(0, 1e-9));
    });

    test('a small negative RA wraps near 24h, never negative', () {
      final c = inputCoordinatesFromDeg(-15, 0); // -15° → 345° → 23h
      expect(c['RAHours'], 23);
      expect(c['RAMinutes'], 0);
      expect(c['RASeconds'], closeTo(0, 1e-9));
    });

    test('decomposes RA minutes/seconds (M31 ≈ 00h42m44s)', () {
      // 10.6847° / 15 = 0.712313h → 0h 42m 44.3s
      final c = inputCoordinatesFromDeg(10.6847, 41.269);
      expect(c['RAHours'], 0);
      expect(c['RAMinutes'], 42);
      expect(c['RASeconds'], closeTo(44.3, 0.2));
    });

    test('negative dec sets the flag and keeps magnitude unsigned', () {
      final c = inputCoordinatesFromDeg(0, -5.5);
      expect(c['NegativeDec'], true);
      expect(c['DecDegrees'], 5);
      expect(c['DecMinutes'], 30);
      expect(c['DecSeconds'], closeTo(0, 1e-6));
    });

    test('positive dec decomposes D/M/S (M31 ≈ +41°16′08″)', () {
      final c = inputCoordinatesFromDeg(10.6847, 41.269);
      expect(c['NegativeDec'], false);
      expect(c['DecDegrees'], 41);
      expect(c['DecMinutes'], 16);
      expect(c['DecSeconds'], closeTo(8.4, 0.3));
    });

    test('dec clamps to the pole: -90° → 90°/0/0 negative', () {
      final c = inputCoordinatesFromDeg(0, -90);
      expect(c['NegativeDec'], true);
      expect(c['DecDegrees'], 90);
      expect(c['DecMinutes'], 0);
      expect(c['DecSeconds'], closeTo(0, 1e-6));
    });

    test('round-trips: H + M/60 + S/3600 reproduces the RA hours', () {
      const raDeg = 233.7361;
      final c = inputCoordinatesFromDeg(raDeg, 0);
      final hours = (c['RAHours'] as int) +
          (c['RAMinutes'] as int) / 60.0 +
          (c['RASeconds'] as double) / 3600.0;
      expect(hours * 15.0, closeTo(raDeg, 1e-6));
    });

    test('round-trips: signed D + M/60 + S/3600 reproduces the Dec degrees', () {
      const decDeg = -27.8664;
      final c = inputCoordinatesFromDeg(0, decDeg);
      final mag = (c['DecDegrees'] as int) +
          (c['DecMinutes'] as int) / 60.0 +
          (c['DecSeconds'] as double) / 3600.0;
      final signed = (c['NegativeDec'] as bool) ? -mag : mag;
      expect(signed, closeTo(decDeg, 1e-6));
    });
  });
}
