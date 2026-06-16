import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../settings/optics_settings_state.dart';
import 'framing_state.dart';

/// §36/§25.5 — the framing overlay the Planning **Frame** mode draws on the
/// Aladin atlas: the profile's sensor seen through its optics, sized in
/// **degrees** and oriented at the framing rotation. A single panel by default
/// ([cols]×[rows] = 1×1); a mosaic when the framing controls request more, with
/// each panel one FOV and adjacent panels sharing [overlapPct]. Null when Frame
/// mode is off or the optics aren't configured (so the overlay clears instead of
/// drawing a fake box).
class FovBox {
  final double widthDeg;
  final double heightDeg;

  /// Position angle in degrees (the framing rotation), applied to the whole grid.
  final double rotationDeg;

  /// Mosaic panel grid. 1×1 is a single FOV box.
  final int cols;
  final int rows;

  /// Overlap between adjacent panels, as a percentage of a panel's size — the
  /// step between panel centres is `size × (1 − overlapPct/100)`.
  final double overlapPct;

  const FovBox({
    required this.widthDeg,
    required this.heightDeg,
    required this.rotationDeg,
    this.cols = 1,
    this.rows = 1,
    this.overlapPct = 0,
  });

  @override
  bool operator ==(Object other) =>
      other is FovBox &&
      other.widthDeg == widthDeg &&
      other.heightDeg == heightDeg &&
      other.rotationDeg == rotationDeg &&
      other.cols == cols &&
      other.rows == rows &&
      other.overlapPct == overlapPct;

  @override
  int get hashCode =>
      Object.hash(widthDeg, heightDeg, rotationDeg, cols, rows, overlapPct);
}

/// Derives the framing overlay from the Frame toggle ([frameModeEnabledProvider]),
/// the profile optics ([opticsSettingsProvider], #468), and the framing controls
/// ([framingControllerProvider]: rotation + mosaic cols/rows/overlap). The FOV
/// getters are arcminutes → /60 to degrees for the atlas overlay.
///
/// [FovBox] implements `==` deliberately (unlike the sibling settings models):
/// this provider feeds an `AladinView.ref.listen` that fires an expensive native
/// `executeJavaScript` redraw, so value-equality keeps an unchanged recompute
/// (e.g. an unrelated optics field, or a rebuild) from triggering a redundant
/// round-trip into the CEF browser.
final frameFovBoxProvider = Provider<FovBox?>((ref) {
  if (!ref.watch(frameModeEnabledProvider)) return null;
  final optics = ref.watch(opticsSettingsProvider);
  final widthArcmin = optics.fovWidthArcmin;
  final heightArcmin = optics.fovHeightArcmin;
  if (widthArcmin == null || heightArcmin == null) return null;
  final framing = ref.watch(framingControllerProvider);
  return FovBox(
    widthDeg: widthArcmin / 60.0,
    heightDeg: heightArcmin / 60.0,
    rotationDeg: framing.rotationDeg,
    cols: framing.mosaicCols,
    rows: framing.mosaicRows,
    overlapPct: framing.mosaicOverlapPct,
  );
});
