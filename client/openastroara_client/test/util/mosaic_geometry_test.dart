import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/mosaic_geometry.dart';

void main() {
  // RedCat 51 + IMX571 single frame: ~187' × 125'.
  const fov = (187.0, 125.0);

  test('tanToRaDec round-trips the page function on known offsets', () {
    // Pure centre → the target itself.
    final c = tanToRaDec(0, 0, 307.0, 44.9);
    expect(c.raDeg, closeTo(307.0, 1e-9));
    expect(c.decDeg, closeTo(44.9, 1e-9));
    // 1° east at Dec 0: RA +1°, Dec 0 (gnomonic ≈ linear at 1°).
    final e = tanToRaDec(1, 0, 10.0, 0.0);
    expect(e.raDeg, closeTo(11.0, 1e-3));
    expect(e.decDeg, closeTo(0.0, 1e-9));
    // 1° north from Dec 60: Dec 61, RA unchanged.
    final n = tanToRaDec(0, 1, 10.0, 60.0);
    expect(n.raDeg, closeTo(10.0, 1e-9));
    expect(n.decDeg, closeTo(61.0, 1e-3));
    // RA wraps into 0–360.
    expect(tanToRaDec(1, 0, 359.5, 0.0).raDeg, closeTo(0.5, 1e-3));
  });

  test('a 1×1 grid is the target; 2×1 splits symmetrically with overlap', () {
    final one = mosaicPanelCentres(
        raDeg: 307, decDeg: 44.9, fovArcmin: fov, g: singleFrame);
    expect(one, hasLength(1));
    expect(one.single.raDeg, closeTo(307, 1e-9));

    const two = (cols: 2, rows: 1, overlapPct: 10);
    final offs = mosaicPanelOffsetsArcmin(fov, two);
    // Step = 187' × 0.9 = 168.3'; centres at ∓84.15'.
    expect(offs[0].$1, closeTo(-84.15, 1e-6));
    expect(offs[1].$1, closeTo(84.15, 1e-6));
    expect(offs[0].$2, 0);
    final (w, h) = mosaicExtentArcmin(fov, two);
    expect(w, closeTo(187 + 168.3, 1e-6));
    expect(h, 125);

    // On the sky at Dec 44.9 the RA gap opens by ~1/cos(dec).
    final p = mosaicPanelCentres(
        raDeg: 307, decDeg: 44.9, fovArcmin: fov, g: two);
    final dRa = (p[1].raDeg - p[0].raDeg) * math.cos(44.9 * math.pi / 180);
    expect(dRa, closeTo(168.3 / 60, 0.01));
    expect(p[0].decDeg, closeTo(p[1].decDeg, 1e-6));
  });

  test('rotation turns the grid as a whole (row-major order kept)', () {
    const g = (cols: 2, rows: 2, overlapPct: 0);
    final flat = mosaicPanelOffsetsArcmin(fov, g);
    final turned = mosaicPanelOffsetsArcmin(fov, g, rotationDeg: 90);
    expect(flat, hasLength(4));
    // 90° clockwise-on-screen: (x, y) → (−y, x) in the page's convention.
    for (var i = 0; i < 4; i++) {
      expect(turned[i].$1, closeTo(-flat[i].$2, 1e-6));
      expect(turned[i].$2, closeTo(flat[i].$1, 1e-6));
    }
  });

  test('suggestMosaic picks the smallest square grid that covers the object', () {
    expect(suggestMosaic(60, fov), isNull); // fits one frame
    expect(suggestMosaic(null, fov), isNull);
    // 200' needs > 125' short side ×1.1 → 2×2 (short side 125 + 112.5).
    expect(suggestMosaic(200, fov), (cols: 2, rows: 2, overlapPct: 10));
    // 400' → 3×3 short side 125 + 2·112.5 = 350 < 440 → 4×4 (462.5).
    expect(suggestMosaic(400, fov), (cols: 4, rows: 4, overlapPct: 10));
    // Beyond 4×4 caps.
    expect(suggestMosaic(3000, fov), (cols: 4, rows: 4, overlapPct: 10));
  });
}
