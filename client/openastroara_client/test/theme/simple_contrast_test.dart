import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';

/// [fg] over [bg], WCAG ratio.
double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

void main() {
  // Simple Mode's panels float over the frame, so their text has to read
  // over whatever is behind: a dark sky, or a flat, the Moon, daylight.
  for (final (name, frame) in const [
    ('a dark sky', Color(0xFF0A0A0A)),
    ('a mid-grey frame', Color(0xFF808080)),
    ('a white frame', Color(0xFFFFFFFF)),
  ]) {
    test('panel text reads at 4.5:1 over $name', () {
      final panel = Color.alphaBlend(AraColors.overlayScrim, frame);
      expect(_contrast(AraColors.textPrimary, panel), greaterThanOrEqualTo(4.5));
      expect(_contrast(AraColors.textSecondary, panel), greaterThanOrEqualTo(4.5));
      expect(_contrast(AraColors.errorText, panel), greaterThanOrEqualTo(4.5));
    });
  }
}
