import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';

/// WCAG relative luminance of an opaque colour.
double _luminance(Color c) {
  double lin(double v) =>
      v <= 0.03928 ? v / 12.92 : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
  return 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
}

/// The WCAG contrast ratio of two opaque colours.
double contrastRatio(Color a, Color b) {
  final x = _luminance(a), y = _luminance(b);
  return (math.max(x, y) + 0.05) / (math.min(x, y) + 0.05);
}

/// The selection tint over the panel scrim over a white frame: the worst
/// case DESIGN.md names (a bright flat behind a Simple Mode panel).
Color selectionTintOnBrightFrame() {
  final panel = Color.alphaBlend(AraColors.overlayScrim, Colors.white);
  return Color.alphaBlend(AraColors.selectionTint, panel);
}

/// The colour the first text under [of] is painted in.
Color renderedTextColor(WidgetTester tester, Finder of) {
  final paragraph = tester.renderObject<RenderParagraph>(of);
  return paragraph.text.style!.color!;
}

/// Fails unless the text found by [of] reads at 4.5:1 on the selection tint.
void expectReadsOnSelectionTint(WidgetTester tester, Finder of) {
  final ratio = contrastRatio(
    renderedTextColor(tester, of),
    selectionTintOnBrightFrame(),
  );
  expect(
    ratio,
    greaterThanOrEqualTo(4.5),
    reason: 'WCAG AA for text, over the tint on a bright frame',
  );
}
