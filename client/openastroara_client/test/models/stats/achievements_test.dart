import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/achievements.dart';

void main() {
  group('StatsAchievements.fromJson', () {
    test('parses a full snake_case payload', () {
      final a = StatsAchievements.fromJson(const {
        'total_nights_imaged': 12,
        'longest_streak_nights': 5,
        'current_streak_nights': 2,
        'longest_night_hours': 9.5,
        'total_integration_hours': 42.25,
        'unique_targets_imaged': 7,
        'total_light_frames': 1340,
        'first_light_utc': '2026-01-15T22:00:00Z',
        'milestones': [
          {
            'id': 'hours_10',
            'title': '10 Hours',
            'description': 'Image for 10 hours total',
            'achieved': true,
            'threshold': 10.0,
            'current': 42.25,
          },
          {
            'id': 'hours_100',
            'title': '100 Hours',
            'description': 'Image for 100 hours total',
            'achieved': false,
            'threshold': 100.0,
            'current': 42.25,
          },
        ],
      });

      expect(a.totalNightsImaged, 12);
      expect(a.longestStreakNights, 5);
      expect(a.currentStreakNights, 2);
      expect(a.longestNightHours, 9.5);
      expect(a.totalIntegrationHours, 42.25);
      expect(a.uniqueTargetsImaged, 7);
      expect(a.totalLightFrames, 1340);
      expect(a.firstLightUtc, DateTime.utc(2026, 1, 15, 22));
      expect(a.firstLightUtc!.isUtc, isTrue);
      expect(a.milestones, hasLength(2));
      expect(a.milestones.first.achieved, isTrue);
      expect(a.isEmpty, isFalse);
    });

    test('degrades on missing / wrong-typed fields rather than throwing', () {
      final a = StatsAchievements.fromJson(const {
        'total_nights_imaged': 'oops',
        'total_integration_hours': null,
        'milestones': 'not-a-list',
      });

      expect(a.totalNightsImaged, 0);
      expect(a.totalIntegrationHours, 0.0);
      expect(a.firstLightUtc, isNull);
      expect(a.milestones, isEmpty);
      expect(a.isEmpty, isTrue, reason: 'no light frames → empty');
    });

    test('coerces an integer hours value to double', () {
      final a = StatsAchievements.fromJson(const {'total_integration_hours': 10});
      expect(a.totalIntegrationHours, 10.0);
    });
  });

  group('StatsMilestone.progress', () {
    test('clamps to [0, 1]', () {
      const under = StatsMilestone(
          id: 'a', title: '', description: '', achieved: false, threshold: 100, current: 25);
      const over = StatsMilestone(
          id: 'b', title: '', description: '', achieved: true, threshold: 10, current: 42);
      expect(under.progress, 0.25);
      expect(over.progress, 1.0);
    });

    test('a non-positive threshold reads as complete (no divide-by-zero)', () {
      const m = StatsMilestone(
          id: 'c', title: '', description: '', achieved: false, threshold: 0, current: 0);
      expect(m.progress, 1.0);
    });
  });
}
