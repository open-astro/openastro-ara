import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import '../support/fake_section_rig.dart';
import '../support/settings_values.dart';

/// #1138 — the safety-policies wire mapping for the flip recovery policies and
/// the explicit unattended window. The daemon reads an unknown mode as its
/// default without an error, so a typo in the string table would silently lose
/// a user's choice: pin every token.
void main() {
  group('safety policies JSON (#1138)', () {
    test('every mode token round-trips verbatim', () {
      for (final mode in MeridianRefocusMode.values) {
        final json = ProfileApi.safetyPoliciesToJson(
          const TestSafetyPolicies().copyWith(meridianRefocus: mode),
        );
        expect(json['meridian_refocus'], isA<String>());
        expect(
          ProfileApi.safetyPoliciesFromJson(json).meridianRefocus,
          mode,
        );
      }
      for (final mode in GuiderRecalMode.values) {
        final json = ProfileApi.safetyPoliciesToJson(
          const TestSafetyPolicies().copyWith(meridianGuiderRecal: mode),
        );
        expect(ProfileApi.safetyPoliciesFromJson(json).meridianGuiderRecal, mode);
      }
    });

    test('the wire tokens are the daemon\'s', () {
      final json = ProfileApi.safetyPoliciesToJson(
        const TestSafetyPolicies().copyWith(
          meridianRefocus: MeridianRefocusMode.ifTempDrifted,
          meridianRefocusTempThresholdC: 3.5,
          meridianGuiderRecal: GuiderRecalMode.autoRestore,
        ),
      );
      expect(json['meridian_refocus'], 'if_temp_drifted');
      expect(json['meridian_refocus_temp_threshold_c'], 3.5);
      expect(json['meridian_guider_recal'], 'auto_restore');
      expect(
        ProfileApi.safetyPoliciesToJson(
          const TestSafetyPolicies().copyWith(meridianRefocus: MeridianRefocusMode.always),
        )['meridian_refocus'],
        'always',
      );
      expect(
        ProfileApi.safetyPoliciesToJson(
          const TestSafetyPolicies().copyWith(meridianGuiderRecal: GuiderRecalMode.full),
        )['meridian_guider_recal'],
        'full',
      );
      expect(
        ProfileApi.safetyPoliciesToJson(
          const TestSafetyPolicies().copyWith(meridianGuiderRecal: GuiderRecalMode.skip),
        )['meridian_guider_recal'],
        'skip',
      );
    });

    test('a blank window end is written as null and read back blank', () {
      final blank = ProfileApi.safetyPoliciesToJson(const TestSafetyPolicies());
      expect(blank.containsKey('unattended_window_start'), isTrue);
      expect(blank['unattended_window_start'], isNull);
      expect(blank['unattended_window_end'], isNull);
      final back = ProfileApi.safetyPoliciesFromJson(blank);
      expect(back.unattendedWindowStart, '');
      expect(back.unattendedWindowEnd, '');

      final set = ProfileApi.safetyPoliciesToJson(
        const TestSafetyPolicies().copyWith(unattendedWindowStart: '22:00', unattendedWindowEnd: '6:00'),
      );
      expect(set['unattended_window_start'], '22:00');
      expect(set['unattended_window_end'], '6:00');
      expect(ProfileApi.safetyPoliciesFromJson(set).unattendedWindowEnd, '6:00');
    });

    test('a null guider mode stays null and saves as null; a missing field or '
        'an unknown mode fails the read', () {
      Map<String, dynamic> rig(Map<String, dynamic> over) =>
          {...rigDefaults['safety']!, ...over};
      // ADR 0015 rule 3: no client default stands in for a field.
      expect(() => ProfileApi.safetyPoliciesFromJson({'on_unsafe': 'pause_and_park'}), throwsA(anything));
      expect(() => ProfileApi.safetyPoliciesFromJson(rig({'meridian_guider_recal': 'bogus'})), throwsFormatException);
      // A profile from before the mode stays null, and a save keeps it null with the legacy bool
      // untouched: the daemon keeps resuming guiding when a guider is connected, and a user who
      // had switched the re-calibration off is not silently moved to "skip" (or to "required").
      final legacy = ProfileApi.safetyPoliciesFromJson(rig({'meridian_recal_guider': false}));
      expect(legacy.meridianGuiderRecal, isNull);
      expect(legacy.meridianRecalGuider, isFalse);
      final saved = ProfileApi.safetyPoliciesToJson(legacy);
      expect(saved['meridian_guider_recal'], isNull);
      expect(saved['meridian_recal_guider'], isFalse);
      expect(
          ProfileApi.safetyPoliciesFromJson(rig({'meridian_recal_guider': true, 'meridian_guider_recal': 'skip'}))
              .meridianGuiderRecal,
          GuiderRecalMode.skip);
    });
  });
}
