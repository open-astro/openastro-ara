import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/frame_quality.dart';

void main() {
  group('FrameQualityDistribution.fromJson', () {
    test('parses the full distribution envelope', () {
      final d = FrameQualityDistribution.fromJson(const {
        'distribution': [
          {'range_low': 0.0, 'range_high': 0.1, 'count': 2},
          {'range_low': 0.9, 'range_high': 1.0, 'count': 5},
        ],
        'mean_score': 0.73,
        'std_dev': 0.12,
      });
      expect(d.buckets.length, 2);
      expect(d.buckets.first.rangeLow, 0.0);
      expect(d.buckets.first.rangeHigh, 0.1);
      expect(d.buckets.first.count, 2);
      expect(d.buckets.last.count, 5);
      expect(d.meanScore, 0.73);
      expect(d.stdDev, 0.12);
      expect(d.isEmpty, isFalse);
    });

    test('isEmpty when every bucket count is zero', () {
      final d = FrameQualityDistribution.fromJson(const {
        'distribution': [
          {'range_low': 0.0, 'range_high': 0.1, 'count': 0},
          {'range_low': 0.1, 'range_high': 0.2, 'count': 0},
        ],
        'mean_score': 0.0,
      });
      expect(d.isEmpty, isTrue);
      expect(d.stdDev, isNull);
    });

    test('degrades on missing / wrong-typed fields', () {
      final d = FrameQualityDistribution.fromJson(const {
        'distribution': 'nope',
        'mean_score': 'oops',
      });
      expect(d.buckets, isEmpty);
      expect(d.meanScore, 0.0);
      expect(d.isEmpty, isTrue);
    });

    test('drops non-object bucket rows and coerces int counts', () {
      final d = FrameQualityDistribution.fromJson(const {
        'distribution': [
          {'range_low': 0.5, 'range_high': 0.6, 'count': 3},
          'garbage',
        ],
      });
      expect(d.buckets.length, 1);
      expect(d.buckets.single.count, 3);
    });
  });
}
