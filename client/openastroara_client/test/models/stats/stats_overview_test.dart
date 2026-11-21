import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/stats_overview.dart';

void main() {
  group('StatsOverview.fromJson', () {
    test('parses a full snake_case payload', () {
      final o = StatsOverview.fromJson(const {
        'total_sessions': 8,
        'total_frames': 1500,
        'total_light_frames': 1340,
        'total_integration_hours': 42.5,
        'unique_targets_imaged': 7,
        'first_image_utc': '2026-01-15T22:00:00Z',
        'last_image_utc': '2026-06-14T04:00:00Z',
        'last_session_score': 0.9,
      });
      expect(o.totalSessions, 8);
      expect(o.totalFrames, 1500);
      expect(o.totalLightFrames, 1340);
      expect(o.totalIntegrationHours, 42.5);
      expect(o.uniqueTargetsImaged, 7);
      expect(o.firstImageUtc, DateTime.utc(2026, 1, 15, 22));
      expect(o.lastImageUtc, DateTime.utc(2026, 6, 14, 4));
      expect(o.lastImageUtc!.isUtc, isTrue);
      expect(o.isEmpty, isFalse);
    });

    test('degrades on missing / wrong-typed fields', () {
      final o = StatsOverview.fromJson(const {'total_frames': 'oops'});
      expect(o.totalFrames, 0);
      expect(o.totalIntegrationHours, 0.0);
      expect(o.firstImageUtc, isNull);
      expect(o.isEmpty, isTrue, reason: 'no frames → empty');
    });

    test('coerces an integer hours value to double', () {
      expect(StatsOverview.fromJson(const {'total_integration_hours': 12}).totalIntegrationHours, 12.0);
    });
  });
}
