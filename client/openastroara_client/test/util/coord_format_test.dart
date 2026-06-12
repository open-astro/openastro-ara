import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/coord_format.dart';

void main() {
  group('formatRaHms', () {
    test('formats a plain value', () {
      expect(formatRaHms(5.5), '5h 30m 00s');
    });

    test('carries a rounded-up second into the minute (never 60s)', () {
      // 5h 59m 59.99s rounds the second to 60 → must roll to 6h 00m 00s.
      final out = formatRaHms(5.9999972);
      expect(out, isNot(contains('60s')));
      expect(out, '6h 00m 00s');
    });

    test('wraps at 24h', () {
      // 23h 59m 59.99s rolls all the way over to 0h.
      expect(formatRaHms(23.9999972), '0h 00m 00s');
    });

    test('zero pads minutes and seconds', () {
      expect(formatRaHms(1.0 + 2 / 60 + 3 / 3600), '1h 02m 03s');
    });
  });

  group('formatDecDms', () {
    test('formats a positive value with a leading +', () {
      expect(formatDecDms(20.5), '+20° 30\' 00"');
    });

    test('formats a negative value', () {
      expect(formatDecDms(-23.0 - 30 / 60), '-23° 30\' 00"');
    });

    test('carries a rounded-up second into the minute (never 60")', () {
      final out = formatDecDms(45.9999972);
      expect(out, isNot(contains('60"')));
      expect(out, '+46° 00\' 00"');
    });
  });
}
