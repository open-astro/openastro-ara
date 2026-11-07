import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bug_report_preparation.dart';

void main() {
  group('BugReportPreparation.fromJson', () {
    test('parses a ready preparation', () {
      final p = BugReportPreparation.fromJson(const {
        'preparation_id': 'abc-123',
        'status': 'ready',
        'estimated_size_bytes': 262144,
        'completed_utc': '2026-06-20T10:00:00Z',
      });
      expect(p.preparationId, 'abc-123');
      expect(p.status, 'ready');
      expect(p.estimatedSizeBytes, 262144);
      expect(p.completedUtc, isNotNull);
    });

    test('reads the §54.3 sharing mode and falls back to full on an older daemon', () {
      expect(
        BugReportPreparation.fromJson(const {'preparation_id': 'a', 'status': 'ready', 'sharing_mode': 'coarse_gps'}).sharingMode,
        'coarse_gps',
      );
      expect(
        BugReportPreparation.fromJson(const {'preparation_id': 'a', 'status': 'ready'}).sharingMode,
        'full',
      );
    });

    test('throws FormatException on a missing preparation_id', () {
      expect(
        () => BugReportPreparation.fromJson(const {'status': 'ready'}),
        throwsFormatException,
      );
    });

    test('throws on an empty preparation_id', () {
      expect(
        () => BugReportPreparation.fromJson(const {'preparation_id': ''}),
        throwsFormatException,
      );
    });
  });

  test('a whole-rig preparation carries its sources, range and error', () {
    final p = BugReportPreparation.fromJson({
      'preparation_id': 'x',
      'status': 'ready',
      'estimated_size_bytes': 10,
      'since_utc': '2026-10-28T03:00:00Z',
      'sources': [
        {'source': 'guider', 'files': 3, 'bytes': 99, 'truncated': true, 'note': null},
      ],
      'error': null,
    });
    expect(p.sources.single.source, 'guider');
    expect(p.sources.single.truncated, isTrue);
    expect(p.sinceUtc, DateTime.utc(2026, 10, 28, 3));
    expect(p.isPreparing, isFalse);
    expect(BugReportPreparation.fromJson({'preparation_id': 'y', 'status': 'preparing'}).isPreparing, isTrue);
    expect(BugReportPreparation.fromJson({'preparation_id': 'z', 'status': 'failed', 'error': 'disk'}).error, 'disk');
  });
}
