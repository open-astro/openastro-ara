import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/state/sequencer/rig_capabilities_state.dart';
import '../../support/settings_values.dart';

void main() {
  group('guiderConfigured', () {
    test('a daemon-side guider counts whatever its link state', () {
      const dropped = GuiderStatus(
        name: 'PHD2',
        connectionState: GuiderConnectionState.disconnected,
        runtimeState: GuiderRuntimeState.unknown,
      );
      expect(guiderConfigured(dropped, const TestPhd2Settings()), isTrue);
    });
    test('a named guide camera counts without a daemon status', () {
      expect(
        guiderConfigured(
          null,
          const TestPhd2Settings(guiderCamera: 'Alpaca Camera [rc91.lan:6800/1]'),
        ),
        isTrue,
      );
      expect(
        guiderConfigured(
          null,
          const TestPhd2Settings(guiderCameraId: 'ZWO_ASI120MM_0'),
        ),
        isTrue,
      );
    });
    test('nothing configured is no guider', () {
      expect(
        guiderConfigured(null, const TestPhd2Settings(guiderCamera: '  ')),
        isFalse,
      );
    });
    test("the guider's None camera choice is no guider", () {
      expect(
        guiderConfigured(null, const TestPhd2Settings(guiderCamera: 'None')),
        isFalse,
      );
    });
  });

  test(
    'rigCapabilitiesFromSettings assumes focuser + wheel, never a rotator',
    () {
      final rig = rigCapabilitiesFromSettings(
        const TestPhd2Settings(guiderCamera: 'cam'),
      );
      expect(rig.focuser, isTrue);
      expect(rig.filterWheel, isTrue);
      expect(rig.rotator, isFalse);
      expect(rig.guider, isTrue);
      expect(rigCapabilitiesFromSettings(const TestPhd2Settings()).guider, isFalse);
    },
  );
}
