import 'dart:math' as math;

/// Mosaic grid geometry shared by the session planner's preview, its
/// "Show on the planetarium" hand-off and its Add-to-run panels — a Dart port
/// of the framing overlay's `addFrameToSequence` grid + `tanToRaDec` gnomonic
/// deprojection (assets/stellarium/index.html), so the run's panels are
/// EXACTLY the rectangles drawn on the preview and the atlas.
typedef MosaicGrid = ({int cols, int rows, int overlapPct});

const MosaicGrid singleFrame = (cols: 1, rows: 1, overlapPct: 10);

extension MosaicGridX on MosaicGrid {
  int get panelCount => cols * rows;
  bool get isMosaic => panelCount > 1;
}

/// Step between panel centres per axis (arcmin) for a frame of [fovArcmin].
(double, double) _step((double, double) fovArcmin, int overlapPct) {
  final ov = overlapPct.clamp(0, 90) / 100;
  return (fovArcmin.$1 * (1 - ov), fovArcmin.$2 * (1 - ov));
}

/// The whole grid's footprint (width, height arcmin) before rotation.
(double, double) mosaicExtentArcmin((double, double) fovArcmin, MosaicGrid g) {
  final (sw, sh) = _step(fovArcmin, g.overlapPct);
  return (
    fovArcmin.$1 + sw * (g.cols - 1),
    fovArcmin.$2 + sh * (g.rows - 1),
  );
}

/// Panel offsets in the tangent plane (arcmin, x east-west / y north-south
/// BEFORE rotation) — row-major, same order the overlay emits. Rotation is
/// applied here so callers draw or deproject the same points.
List<(double, double)> mosaicPanelOffsetsArcmin(
  (double, double) fovArcmin,
  MosaicGrid g, {
  double rotationDeg = 0,
}) {
  final (sw, sh) = _step(fovArcmin, g.overlapPct);
  final th = rotationDeg * math.pi / 180;
  final cosT = math.cos(th), sinT = math.sin(th);
  return [
    for (var iy = 0; iy < g.rows; iy++)
      for (var ix = 0; ix < g.cols; ix++)
        () {
          final gx = (ix - (g.cols - 1) / 2) * sw;
          final gy = (iy - (g.rows - 1) / 2) * sh;
          return (gx * cosT - gy * sinT, gx * sinT + gy * cosT);
        }(),
  ];
}

/// Gnomonic (TAN) inverse: tangent-plane offsets (degrees) → J2000 RA/Dec,
/// identical to the page's `tanToRaDec`.
({double raDeg, double decDeg}) tanToRaDec(
    double xiDeg, double etaDeg, double ra0Deg, double dec0Deg) {
  const d2r = math.pi / 180;
  final xi = xiDeg * d2r, eta = etaDeg * d2r;
  final rho = math.sqrt(xi * xi + eta * eta);
  if (rho < 1e-12) return (raDeg: ra0Deg, decDeg: dec0Deg);
  final c = math.atan(rho), sinC = math.sin(c), cosC = math.cos(c);
  final d0 = dec0Deg * d2r, sin0 = math.sin(d0), cos0 = math.cos(d0);
  final dec = math.asin(cosC * sin0 + (eta * sinC * cos0) / rho);
  final ra = ra0Deg * d2r +
      math.atan2(xi * sinC, rho * cos0 * cosC - eta * sin0 * sinC);
  var raOut = ra / d2r;
  raOut = ((raOut % 360) + 360) % 360;
  return (raDeg: raOut, decDeg: dec / d2r);
}

/// Per-panel J2000 centres (row-major) for a grid of [g] camera frames of
/// [fovArcmin] centred on the target and rotated by [rotationDeg]. A 1×1 grid
/// is the target itself.
List<({double raDeg, double decDeg})> mosaicPanelCentres({
  required double raDeg,
  required double decDeg,
  required (double, double) fovArcmin,
  required MosaicGrid g,
  double rotationDeg = 0,
}) =>
    [
      for (final (dx, dy) in mosaicPanelOffsetsArcmin(fovArcmin, g,
          rotationDeg: rotationDeg))
        tanToRaDec(dx / 60, dy / 60, raDeg, decDeg),
    ];

/// The smallest grid whose footprint covers an object [sizeMajArcmin] across
/// with the default overlap, up to 4×4 — the "Suggest" behind an overflowing
/// target. Null when a single frame already covers it (or the size is unknown).
MosaicGrid? suggestMosaic(double? sizeMajArcmin, (double, double) fovArcmin,
    {int overlapPct = 10, int maxTiles = 4}) {
  if (sizeMajArcmin == null || sizeMajArcmin <= 0) return null;
  final need = sizeMajArcmin * 1.1; // a little sky around it
  for (var n = 1; n <= maxTiles; n++) {
    final g = (cols: n, rows: n, overlapPct: overlapPct);
    final (w, h) = mosaicExtentArcmin(fovArcmin, g);
    if (math.min(w, h) >= need) return n == 1 ? null : g;
  }
  return (cols: maxTiles, rows: maxTiles, overlapPct: overlapPct);
}
