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
  });
}
