import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/clone_status.dart';

void main() {
  group('CloneStatus.fromJson', () {
    test('parses a running status', () {
      final s = CloneStatus.fromJson(const {
        'state': 'running',
        'progress_pct': null,
        'current_area': 'profiles',
        'message': 'Restoring…',
      });
      expect(s.state, 'running');
      expect(s.isRunning, isTrue);
      expect(s.isTerminal, isFalse);
      expect(s.currentArea, 'profiles');
    });

    test('parses a done status with progress + message', () {
      final s = CloneStatus.fromJson(const {
        'state': 'done',
        'progress_pct': 100,
        'current_area': null,
        'message': 'Restored: profiles, sequences',
      });
      expect(s.isTerminal, isTrue);
      expect(s.isFailed, isFalse);
      expect(s.progressPct, 100);
      expect(s.message, contains('sequences'));
    });

    test('parses a failed status', () {
      final s = CloneStatus.fromJson(const {'state': 'failed', 'message': 'disk gone'});
      expect(s.isFailed, isTrue);
      expect(s.isTerminal, isTrue);
      expect(s.message, 'disk gone');
    });

    test('defaults a missing/odd state to idle and tolerates missing fields', () {
      final s = CloneStatus.fromJson(const {});
      expect(s.state, 'idle');
      expect(s.isTerminal, isFalse);
      expect(s.progressPct, isNull);
      expect(s.currentArea, isNull);
      expect(s.message, isNull);
    });

    test('ignores a non-numeric progress_pct', () {
      final s = CloneStatus.fromJson(const {'state': 'running', 'progress_pct': 'oops'});
      expect(s.progressPct, isNull);
    });
  });
}
