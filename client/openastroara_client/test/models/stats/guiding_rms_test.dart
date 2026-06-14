import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/guiding_rms.dart';

void main() {
  group('GuidingRmsSeries.fromJson', () {
    test('parses the full series envelope', () {
      final s = GuidingRmsSeries.fromJson(const {
        'samples': [
          {'timestamp': '2026-01-15T22:00:00Z', 'rms_arcsec': 0.8, 'ra_rms': null, 'dec_rms': null},
          {'timestamp': '2026-01-15T22:05:00Z', 'rms_arcsec': 1.2},
        ],
        'mean_rms_arcsec': 1.0,
        'p95_rms_arcsec': 1.2,
      });
      expect(s.samples.length, 2);
      expect(s.samples.first.rmsArcsec, 0.8);
      expect(s.samples.first.timestamp, DateTime.utc(2026, 1, 15, 22));
      expect(s.samples.first.timestamp!.isUtc, isTrue);
      expect(s.samples.first.raRms, isNull);
      expect(s.meanRmsArcsec, 1.0);
      expect(s.p95RmsArcsec, 1.2);
      expect(s.isEmpty, isFalse);
    });

    test('parses an optional RA/Dec breakdown when present', () {
      final s = GuidingRmsSeries.fromJson(const {
        'samples': [
          {'rms_arcsec': 1.0, 'ra_rms': 0.7, 'dec_rms': 0.6},
        ],
      });
      expect(s.samples.single.raRms, 0.7);
      expect(s.samples.single.decRms, 0.6);
    });

    test('empty / missing samples → isEmpty, null summary stats', () {
      final s = GuidingRmsSeries.fromJson(const {'samples': []});
      expect(s.isEmpty, isTrue);
      expect(s.meanRmsArcsec, isNull);
      expect(s.p95RmsArcsec, isNull);
    });

    test('drops samples without a numeric rms_arcsec, and non-object rows', () {
      final s = GuidingRmsSeries.fromJson(const {
        'samples': [
          {'rms_arcsec': 'oops'}, // non-numeric → dropped (no spurious 0 spike)
          {'timestamp': '2026-01-15T22:00:00Z'}, // missing rms_arcsec → dropped
          'garbage', // non-object → dropped
          {'rms_arcsec': 1.3}, // valid → kept
        ],
        'mean_rms_arcsec': 'nope',
      });
      expect(s.samples.length, 1);
      expect(s.samples.single.rmsArcsec, 1.3);
      expect(s.meanRmsArcsec, isNull);
    });
  });
}
