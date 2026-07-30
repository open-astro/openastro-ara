import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';

void main() {
  group('Phd2SettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match playbook §63', () {
      final s = container.read(phd2SettingsProvider);
      expect(s.host, 'localhost');
      expect(s.port, 4400);
      expect(s.phd2Profile, 'Default');
      expect(s.ditherEnabled, isTrue);
      expect(s.ditherEveryNFrames, 1);
      expect(s.ditherPixels, 5.0);
      expect(s.settlePixels, 1.5);
      expect(s.settleTimeSec, 10);
      expect(s.settleTimeoutSec, 60);
      expect(s.forceCalibrationEachSession, isFalse);
      // §63.19 — guide-scope setup by default (focal length user-entered).
      expect(s.guiderSetupType, 'guide_scope');
      // §63.17 guider equipment selection — "" / 0 = unset.
      expect(s.guiderCamera, '');
      expect(s.guiderCameraId, '');
      expect(s.guiderMount, '');
      expect(s.guiderAuxMount, '');
      expect(s.guiderRotator, '');
      expect(s.guiderAlpacaHost, '');
      expect(s.guiderAlpacaPort, 0);
    });

    test('§63.19 setGuiderSetupType accepts known tokens, normalizes case', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setGuiderSetupType('oag');
      expect(container.read(phd2SettingsProvider).guiderSetupType, 'oag');
      n.setGuiderSetupType('guide_scope');
      expect(
          container.read(phd2SettingsProvider).guiderSetupType, 'guide_scope');
      n.setGuiderSetupType('  OAG  ');
      expect(container.read(phd2SettingsProvider).guiderSetupType, 'oag');
    });

    test('§63.19 unknown setup-type tokens coerce to guide_scope', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setGuiderSetupType('oag');
      n.setGuiderSetupType('newtonian_oag_v2');
      expect(
          container.read(phd2SettingsProvider).guiderSetupType, 'guide_scope');
      expect(Phd2SettingsNotifier.normalizeGuiderSetupType(''), 'guide_scope');
      expect(Phd2SettingsNotifier.normalizeGuiderSetupType('bogus'),
          'guide_scope');
      expect(Phd2SettingsNotifier.normalizeGuiderSetupType('OAG'), 'oag');
    });

    test('§63.17 equipment setters trim and accept empty (= unset)', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setGuiderCamera('  ZWO ASI120MM  ');
      n.setGuiderCameraId(' cam-1 ');
      n.setGuiderMount(' On-camera ');
      n.setGuiderAuxMount(' INDI Mount ');
      n.setGuiderRotator(' Alpaca Rotator ');
      n.setGuiderAlpacaHost(' 192.168.1.20 ');
      var s = container.read(phd2SettingsProvider);
      expect(s.guiderCamera, 'ZWO ASI120MM');
      expect(s.guiderCameraId, 'cam-1');
      expect(s.guiderMount, 'On-camera');
      expect(s.guiderAuxMount, 'INDI Mount');
      expect(s.guiderRotator, 'Alpaca Rotator');
      expect(s.guiderAlpacaHost, '192.168.1.20');

      // Empty is a valid value — it clears the selection back to daemon default.
      n.setGuiderCamera('  ');
      n.setGuiderAlpacaHost('');
      s = container.read(phd2SettingsProvider);
      expect(s.guiderCamera, '');
      expect(s.guiderAlpacaHost, '');
    });

    test('setGuiderAlpacaPort accepts 0 (= unset) and rejects out-of-range', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setGuiderAlpacaPort(11111);
      expect(container.read(phd2SettingsProvider).guiderAlpacaPort, 11111);
      n.setGuiderAlpacaPort(-1);
      n.setGuiderAlpacaPort(65536);
      expect(container.read(phd2SettingsProvider).guiderAlpacaPort, 11111);
      n.setGuiderAlpacaPort(0);
      expect(container.read(phd2SettingsProvider).guiderAlpacaPort, 0);
    });

    test('setHost trims + rejects empty', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setHost('');
      n.setHost('   ');
      expect(container.read(phd2SettingsProvider).host, 'localhost');
      n.setHost('  192.168.1.50  ');
      expect(container.read(phd2SettingsProvider).host, '192.168.1.50');
    });

    test('setPort rejects privileged + out-of-range', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setPort(0);
      n.setPort(80);
      n.setPort(1023);
      n.setPort(65536);
      expect(container.read(phd2SettingsProvider).port, 4400);
      n.setPort(4401);
      expect(container.read(phd2SettingsProvider).port, 4401);
    });

    test('setPhd2Profile trims + rejects empty', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setPhd2Profile('');
      expect(container.read(phd2SettingsProvider).phd2Profile, 'Default');
      n.setPhd2Profile('  Imaging rig 1  ');
      expect(container.read(phd2SettingsProvider).phd2Profile, 'Imaging rig 1');
    });

    test('positive-only setters reject zero/negative', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setDitherEveryNFrames(0);
      n.setDitherEveryNFrames(-1);
      n.setSettleTimeoutSec(0);
      n.setSettleTimeoutSec(-1);
      final s = container.read(phd2SettingsProvider);
      expect(s.ditherEveryNFrames, 1);
      expect(s.settleTimeoutSec, 60);
    });

    test('non-negative-allowing setters reject negative', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setDitherPixels(-1);
      n.setSettlePixels(-1);
      n.setSettleTimeSec(-1);
      final s = container.read(phd2SettingsProvider);
      expect(s.ditherPixels, 5.0);
      expect(s.settlePixels, 1.5);
      expect(s.settleTimeSec, 10);
      // Zero is allowed (instant settle).
      n.setSettleTimeSec(0);
      expect(container.read(phd2SettingsProvider).settleTimeSec, 0);
    });

    test('boolean toggles assign directly', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setDitherEnabled(false);
      n.setForceCalibrationEachSession(true);
      final s = container.read(phd2SettingsProvider);
      expect(s.ditherEnabled, isFalse);
      expect(s.forceCalibrationEachSession, isTrue);
    });

    test('§63.5 guider-engine defaults', () {
      final s = container.read(phd2SettingsProvider);
      expect(s.guideFocalLength, 0);
      expect(s.guidePixelSize, 0);
      expect(s.raAggressiveness, 0.7);
      expect(s.decAggressiveness, 0.7);
      expect(s.minimumMove, 0.15);
      expect(s.decGuideMode, 'auto');
    });

    test('§63.5 setters validate ranges (mirror the server)', () {
      final n = container.read(phd2SettingsProvider.notifier);
      // Aggressiveness rejects outside [0,1].
      n.setRaAggressiveness(1.5);
      n.setRaAggressiveness(-0.1);
      expect(container.read(phd2SettingsProvider).raAggressiveness, 0.7);
      n.setRaAggressiveness(0.85);
      expect(container.read(phd2SettingsProvider).raAggressiveness, 0.85);
      // Focal / pixel / min-move reject negatives.
      n.setGuideFocalLength(-5);
      n.setMinimumMove(-1);
      expect(container.read(phd2SettingsProvider).guideFocalLength, 0);
      expect(container.read(phd2SettingsProvider).minimumMove, 0.15);
      n.setGuideFocalLength(250);
      expect(container.read(phd2SettingsProvider).guideFocalLength, 250);
    });

    test('§63.5 setDecGuideMode normalizes + rejects unknown', () {
      final n = container.read(phd2SettingsProvider.notifier);
      n.setDecGuideMode('NORTH'); // case-insensitive
      expect(container.read(phd2SettingsProvider).decGuideMode, 'north');
      n.setDecGuideMode('sideways'); // unknown → rejected, keeps prior
      expect(container.read(phd2SettingsProvider).decGuideMode, 'north');
    });
  });
}
