import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';

void main() {
  group('NotificationsSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults — channels + triggers on, tokens empty', () {
      final s = container.read(notificationsSettingsProvider);
      expect(s.inAppBanner, isTrue);
      expect(s.osDesktop, isTrue);
      expect(s.soundAlert, isTrue);
      expect(s.pushoverToken, '');
      expect(s.telegramBotToken, '');
      expect(s.onSequenceComplete, isTrue);
      expect(s.onSequencePaused, isTrue);
      expect(s.onCriticalDiagnostic, isTrue);
      expect(s.onSafetyEvent, isTrue);
      expect(s.onAutofocusFailed, isTrue);
      expect(s.onPlateSolveFailed, isTrue);
      expect(s.onDiskSpaceLow, isTrue);
    });

    test('channel toggles flip state independently', () {
      final n = container.read(notificationsSettingsProvider.notifier);
      n.setInAppBanner(false);
      expect(container.read(notificationsSettingsProvider).inAppBanner, isFalse);
      expect(container.read(notificationsSettingsProvider).osDesktop, isTrue);
    });

    test('setPushoverToken trims whitespace', () {
      final n = container.read(notificationsSettingsProvider.notifier);
      n.setPushoverToken('  abc-123  ');
      expect(container.read(notificationsSettingsProvider).pushoverToken,
          'abc-123');
    });

    test('setTelegramBotToken trims whitespace', () {
      final n = container.read(notificationsSettingsProvider.notifier);
      n.setTelegramBotToken('\tbot-xyz\n');
      expect(container.read(notificationsSettingsProvider).telegramBotToken,
          'bot-xyz');
    });

    test('all 7 triggers can be disabled individually', () {
      final n = container.read(notificationsSettingsProvider.notifier);
      n.setOnSequenceComplete(false);
      n.setOnSequencePaused(false);
      n.setOnCriticalDiagnostic(false);
      n.setOnSafetyEvent(false);
      n.setOnAutofocusFailed(false);
      n.setOnPlateSolveFailed(false);
      n.setOnDiskSpaceLow(false);
      final s = container.read(notificationsSettingsProvider);
      expect(s.onSequenceComplete, isFalse);
      expect(s.onSequencePaused, isFalse);
      expect(s.onCriticalDiagnostic, isFalse);
      expect(s.onSafetyEvent, isFalse);
      expect(s.onAutofocusFailed, isFalse);
      expect(s.onPlateSolveFailed, isFalse);
      expect(s.onDiskSpaceLow, isFalse);
    });

    test('copyWith preserves untouched fields', () {
      const a = NotificationsSettings(
        inAppBanner: false,
        osDesktop: true,
        pushoverToken: 'k',
      );
      final b = a.copyWith(osDesktop: false);
      expect(b.inAppBanner, isFalse);
      expect(b.osDesktop, isFalse);
      expect(b.pushoverToken, 'k');
    });
  });
}
