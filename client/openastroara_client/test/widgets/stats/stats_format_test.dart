import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/stats/stats_format.dart';

void main() {
  group('formatIntegrationHours', () {
    test('whole hours omit minutes', () {
      expect(formatIntegrationHours(12), '12h');
    });
    test('fractional hours add zero-padded minutes', () {
      expect(formatIntegrationHours(12.5), '12h 30m');
      expect(formatIntegrationHours(1.1), '1h 06m');
    });
    test('non-finite or negative render as em dash', () {
      expect(formatIntegrationHours(double.nan), '—');
      expect(formatIntegrationHours(double.infinity), '—');
      expect(formatIntegrationHours(-1), '—');
    });
  });

  group('formatStatsDate', () {
    test('null renders as em dash', () {
      expect(formatStatsDate(null), '—');
    });
    test('renders day month year in the local zone', () {
      // Construct from a local date so the test is timezone-independent.
      final local = DateTime(2026, 6, 14, 9, 30);
      expect(formatStatsDate(local.toUtc()), '14 Jun 2026');
    });
  });
}
