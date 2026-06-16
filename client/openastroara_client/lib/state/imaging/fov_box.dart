import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../settings/optics_settings_state.dart';
import 'framing_state.dart';

/// §36/§25.5 — the field-of-view rectangle the Planning **Frame** overlay draws
/// on the Aladin atlas: the profile's sensor seen through its optics, sized in
/// **degrees** and oriented at the framing rotation. Null when Frame mode is off
/// or the optics aren't configured (so the overlay clears instead of drawing a
/// fake box).
class FovBox {
  final double widthDeg;
  final double heightDeg;

  /// Position angle in degrees (the framing rotation).
  final double rotationDeg;

  const FovBox({
    required this.widthDeg,
    required this.heightDeg,
    required this.rotationDeg,
  });

  @override
  bool operator ==(Object other) =>
      other is FovBox &&
      other.widthDeg == widthDeg &&
      other.heightDeg == heightDeg &&
      other.rotationDeg == rotationDeg;

  @override
  int get hashCode => Object.hash(widthDeg, heightDeg, rotationDeg);
}

/// Derives the FOV box from the Frame toggle ([frameModeEnabledProvider]), the
/// profile optics ([opticsSettingsProvider], #468), and the framing rotation
/// ([framingControllerProvider]). The FOV getters are arcminutes → /60 to
/// degrees for the atlas overlay.
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
  final rotationDeg =
      ref.watch(framingControllerProvider.select((p) => p.rotationDeg));
  return FovBox(
    widthDeg: widthArcmin / 60.0,
    heightDeg: heightArcmin / 60.0,
    rotationDeg: rotationDeg,
  );
});
