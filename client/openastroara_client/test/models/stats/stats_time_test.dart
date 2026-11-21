import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/stats_time.dart';

void main() {
  group('parseStatsUtc', () {
    test('a Z-suffixed timestamp is UTC', () {
      final d = parseStatsUtc('2026-01-15T22:00:00Z');
      expect(d, DateTime.utc(2026, 1, 15, 22));
      expect(d!.isUtc, isTrue);
    });

    test('a zone-less timestamp is reinterpreted as UTC, not shifted to local', () {
      final d = parseStatsUtc('2026-01-15T22:00:00');
      expect(d, DateTime.utc(2026, 1, 15, 22));
      expect(d!.isUtc, isTrue);
    });

    test('a numeric-offset timestamp converts to the correct UTC instant', () {
      final d = parseStatsUtc('2026-01-15T22:00:00-05:00');
      expect(d, DateTime.utc(2026, 1, 16, 3));
      expect(d!.isUtc, isTrue);
    });

    test('non-string / empty / unparseable returns null', () {
      expect(parseStatsUtc(null), isNull);
      expect(parseStatsUtc(42), isNull);
      expect(parseStatsUtc(''), isNull);
      expect(parseStatsUtc('not a date'), isNull);
    });
  });
}
