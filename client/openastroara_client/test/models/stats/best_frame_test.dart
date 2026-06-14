import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/stats/best_frame.dart';

void main() {
  group('BestFrame.fromJson', () {
    test('parses a full snake_case row', () {
      final f = BestFrame.fromJson(const {
        'frame_id': '11111111-2222-3333-4444-555555555555',
        'target_name': 'M31',
        'captured_utc': '2026-01-15T22:30:00Z',
        'composite_score': 0.93,
        'filter_name': 'Ha',
      });
      expect(f.frameId, '11111111-2222-3333-4444-555555555555');
      expect(f.targetName, 'M31');
      expect(f.capturedUtc, DateTime.utc(2026, 1, 15, 22, 30));
      expect(f.capturedUtc!.isUtc, isTrue);
      expect(f.compositeScore, 0.93);
      expect(f.filterName, 'Ha');
    });

    test('coerces an integer score to double and treats empty filter as null', () {
      final f = BestFrame.fromJson(const {
        'frame_id': 'abc',
        'composite_score': 1,
        'filter_name': '',
      });
      expect(f.compositeScore, 1.0);
      expect(f.filterName, isNull);
    });

    test('degrades on missing / wrong-typed fields', () {
      final f = BestFrame.fromJson(const {'composite_score': 'oops'});
      expect(f.frameId, '');
      expect(f.targetName, '');
      expect(f.capturedUtc, isNull);
      expect(f.compositeScore, 0.0);
      expect(f.filterName, isNull);
    });
  });

  group('BestFrame.listFromJson', () {
    test('parses the frames envelope, dropping non-object rows', () {
      final list = BestFrame.listFromJson(const {
        'frames': [
          {'frame_id': 'a', 'composite_score': 0.9},
          'not-an-object',
          {'frame_id': 'b', 'composite_score': 0.8},
        ],
      });
      expect(list.map((f) => f.frameId), ['a', 'b']);
    });

    test('returns empty when frames is missing or not a list', () {
      expect(BestFrame.listFromJson(const {}), isEmpty);
      expect(BestFrame.listFromJson(const {'frames': 'nope'}), isEmpty);
    });
  });
}
