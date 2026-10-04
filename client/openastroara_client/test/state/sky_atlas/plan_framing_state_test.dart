import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/sky_atlas/plan_framing_state.dart';

void main() {
  group('PlanFraming.fromEvent', () {
    test('reads the page payload and normalises the angle', () {
      final f = PlanFraming.fromEvent({
        'type': 'framing',
        'on': true,
        'raDeg': 314.8,
        'decDeg': 44.5,
        'rotationDeg': -61,
        'name': ' NGC 7000 ',
      });
      expect(f.hasTarget, isTrue);
      expect(f.rotationDeg, 299);
      expect(f.name, 'NGC 7000');
    });

    test('framing switched off or without a centre is no target', () {
      expect(
        PlanFraming.fromEvent({'on': false, 'raDeg': 1, 'decDeg': 2}).hasTarget,
        isFalse,
      );
      expect(PlanFraming.fromEvent({'on': true}).hasTarget, isFalse);
      // The rotateCamera button's payload carries no `on`: it is on by definition.
      expect(
        PlanFraming.fromEvent({'raDeg': 1, 'decDeg': 2}).hasTarget,
        isTrue,
      );
    });
  });
}
