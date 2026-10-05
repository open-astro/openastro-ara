import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/coord_parse.dart';

void main() {
  group('parseRa', () {
    test('reads the common sexagesimal forms as hours', () {
      const want = (5 + 35 / 60 + 17.3 / 3600) * 15;
      for (final s in [
        '05 35 17.3',
        '05:35:17.3',
        '5h35m17.3s',
        '5h 35m 17.3s',
        '05h35m17,3s',
        ' 05 35 17.3 ',
      ]) {
        expect(parseRa(s), closeTo(want, 1e-9), reason: s);
      }
    });

    test('two fields are hours and minutes', () {
      expect(parseRa('05 35'), closeTo(5.5833333 * 15, 1e-5));
    });

    test('a single decimal above 24 is degrees', () {
      expect(parseRa('83.82208'), closeTo(83.82208, 1e-9));
      expect(parseRa('83.82208', decimalUnit: RaUnit.hours),
          closeTo(83.82208, 1e-9));
    });

    test('a single decimal at or below 24 follows the chosen unit', () {
      expect(parseRa('5.5'), closeTo(5.5, 1e-9));
      expect(parseRa('5.5', decimalUnit: RaUnit.hours), closeTo(82.5, 1e-9));
      expect(raUnitIsAmbiguous('5.5'), isTrue);
      expect(raUnitIsAmbiguous('83.8'), isFalse);
      expect(raUnitIsAmbiguous('5.5h'), isFalse);
      expect(raUnitIsAmbiguous('05 35'), isFalse);
    });

    test('an h marker forces hours', () {
      expect(parseRa('5.5h'), closeTo(82.5, 1e-9));
      expect(parseRa('5.5h', decimalUnit: RaUnit.degrees), closeTo(82.5, 1e-9));
    });

    test('rejects out-of-range, negative and malformed values', () {
      expect(parseRa('24 00 00'), isNull);
      expect(parseRa('05 60 00'), isNull);
      expect(parseRa('05 35 60'), isNull);
      expect(parseRa('-05 35 17'), isNull);
      expect(parseRa('360'), isNull);
      expect(parseRa('25h'), isNull);
      expect(parseRa('M42'), isNull);
      expect(parseRa(''), isNull);
      expect(parseRa('05 35 17 3'), isNull);
    });
  });

  group('parseDec', () {
    test('reads sexagesimal with the sign on the whole value', () {
      const want = -(5 + 23 / 60 + 28 / 3600);
      for (final s in ['-05 23 28', '-05:23:28', '-5°23\'28"', '−05d23m28s']) {
        expect(parseDec(s), closeTo(want, 1e-9), reason: s);
      }
      expect(parseDec('+71 18 16'), closeTo(71 + 18 / 60 + 16 / 3600, 1e-9));
    });

    test('keeps the sign of a negative zero degree field', () {
      expect(parseDec('-00 30 00'), closeTo(-0.5, 1e-9));
    });

    test('reads decimal degrees', () {
      expect(parseDec('-5.39111'), closeTo(-5.39111, 1e-9));
      expect(parseDec('71.30444'), closeTo(71.30444, 1e-9));
    });

    test('rejects beyond the poles and bad fields', () {
      expect(parseDec('90 00 01'), isNull);
      expect(parseDec('-91'), isNull);
      expect(parseDec('10 61 00'), isNull);
      expect(parseDec('10 +20 00'), isNull);
      expect(parseDec('north'), isNull);
    });
  });

  group('splitRaDec / parseRaDec', () {
    test('splits a SIMBAD sexagesimal line at the Dec sign', () {
      final p = parseRaDec('02 37 31.5 +71 18 16');
      expect(p, isNotNull);
      expect(p!.raDeg, closeTo((2 + 37 / 60 + 31.5 / 3600) * 15, 1e-9));
      expect(p.decDeg, closeTo(71 + 18 / 60 + 16 / 3600, 1e-9));
    });

    test('splits Gaia decimal degrees', () {
      final p = parseRaDec('39.38133 71.30444');
      expect(p, equals(const ParsedCoordinates(39.38133, 71.30444)));
      final n = parseRaDec('83.82208 -5.39111');
      expect(n!.decDeg, closeTo(-5.39111, 1e-9));
    });

    test('halves an unsigned even run of fields', () {
      final p = parseRaDec('05 35 17.3 05 23 28');
      expect(p!.raDeg, closeTo((5 + 35 / 60 + 17.3 / 3600) * 15, 1e-9));
      expect(p.decDeg, closeTo(5 + 23 / 60 + 28 / 3600, 1e-9));
    });

    test('reads marker and comma separated forms', () {
      expect(parseRaDec('5h35m17s +22d00m52s'), isNotNull);
      expect(parseRaDec('05:35:17.3, -05:23:28'), isNotNull);
    });

    test('reads Stellarium\'s slash-separated RA/Dec readout', () {
      final p = parseRaDec('5h35m17.3s/-5°23\'28"');
      expect(p!.raDeg, closeTo((5 + 35 / 60 + 17.3 / 3600) * 15, 1e-9));
      expect(p.decDeg, closeTo(-(5 + 23 / 60 + 28 / 3600), 1e-9));
      final n = parseRaDec('5h35m17.3s/+22°00\'52"');
      expect(n!.decDeg, closeTo(22 + 52 / 3600, 1e-9));
      expect(looksLikeCoordinates('5h35m17.3s/-5°23\'28"'), isTrue);
    });

    test('rejects odd field counts and single values', () {
      expect(splitRaDec('05 35 17'), isNull);
      expect(parseRaDec('83.8'), isNull);
      expect(parseRaDec(''), isNull);
    });
  });

  group('looksLikeCoordinates', () {
    test('is true for pasted positions and false for names', () {
      expect(looksLikeCoordinates('05:35 -05:23'), isTrue);
      expect(looksLikeCoordinates('83.82 -5.39'), isTrue);
      expect(looksLikeCoordinates('5h35m +22d'), isTrue);
      expect(looksLikeCoordinates('NGC 7000'), isFalse);
      expect(looksLikeCoordinates('Vega'), isFalse);
      expect(looksLikeCoordinates('Sh2 101'), isFalse);
      expect(looksLikeCoordinates('M 42'), isFalse);
      // Designations made of unit letters, digits and a sign stay names.
      expect(looksLikeCoordinates('Sh2-155'), isFalse);
      expect(looksLikeCoordinates('HD 12345'), isFalse);
    });
  });
}
