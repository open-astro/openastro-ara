import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';

void main() {
  group('autofocus settings codec', () {
    test('step_size_auto decodes, defaults to on when absent, and round-trips off', () {
      // A profile written before §59.8 has no key: automatic is the daemon default.
      expect(ProfileApi.autofocusSettingsFromJson(const {}).stepSizeAuto, isTrue);
      expect(ProfileApi.autofocusSettingsFromJson(const {'step_size_auto': false}).stepSizeAuto, isFalse);

      // A user's "off" must survive the PUT: dropping the key would reset it to auto on the next save.
      final off = ProfileApi.autofocusSettingsFromJson(const {'step_size_auto': false, 'step_size': 80});
      final wire = ProfileApi.autofocusSettingsToJson(off);
      expect(wire['step_size_auto'], isFalse);
      expect(wire['step_size'], 80);
      expect(ProfileApi.autofocusSettingsFromJson(wire).stepSizeAuto, isFalse);
    });

    test('backlash in/out steps (#1541) default to 0 and round-trip', () {
      final none = ProfileApi.autofocusSettingsFromJson(const {});
      expect(none.backlashInSteps, 0);
      expect(none.backlashOutSteps, 0);

      final set = ProfileApi.autofocusSettingsFromJson(const {'backlash_in_steps': 30, 'backlash_out_steps': 50});
      final wire = ProfileApi.autofocusSettingsToJson(set);
      expect(wire['backlash_in_steps'], 30);
      expect(wire['backlash_out_steps'], 50);
    });
  });
}
