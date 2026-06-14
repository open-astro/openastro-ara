import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/stats_target.dart';

void main() {
  group('StatsTarget.fromJson', () {
    test('parses a full snake_case row', () {
      final t = StatsTarget.fromJson(const {
        'target_name': 'M31',
        'frame_count': 142,
        'integration_hours': 11.75,
        'composite_quality_score': null,
        'last_imaged_utc': '2026-01-15T22:00:00Z',
      });
      expect(t.targetName, 'M31');
      expect(t.frameCount, 142);
      expect(t.integrationHours, 11.75);
      expect(t.lastImagedUtc, DateTime.utc(2026, 1, 15, 22));
      expect(t.lastImagedUtc!.isUtc, isTrue);
    });

    test('degrades on missing / wrong-typed fields', () {
      final t = StatsTarget.fromJson(const {'frame_count': 'oops'});
      expect(t.targetName, '');
      expect(t.frameCount, 0);
      expect(t.integrationHours, 0.0);
      expect(t.lastImagedUtc, isNull);
    });

    test('coerces an integer hours value to double', () {
      final t = StatsTarget.fromJson(const {'integration_hours': 12});
      expect(t.integrationHours, 12.0);
    });
  });
}
