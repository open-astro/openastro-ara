import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/sequencer/rig_capabilities_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

void main() {
  group('guiderConfigured', () {
    test('a daemon-side guider counts whatever its link state', () {
      const dropped = GuiderStatus(
        name: 'PHD2',
        connectionState: GuiderConnectionState.disconnected,
        runtimeState: GuiderRuntimeState.unknown,
      );
      expect(guiderConfigured(dropped, const Phd2Settings()), isTrue);
    });
    test('a named guide camera counts without a daemon status', () {
      expect(
        guiderConfigured(
          null,
          const Phd2Settings(guiderCamera: 'Alpaca Camera [rc91.lan:6800/1]'),
        ),
        isTrue,
      );
      expect(
        guiderConfigured(
          null,
          const Phd2Settings(guiderCameraId: 'ZWO_ASI120MM_0'),
        ),
        isTrue,
      );
    });
    test('nothing configured is no guider', () {
      expect(
        guiderConfigured(null, const Phd2Settings(guiderCamera: '  ')),
        isFalse,
      );
    });
  });

  test(
    'rigCapabilitiesFromSettings assumes focuser + wheel, never a rotator',
    () {
      final rig = rigCapabilitiesFromSettings(
        const Phd2Settings(guiderCamera: 'cam'),
      );
      expect(rig.focuser, isTrue);
      expect(rig.filterWheel, isTrue);
      expect(rig.rotator, isFalse);
      expect(rig.guider, isTrue);
      expect(rigCapabilitiesFromSettings(const Phd2Settings()).guider, isFalse);
    },
  );
}
