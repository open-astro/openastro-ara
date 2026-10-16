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

  test('at 2× the picture is the central half of the crop', () {
    const crop = BahtinovFrameCrop(left: 1800, top: 900, size: 256);
    final v = BahtinovFrameView.visibleCrop(crop, 2)!;
    expect((v.left, v.top, v.size), (1864, 964, 128));
    expect(BahtinovFrameView.visibleCrop(crop, 1), same(crop));
    expect(BahtinovFrameView.visibleCrop(null, 4), isNull);
    // The box's centre is still the crop's centre; a corner tap stays inside
    // the visible 128-px square.
    const box = Size(1000, 500);
    final centre = BahtinovFrameView.tapToFramePixel(
      box: box,
      tap: const Offset(500, 250),
      frameWidth: 3856,
      frameHeight: 2180,
      crop: crop,
      zoom: 2,
    );
    expect(centre, (x: 1928, y: 1028));
    final corner = BahtinovFrameView.tapToFramePixel(
      box: box,
      tap: const Offset(251, 1),
      frameWidth: 3856,
      frameHeight: 2180,
      crop: crop,
      zoom: 2,
    );
    expect(corner, (x: 1864, y: 964));
  });

  test('the steady reading is the median of the last five measured frames', () {
    Map<String, dynamic> sample(int seq, double? off) => {
      'seq': seq,
      'detected': off != null,
      'offset_px': off,
      'defocus_um': off == null ? null : off * 10,
      'within_zone': off != null && off.abs() <= 0.79,
    };
    final s = BahtinovFocusStatus.fromJson({
      'active': true,
      'state': 'running',
      'zone_px': 0.79,
      'recent': [
        sample(1, 5.0),
        sample(2, 0.2),
        sample(3, null),
        sample(4, -0.4),
        sample(5, 2.9),
        sample(6, 0.1),
        sample(7, 0.5),
      ],
      'latest': sample(7, 0.5),
    });
    final steady = s.steady!;
    // Last five measured: 0.2, -0.4, 2.9, 0.1, 0.5 → median 0.2; the 5.0 frame is out of the window.
    expect(steady.offsetPx, closeTo(0.2, 1e-9));
    expect(steady.defocusUm, closeTo(2.0, 1e-9));
    expect(
      steady.withinZone,
      isTrue,
      reason: 'the median sits in the zone though the latest frame jumped',
    );
    expect(s.latest!.offsetPx, 0.5, reason: 'the raw frame value is untouched');
    final single = BahtinovFocusStatus.fromJson({
      'latest': sample(1, 3.0),
      'recent': [sample(1, 3.0)],
    });
    expect(
      single.steady!.offsetPx,
      3.0,
      reason: 'one frame is its own reading',
    );
    final miss = BahtinovFocusStatus.fromJson({
      'latest': sample(2, null),
      'recent': [sample(1, 3.0), sample(2, null)],
    });
    expect(miss.steady!.detected, isFalse);
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
