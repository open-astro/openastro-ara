import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/widgets/status_indicator.dart';

// iOS Reduce Motion arrives as AccessibilityFeatures.reduceMotion, which
// MediaQuery.disableAnimations does not reflect (Android's flag only).
void main() {
  Future<double> opacity(WidgetTester t, {bool disableAnimations = false}) async {
    await t.pumpWidget(MediaQuery(
      data: MediaQueryData(disableAnimations: disableAnimations),
      child: Directionality(
        textDirection: TextDirection.ltr,
        child: StatusBlink(on: true, child: const SizedBox(width: 10, height: 10)),
      ),
    ));
    await t.pump(const Duration(milliseconds: 350));
    return t.widget<FadeTransition>(find.byType(FadeTransition)).opacity.value;
  }

  void features(WidgetTester t, {bool reduceMotion = false}) {
    t.platformDispatcher.accessibilityFeaturesTestValue =
        FakeAccessibilityFeatures(reduceMotion: reduceMotion);
    addTearDown(t.platformDispatcher.clearAccessibilityFeaturesTestValue);
  }

  testWidgets('reduceMotion only: steady', (t) async {
    features(t, reduceMotion: true);
    expect(await opacity(t), 1);
  });

  testWidgets('disableAnimations only: steady', (t) async {
    features(t);
    expect(await opacity(t, disableAnimations: true), 1);
  });

  testWidgets('neither: blinks', (t) async {
    features(t);
    expect(await opacity(t), lessThan(1));
  });
}
