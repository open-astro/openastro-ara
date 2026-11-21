import '../settings/autofocus_settings_state.dart';

/// #1544 — "Keep focus sharp": one pick that sets all four of the profile's
/// autofocus triggers (filter change, temperature, HFR drift, every N hours).
/// [custom] is what the profile shows when its triggers match no preset; it is
/// never applied.
enum KeepFocusSharp {
  relaxed(tempC: 3, hfrPct: 25, hours: 4),
  normal(tempC: 2, hfrPct: 15, hours: 2),
  tight(tempC: 1, hfrPct: 10, hours: 1),
  custom(tempC: 0, hfrPct: 0, hours: 0);

  const KeepFocusSharp({
    required this.tempC,
    required this.hfrPct,
    required this.hours,
  });

  final double tempC;
  final double hfrPct;
  final int hours;

  /// The preset applied when autofocus during plans is switched back on:
  /// today's defaults.
  static const KeepFocusSharp onDefault = normal;
}

/// Autofocus during plans is on while any of the four triggers can fire. Off
/// means plans never refocus by themselves (the Autofocus button still works).
bool autofocusDuringPlans(AutofocusSettings s) =>
    s.runAfterFilterChange ||
    s.triggerTempDeltaC > 0 ||
    s.triggerHfrDriftPct > 0 ||
    s.everyNHours > 0;

/// The preset the four triggers match, [KeepFocusSharp.custom] when they
/// match none, and null while autofocus during plans is off.
KeepFocusSharp? keepFocusSharpOf(AutofocusSettings s) {
  if (!autofocusDuringPlans(s)) return null;
  for (final p in KeepFocusSharp.values) {
    if (p == KeepFocusSharp.custom) continue;
    if (s.runAfterFilterChange &&
        s.triggerTempDeltaC == p.tempC &&
        s.triggerHfrDriftPct == p.hfrPct &&
        s.everyNHours == p.hours) {
      return p;
    }
  }
  return KeepFocusSharp.custom;
}

/// [s] with all four triggers set to [p] (a no-op for [KeepFocusSharp.custom]).
AutofocusSettings withKeepFocusSharp(AutofocusSettings s, KeepFocusSharp p) =>
    p == KeepFocusSharp.custom
    ? s
    : s.copyWith(
        runAfterFilterChange: true,
        triggerTempDeltaC: p.tempC,
        triggerHfrDriftPct: p.hfrPct,
        everyNHours: p.hours,
      );

/// #1544 — "Autofocus during plans" set to [on]: unchanged when it already
/// reads so, the Normal preset when switched on, every trigger off when off.
AutofocusSettings withAutofocusDuringPlans(AutofocusSettings s, bool on) {
  if (on == autofocusDuringPlans(s)) return s;
  return on
      ? withKeepFocusSharp(s, KeepFocusSharp.onDefault)
      : withAutofocusDuringPlansOff(s);
}

/// [s] with every trigger off.
AutofocusSettings withAutofocusDuringPlansOff(AutofocusSettings s) =>
    s.copyWith(
      runAfterFilterChange: false,
      triggerTempDeltaC: 0,
      triggerHfrDriftPct: 0,
      everyNHours: 0,
    );
