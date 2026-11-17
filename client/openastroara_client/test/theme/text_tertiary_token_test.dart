import 'dart:math' as math;
import 'dart:ui';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/theme/ara_colors.dart';

/// [fg] over [bg], WCAG ratio.
double _contrast(Color fg, Color bg) {
  final a = fg.computeLuminance(), b = bg.computeLuminance();
  return (math.max(a, b) + 0.05) / (math.min(a, b) + 0.05);
}

// Own file: until the token exists this file does not compile (#1843).
void main() {
  for (final (name, bg) in const [
    ('bg-panel', AraColors.bgPanel),
    ('bg-panel-alt', AraColors.bgPanelAlt),
    ('bg-input', AraColors.bgInput),
  ]) {
    test('text-tertiary reads at 4.5:1 on $name', () {
      expect(_contrast(AraColors.textTertiary, bg), greaterThanOrEqualTo(4.5));
    });
  }

  test('text-tertiary sits between text-secondary and text-disabled', () {
    expect(AraColors.textTertiary, const Color(0xFF9B9B9B));
    expect(
      AraColors.textTertiary.computeLuminance(),
      lessThan(AraColors.textSecondary.computeLuminance()),
    );
    expect(
      AraColors.textTertiary.computeLuminance(),
      greaterThan(AraColors.textDisabled.computeLuminance()),
    );
  });
}
