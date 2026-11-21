import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/status_indicator.dart';

// DESIGN.md status rule: a device working normally (exposing, slewing,
// moving, guiding) blinks green, wherever its status shows (user, 2026-11-04).
void main() {
  Future<double> opacityAfter(WidgetTester t, {required bool on, bool reduceMotion = false}) async {
    await t.pumpWidget(MediaQuery(
      data: MediaQueryData(disableAnimations: reduceMotion),
      child: Directionality(
        textDirection: TextDirection.ltr,
        child: StatusBlink(on: on, child: const SizedBox(width: 10, height: 10)),
      ),
    ));
    await t.pump(const Duration(milliseconds: 350));
    return t.widget<FadeTransition>(find.byType(FadeTransition)).opacity.value;
  }

  testWidgets('on: it blinks', (t) async {
    expect(await opacityAfter(t, on: true), lessThan(1));
  });

  testWidgets('off: steady', (t) async {
    expect(await opacityAfter(t, on: false), 1);
  });

  testWidgets('Reduce Motion: steady, even when on (WCAG 2.3.3)', (t) async {
    expect(await opacityAfter(t, on: true, reduceMotion: true), 1);
  });
}
