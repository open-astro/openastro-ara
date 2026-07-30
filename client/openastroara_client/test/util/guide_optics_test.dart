import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/guide_optics.dart';

void main() {
  group('derivedOagGuideFocalLength (§63.19)', () {
    test('multiplies main focal length by the reducer factor', () {
      expect(derivedOagGuideFocalLength(1480, 0.8), 1184);
      expect(derivedOagGuideFocalLength(1000, 1.0), 1000);
      expect(derivedOagGuideFocalLength(400, 2.0), 800); // barlow
    });

    test('rounds to the nearest integer millimetre', () {
      expect(derivedOagGuideFocalLength(530, 0.63), 334); // 333.9
      expect(derivedOagGuideFocalLength(999, 0.5), 500); // 499.5 rounds up
    });

    test('returns 0 (unset) when optics are unset or reducer invalid', () {
      expect(derivedOagGuideFocalLength(0, 1.0), 0);
      expect(derivedOagGuideFocalLength(-100, 1.0), 0);
      expect(derivedOagGuideFocalLength(1000, 0), 0);
      expect(derivedOagGuideFocalLength(1000, -0.5), 0);
    });
  });
}
