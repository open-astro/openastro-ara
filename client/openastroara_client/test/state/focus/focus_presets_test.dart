import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/focus/focus_presets.dart';
import '../../support/settings_values.dart';

void main() {
  test('the three presets set all four triggers (#1544)', () {
    const base = TestAutofocusSettings();
    final relaxed = withKeepFocusSharp(base, KeepFocusSharp.relaxed);
    expect(
      (
        relaxed.runAfterFilterChange,
        relaxed.triggerTempDeltaC,
        relaxed.triggerHfrDriftPct,
        relaxed.everyNHours,
      ),
      (true, 3.0, 25.0, 4),
    );
    final tight = withKeepFocusSharp(base, KeepFocusSharp.tight);
    expect(
      (
        tight.runAfterFilterChange,
        tight.triggerTempDeltaC,
        tight.triggerHfrDriftPct,
        tight.everyNHours,
      ),
      (true, 1.0, 10.0, 1),
    );
  });

  test('Normal is the profile default', () {
    expect(keepFocusSharpOf(const TestAutofocusSettings()), KeepFocusSharp.normal);
  });

  test('each preset round-trips; any one trigger changed reads Custom', () {
    for (final p in [
      KeepFocusSharp.relaxed,
      KeepFocusSharp.normal,
      KeepFocusSharp.tight,
    ]) {
      final s = withKeepFocusSharp(const TestAutofocusSettings(), p);
      expect(keepFocusSharpOf(s), p);
      expect(
        keepFocusSharpOf(s.copyWith(triggerTempDeltaC: p.tempC + 0.5)),
        KeepFocusSharp.custom,
      );
      expect(
        keepFocusSharpOf(s.copyWith(triggerHfrDriftPct: p.hfrPct + 1)),
        KeepFocusSharp.custom,
      );
      expect(
        keepFocusSharpOf(s.copyWith(everyNHours: p.hours + 1)),
        KeepFocusSharp.custom,
      );
      expect(
        keepFocusSharpOf(s.copyWith(runAfterFilterChange: false)),
        KeepFocusSharp.custom,
      );
    }
  });

  test('picking a preset over Custom overwrites the triggers', () {
    final custom = const TestAutofocusSettings().copyWith(
      triggerTempDeltaC: 7,
      everyNHours: 9,
    );
    expect(keepFocusSharpOf(custom), KeepFocusSharp.custom);
    expect(
      keepFocusSharpOf(withKeepFocusSharp(custom, KeepFocusSharp.tight)),
      KeepFocusSharp.tight,
    );
    // Custom is never applied.
    expect(withKeepFocusSharp(custom, KeepFocusSharp.custom), custom);
  });

  test('autofocus during plans is off only when all four triggers are', () {
    final off = withAutofocusDuringPlansOff(const TestAutofocusSettings());
    expect(autofocusDuringPlans(off), isFalse);
    expect(keepFocusSharpOf(off), isNull);
    for (final one in [
      off.copyWith(runAfterFilterChange: true),
      off.copyWith(triggerTempDeltaC: 1),
      off.copyWith(triggerHfrDriftPct: 5),
      off.copyWith(everyNHours: 1),
    ]) {
      expect(autofocusDuringPlans(one), isTrue);
    }
  });
}
