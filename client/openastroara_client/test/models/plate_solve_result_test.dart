import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/plate_solve_result.dart';

void main() {
  group('PlateSolveResult.fromJson', () {
    test('parses a successful solution (snake_case wire shape)', () {
      final r = PlateSolveResult.fromJson(const {
        'success': true,
        'ra': 5.5,
        'dec': -23.25,
        'orientation': 91.3,
        'pixel_scale': 1.27,
        'search_radius': 2.0,
      });
      expect(r.success, isTrue);
      expect(r.ra, 5.5);
      expect(r.dec, -23.25);
      expect(r.orientation, 91.3);
      expect(r.pixelScale, 1.27);
      expect(r.searchRadius, 2.0);
    });

    test('parses a no-solution result with all-null fields', () {
      final r = PlateSolveResult.fromJson(const {
        'success': false,
        'ra': null,
        'dec': null,
        'orientation': null,
        'pixel_scale': null,
        'search_radius': null,
      });
      expect(r.success, isFalse);
      expect(r.ra, isNull);
      expect(r.pixelScale, isNull);
    });

    test('coerces integer JSON numbers to double', () {
      final r = PlateSolveResult.fromJson(const {
        'success': true,
        'ra': 6,
        'dec': 0,
        'pixel_scale': 2,
      });
      expect(r.ra, 6.0);
      expect(r.dec, 0.0);
      expect(r.pixelScale, 2.0);
    });

    test('defaults success to false when the field is missing', () {
      final r = PlateSolveResult.fromJson(const {});
      expect(r.success, isFalse);
      expect(r.ra, isNull);
    });
  });
}
