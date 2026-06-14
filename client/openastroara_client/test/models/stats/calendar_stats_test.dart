import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/calendar_stats.dart';

void main() {
  group('CalendarStats.fromJson', () {
    test('parses the days envelope', () {
      final c = CalendarStats.fromJson(const {
        'days': [
          {
            'date': '2026-06-10',
            'frame_count': 12,
            'integration_hours': 1.5,
            'targets_imaged': ['M31', 'M42'],
          },
          {
            'date': '2026-06-12',
            'frame_count': 4,
            'integration_hours': 0.5,
            'targets_imaged': [],
          },
        ],
      });
      expect(c.days.length, 2);
      expect(c.days.first.date, DateTime(2026, 6, 10));
      expect(c.days.first.frameCount, 12);
      expect(c.days.first.integrationHours, 1.5);
      expect(c.days.first.integrationMinutes, 90);
      expect(c.days.first.targetsImaged, ['M31', 'M42']);
      expect(c.isEmpty, isFalse);
    });

    test('minutesByDay keys by yyyy-MM-dd and rounds hours to minutes', () {
      final c = CalendarStats.fromJson(const {
        'days': [
          {'date': '2026-06-09', 'integration_hours': 2.0},
          {'date': '2026-12-01', 'integration_hours': 0.25},
        ],
      });
      expect(c.minutesByDay['2026-06-09'], 120);
      expect(c.minutesByDay['2026-12-01'], 15);
    });

    test('drops a day with an unparseable date, and non-object rows', () {
      final c = CalendarStats.fromJson(const {
        'days': [
          {'date': 'not-a-date', 'frame_count': 9},
          'garbage',
          {'date': '2026-06-10', 'frame_count': 1},
        ],
      });
      expect(c.days.length, 1);
      expect(c.days.single.date, DateTime(2026, 6, 10));
    });

    test('missing / wrong-typed days → empty', () {
      expect(CalendarStats.fromJson(const {}).isEmpty, isTrue);
      expect(CalendarStats.fromJson(const {'days': 'nope'}).isEmpty, isTrue);
    });
  });
}
