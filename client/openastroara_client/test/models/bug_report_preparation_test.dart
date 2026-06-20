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
}
