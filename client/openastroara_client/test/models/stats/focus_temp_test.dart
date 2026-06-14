import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/focus_temp.dart';

void main() {
  group('FocusTempSeries.fromJson', () {
    test('parses the full scatter envelope', () {
      final s = FocusTempSeries.fromJson(const {
        'samples': [
          {'temperature_c': 5.0, 'focuser_position': 1000, 'timestamp': '2026-01-15T22:00:00Z'},
          {'temperature_c': 3.0, 'focuser_position': 1100, 'timestamp': '2026-01-15T22:05:00Z'},
        ],
        'correlation_r2': 0.92,
      });
      expect(s.samples.length, 2);
      expect(s.samples.first.temperatureC, 5.0);
      expect(s.samples.first.focuserPosition, 1000);
      expect(s.samples.first.timestamp, DateTime.utc(2026, 1, 15, 22));
      expect(s.samples.first.timestamp!.isUtc, isTrue);
      expect(s.correlationR2, 0.92);
      expect(s.isEmpty, isFalse);
    });

    test('a zone-less timestamp is read as UTC, not shifted to local', () {
      final s = FocusTempSeries.fromJson(const {
        'samples': [
          {'temperature_c': 1.0, 'focuser_position': 500, 'timestamp': '2026-01-15T22:00:00'},
        ],
      });
      expect(s.samples.single.timestamp, DateTime.utc(2026, 1, 15, 22));
      expect(s.samples.single.timestamp!.isUtc, isTrue);
    });

    test('a numeric-offset timestamp converts to the correct UTC instant', () {
      final s = FocusTempSeries.fromJson(const {
        'samples': [
          {'temperature_c': 1.0, 'focuser_position': 500, 'timestamp': '2026-01-15T22:00:00-05:00'},
        ],
      });
      expect(s.samples.single.timestamp, DateTime.utc(2026, 1, 16, 3));
    });

    test('empty / missing samples → isEmpty, null correlation', () {
      final s = FocusTempSeries.fromJson(const {'samples': []});
      expect(s.isEmpty, isTrue);
      expect(s.correlationR2, isNull);
    });

    test('drops rows missing either plotted axis, and non-object rows', () {
      final s = FocusTempSeries.fromJson(const {
        'samples': [
          {'temperature_c': 5.0}, // missing focuser_position → dropped
          {'focuser_position': 1000}, // missing temperature_c → dropped
          {'temperature_c': 'oops', 'focuser_position': 1000}, // non-numeric → dropped
          'garbage', // non-object → dropped
          {'temperature_c': 4.0, 'focuser_position': 1050}, // valid → kept
        ],
        'correlation_r2': 'nope',
      });
      expect(s.samples.length, 1);
      expect(s.samples.single.temperatureC, 4.0);
      expect(s.samples.single.focuserPosition, 1050);
      expect(s.correlationR2, isNull);
    });
  });
}
