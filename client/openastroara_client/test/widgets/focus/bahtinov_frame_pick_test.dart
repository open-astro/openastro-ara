import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/bahtinov_focus.dart';
import 'package:openastroara/widgets/focus/bahtinov_focus_card.dart';

void main() {
  group('tapToFramePixel', () {
    // A 3856×2180 frame drawn with BoxFit.contain in a 1000×500 box: the
    // picture is 884×500, centred, with 58 px of letterbox each side.
    const w = 3856, h = 2180;
    const box = Size(1000, 500);

    test('maps a tap on the whole frame to frame pixels', () {
      final p = BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(500, 250),
        frameWidth: w,
        frameHeight: h,
      );
      expect(p, (x: 1928, y: 1090));
      final tl = BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(58.5, 0.5),
        frameWidth: w,
        frameHeight: h,
      );
      // 0.7 px into an 884-px picture of 3856 frame pixels: ~3 frame pixels.
      expect(tl!.x, inInclusiveRange(2, 3));
      expect(tl.y, 2);
    });

    test('a tap in the letterbox is not a pick', () {
      expect(
        BahtinovFrameView.tapToFramePixel(
          box: box,
          tap: const Offset(20, 250),
          frameWidth: w,
          frameHeight: h,
        ),
        isNull,
      );
      expect(
        BahtinovFrameView.tapToFramePixel(
          box: box,
          tap: const Offset(500, 250),
          frameWidth: 0,
          frameHeight: 0,
        ),
        isNull,
        reason: 'no frame size yet',
      );
    });

    test('a tap on a crop lands inside the crop in frame coordinates', () {
      const crop = BahtinovFrameCrop(left: 1800, top: 900, size: 256);
      // The square crop fills the box's height: 500×500 at x = 250…750.
      final centre = BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(500, 250),
        frameWidth: w,
        frameHeight: h,
        crop: crop,
      );
      expect(centre, (x: 1928, y: 1028));
      final corner = BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(749, 499),
        frameWidth: w,
        frameHeight: h,
        crop: crop,
      );
      expect(corner, (x: 1800 + 255, y: 900 + 255));
    });
  });

  test('a zoomed crop maps taps through the magnified picture, wide boxes included', () {
    const crop = BahtinovFrameCrop(left: 1800, top: 900, size: 256);
    // 1000×500 at 2×: the crop is drawn 1000 px square, centred, clipped to the box.
    const box = Size(1000, 500);
    expect(
      BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(500, 250),
        frameWidth: 3856,
        frameHeight: 2180,
        crop: crop,
        zoom: 2,
      ),
      (x: 1928, y: 1028),
    );
    expect(
      BahtinovFrameView.tapToFramePixel(
        box: box,
        tap: const Offset(251, 1),
        frameWidth: 3856,
        frameHeight: 2180,
        crop: crop,
        zoom: 2,
      ),
      (x: 1864, y: 964),
    );
    // Review of #1365: 1000×300 at 2× draws the crop 600 px wide over x = 200…800,
    // wider than the central 300-px square. A tap at x 250 is on the picture.
    const wide = Size(1000, 300);
    expect(
      BahtinovFrameView.tapToFramePixel(
        box: wide,
        tap: const Offset(250, 150),
        frameWidth: 3856,
        frameHeight: 2180,
        crop: crop,
        zoom: 2,
      ),
      (x: 1821, y: 1028),
    );
    expect(
      BahtinovFrameView.tapToFramePixel(
        box: wide,
        tap: const Offset(799, 150),
        frameWidth: 3856,
        frameHeight: 2180,
        crop: crop,
        zoom: 2,
      ),
      (x: 2055, y: 1028),
      reason: 'the last visible column is the crop\'s last pixel',
    );
    expect(
      BahtinovFrameView.tapToFramePixel(
        box: wide,
        tap: const Offset(150, 150),
        frameWidth: 3856,
        frameHeight: 2180,
        crop: crop,
        zoom: 2,
      ),
      isNull,
      reason: 'left of the drawn picture',
    );
  });

  test('status parses the frame size, crop and pick', () {
    final s = BahtinovFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'frame_width': 3856,
      'frame_height': 2180,
      'frame_crop': {'left': 10, 'top': 20, 'size': 256},
      'pick_x': 138,
      'pick_y': 148,
    });
    expect((s.frameWidth, s.frameHeight), (3856, 2180));
    expect(s.frameCrop?.left, 10);
    expect(s.frameCrop?.top, 20);
    expect(s.frameCrop?.size, 256);
    expect(s.hasPick, isTrue);
    expect((s.pickX, s.pickY), (138, 148));
    final whole = BahtinovFocusStatus.fromJson({'frame_crop': null});
    expect(whole.frameCrop, isNull);
    expect(whole.hasPick, isFalse);
  });
}
