import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/sky_atlas/rotation_overlay.dart';

void main() {
  group('rotation overlay geometry', () {
    test('north is straight up at position angle 0, either way round', () {
      expect(northScreenAngleDeg(0, flipped: false), -90);
      expect(northScreenAngleDeg(0, flipped: true), -90);
    });
    test('unflipped, north turns clockwise with the position angle', () {
      // East is to the left of an unflipped picture: the up axis turned 30°
      // toward east is 30° counterclockwise, so north sits 30° clockwise of up.
      expect(northScreenAngleDeg(30, flipped: false), -60);
    });
    test('a mirrored train reverses the sense', () {
      expect(northScreenAngleDeg(30, flipped: true), -120);
    });
    test(
      'the planned rectangle turns against the remaining delta, flip-aware',
      () {
        expect(plannedFrameScreenRotationDeg(23, flipped: false), -23);
        expect(plannedFrameScreenRotationDeg(23, flipped: true), 23);
        expect(plannedFrameScreenRotationDeg(0, flipped: false), 0);
      },
    );
  });
}
