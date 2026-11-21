import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';

import '../support/fake_section_rig.dart';

void main() {
  group('autofocus settings codec', () {
    test('step_size_auto decodes and round-trips off', () {
      Map<String, dynamic> rig(Map<String, dynamic> over) =>
          {...rigDefaults['autofocus']!, ...over};
      expect(ProfileApi.autofocusSettingsFromJson(rig({})).stepSizeAuto, isTrue);
      expect(ProfileApi.autofocusSettingsFromJson(rig({'step_size_auto': false})).stepSizeAuto, isFalse);

      // A user's "off" must survive the PUT: dropping the key would reset it to auto on the next save.
      final off = ProfileApi.autofocusSettingsFromJson(rig({'step_size_auto': false, 'step_size': 80}));
      final wire = ProfileApi.autofocusSettingsToJson(off);
      expect(wire['step_size_auto'], isFalse);
      expect(wire['step_size'], 80);
      expect(ProfileApi.autofocusSettingsFromJson(wire).stepSizeAuto, isFalse);
    });

    test('backlash in/out steps (#1541) round-trip', () {
      final set = ProfileApi.autofocusSettingsFromJson({
        ...rigDefaults['autofocus']!,
        'backlash_in_steps': 30,
        'backlash_out_steps': 50,
      });
      final wire = ProfileApi.autofocusSettingsToJson(set);
      expect(wire['backlash_in_steps'], 30);
      expect(wire['backlash_out_steps'], 50);
    });
  });
}
