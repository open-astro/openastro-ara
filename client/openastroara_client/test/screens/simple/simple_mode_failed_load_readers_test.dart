import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015, #1807: callers that tell a failed section load from a good one.
// Today the load throws when the rig is unreachable; on the section module it
// returns false and keeps the failure (`load()`'s result). These readers must
// test that result, so a failed load never reads as loaded. They pass today and
// guard the move: each runs the real notifier over FakeSectionRig, no override
// of the section's provider.

/// The rig's copy of each section the readers load (full wire forms, so a
/// strict reader takes them).
final _sections = <String, Object?>{
  'site': ProfileApi.siteSettingsToJson(
    const SiteSettings(
      siteName: 'Rig site',
      latitudeDeg: 51.477833,
      longitudeDeg: -0.0015,
      elevationM: 42,
    ),
  ),
  'optics': ProfileApi.opticsToJson(
    const OpticsSettings(focalLengthMm: 800, apertureMm: 100),
  ),
  'phd2': ProfileApi.phd2SettingsToJson(
    const Phd2Settings(guideFocalLength: 240, guidePixelSize: 3.75),
  ),
};

void main() {
  for (final failing in ['optics']) {
    test('the rig setup check reads as not loaded when the $failing load '
        'fails', () async {
      final rig = FakeSectionRig(_sections)..failGet.add(failing);
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig.api())],
      );
      addTearDown(c.dispose);
      c.listen(rigSetupHydratedProvider, (_, _) {});
      expect(await c.read(rigSetupHydratedProvider.future), isFalse);
    });
  }
}
