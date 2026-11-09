import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/guide_optics.dart';

void main() {
  group('guideFocalLengthLooksCopiedFromMainScope (#1715)', () {
    test('flags a guide focal length equal to the main focal length, with or without the reducer', () {
      expect(guideFocalLengthLooksCopiedFromMainScope(375, 375, 1.0), isTrue);
      expect(guideFocalLengthLooksCopiedFromMainScope(300, 375, 0.8), isTrue);
      expect(guideFocalLengthLooksCopiedFromMainScope(375, 375, 0.8), isTrue);
    });

    test('does not flag a different guide scope, or anything unset', () {
      expect(guideFocalLengthLooksCopiedFromMainScope(128, 375, 1.0), isFalse);
      expect(guideFocalLengthLooksCopiedFromMainScope(0, 0, 1.0), isFalse);
      expect(guideFocalLengthLooksCopiedFromMainScope(375, 0, 1.0), isFalse);
    });
  });

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
