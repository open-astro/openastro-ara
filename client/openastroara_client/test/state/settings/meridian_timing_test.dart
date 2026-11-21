import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

/// #2044 Testing Decision 6 — Simple Mode's signed "At the meridian" value and
/// the three wire fields it writes.
void main() {
  const base = SafetyPolicies(meridianPauseMin: 3, minutesAfterMeridian: 7);

  test('the signed meridian value -10, 0 and +5 writes pause, after and max', () {
    final before = withMeridianTimingMin(base, -10);
    expect(
      (before.meridianPauseMin, before.minutesAfterMeridian, before.maxMinutesAfterMeridian),
      (10, 0, 5),
    );
    final on = withMeridianTimingMin(base, 0);
    expect((on.meridianPauseMin, on.minutesAfterMeridian, on.maxMinutesAfterMeridian), (0, 0, 5));
    final after = withMeridianTimingMin(base, 5);
    expect(
      (after.meridianPauseMin, after.minutesAfterMeridian, after.maxMinutesAfterMeridian),
      (0, 5, 10),
    );
    for (final v in [-10, 0, 5]) {
      expect(meridianTimingMin(withMeridianTimingMin(base, v)), v);
    }
  });

  test('a non-zero pause reads as before the meridian, as the daemon applies it', () {
    // The defaults (pause 5, after 5): the pause is a hard stop, so the flip waits.
    expect(meridianTimingMin(const SafetyPolicies()), -5);
    expect(meridianTimingMin(const SafetyPolicies(meridianPauseMin: 0, minutesAfterMeridian: 15)), 15);
  });

  test('the meridian window and stop-before fields round-trip on the wire, Off as null', () {
    final json = ProfileApi.safetyPoliciesToJson(
      const SafetyPolicies(minutesAfterMeridian: 15, maxMinutesAfterMeridian: 20, stopBeforeMeridianMin: 10),
    );
    expect(json['minutes_after_meridian'], 15);
    expect(json['max_minutes_after_meridian'], 20);
    expect(json['stop_before_meridian_min'], 10);
    final back = ProfileApi.safetyPoliciesFromJson(json);
    expect((back.minutesAfterMeridian, back.maxMinutesAfterMeridian, back.stopBeforeMeridianMin), (15, 20, 10));
    expect(ProfileApi.safetyPoliciesToJson(const SafetyPolicies())['stop_before_meridian_min'], isNull);
    final old = ProfileApi.safetyPoliciesFromJson(const {});
    expect((old.minutesAfterMeridian, old.maxMinutesAfterMeridian, old.stopBeforeMeridianMin), (5, 10, 0),
        reason: 'an older daemon sends none: NINA defaults, stop-before off');
  });
}
