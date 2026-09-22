import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/nmea_parser.dart';

void main() {
  group('parseNmeaSentence', () {
    test('RMC with a valid checksum yields time and signed position', () {
      final fix = parseNmeaSentence(
          r'$GPRMC,041926.000,A,3851.2384,N,07702.6101,W,0.09,318.63,220926,,,A*79');
      // The checksum above is computed below by the sanity test; assert on content here.
      expect(fix, isNotNull);
      expect(fix!.timeUtc, DateTime.utc(2026, 9, 22, 4, 19, 26));
      expect(fix.latitudeDeg, closeTo(38.85397, 1e-5));
      expect(fix.longitudeDeg, closeTo(-77.04350, 1e-5));
      expect(fix.altitudeM, isNull);
    });

    test('talker prefix does not matter (GN, GL)', () {
      final gn = parseNmeaSentence(_withChecksum(r'GNRMC,120000.00,A,5130.0000,N,00007.0000,W,0.0,0.0,010126,,,A'));
      expect(gn, isNotNull);
      expect(gn!.timeUtc, DateTime.utc(2026, 1, 1, 12));
      expect(gn.latitudeDeg, closeTo(51.5, 1e-9));
      expect(gn.longitudeDeg, closeTo(-0.116667, 1e-6));
    });

    test('void RMC (V flag) is not a fix', () {
      expect(parseNmeaSentence(_withChecksum(r'GPRMC,120000,V,,,,,,,010126,,,N')), isNull);
    });

    test('bad checksum is rejected', () {
      expect(parseNmeaSentence(r'$GPRMC,120000,A,5130.0000,N,00007.0000,W,0.0,0.0,010126,,,A*00'), isNull);
    });

    test('sentence without a checksum still parses', () {
      final fix = parseNmeaSentence(r'$GPRMC,120000,A,5130.0000,N,00007.0000,W,0.0,0.0,010126,,,A');
      expect(fix?.timeUtc, DateTime.utc(2026, 1, 1, 12));
    });

    test('GGA yields position and altitude but no time', () {
      final fix = parseNmeaSentence(_withChecksum(r'GPGGA,120000,5130.0000,N,00007.0000,W,1,08,0.9,145.3,M,47.0,M,,'));
      expect(fix, isNotNull);
      expect(fix!.timeUtc, isNull);
      expect(fix.altitudeM, closeTo(145.3, 1e-9));
      expect(fix.latitudeDeg, closeTo(51.5, 1e-9));
    });

    test('GGA with fix quality 0 is not a fix', () {
      expect(parseNmeaSentence(_withChecksum(r'GPGGA,120000,,,,,0,00,,,M,,M,,')), isNull);
    });

    test('a cold receiver with a nonsense date is skipped', () {
      expect(parseNmeaSentence(_withChecksum(r'GPRMC,120000,A,5130.0000,N,00007.0000,W,0.0,0.0,310226,,,A')), isNull,
          reason: '31 Feb must not roll over to March');
      expect(parseNmeaSentence(_withChecksum(r'GPRMC,120000,A,5130.0000,N,00007.0000,W,0.0,0.0,011326,,,A')), isNull,
          reason: 'month 13');
    });

    test('fractional seconds are kept', () {
      final fix = parseNmeaSentence(_withChecksum(r'GPRMC,120000.250,A,5130.0000,N,00007.0000,W,0.0,0.0,010126,,,A'));
      expect(fix!.timeUtc, DateTime.utc(2026, 1, 1, 12, 0, 0, 250));
    });

    test('line noise, other sentences and empty input are null, never throw', () {
      for (final s in [null, '', r'$', 'GPRMC,…', r'$GPGSV,3,1,11,…*00', r'$GPRMC*', r'$GPRMC,1,2*Z9', 'x' * 3]) {
        expect(() => parseNmeaSentence(s), returnsNormally);
        expect(parseNmeaSentence(s), isNull);
      }
    });

    test('checksum helper matches the parser', () {
      // Sanity for the fixtures: the first test's literal was produced this way.
      expect(_withChecksum(r'GPRMC,041926.000,A,3851.2384,N,07702.6101,W,0.09,318.63,220926,,,A'),
          r'$GPRMC,041926.000,A,3851.2384,N,07702.6101,W,0.09,318.63,220926,,,A*79');
    });
  });
}

String _withChecksum(String body) {
  var c = 0;
  for (final u in body.codeUnits) {
    c ^= u;
  }
  return '\$$body*${c.toRadixString(16).toUpperCase().padLeft(2, '0')}';
}
