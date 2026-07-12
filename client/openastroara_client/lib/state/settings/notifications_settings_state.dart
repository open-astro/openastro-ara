import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// §54 Notifications settings — channel toggles + trigger toggles + per-
/// channel tokens. Phase 12h.6d wires the daemon round-trip via
/// [ProfileApi]; local state is still the source of truth between syncs.

class NotificationsSettings {
  // Channels.
  final bool inAppBanner;
  final bool osDesktop;
  final bool soundAlert;
  final String pushoverToken;
  // §54 push channels — each service needs TWO values: Pushover app token +
  // USER key; Telegram bot token + CHAT id. A channel only sends when both
  // of its values are set on the daemon.
  final String pushoverUserKey;
  final String telegramChatId;
  final String telegramBotToken;

  // Triggers.
  final bool onSequenceComplete;
  final bool onSequencePaused;
  final bool onCriticalDiagnostic;
  final bool onSafetyEvent;
  final bool onAutofocusFailed;
  final bool onPlateSolveFailed;
  final bool onDiskSpaceLow;

  const NotificationsSettings({
    this.inAppBanner = true,
    this.osDesktop = true,
    this.soundAlert = true,
    this.pushoverToken = '',
    this.pushoverUserKey = '',
    this.telegramChatId = '',
    this.telegramBotToken = '',
    this.onSequenceComplete = true,
    this.onSequencePaused = true,
    this.onCriticalDiagnostic = true,
    this.onSafetyEvent = true,
    this.onAutofocusFailed = true,
    this.onPlateSolveFailed = true,
    this.onDiskSpaceLow = true,
  });

  NotificationsSettings copyWith({
    bool? inAppBanner,
    bool? osDesktop,
    bool? soundAlert,
    String? pushoverToken,
    String? pushoverUserKey,
    String? telegramChatId,
    String? telegramBotToken,
    bool? onSequenceComplete,
    bool? onSequencePaused,
    bool? onCriticalDiagnostic,
    bool? onSafetyEvent,
    bool? onAutofocusFailed,
    bool? onPlateSolveFailed,
    bool? onDiskSpaceLow,
  }) =>
      NotificationsSettings(
        inAppBanner: inAppBanner ?? this.inAppBanner,
        osDesktop: osDesktop ?? this.osDesktop,
        soundAlert: soundAlert ?? this.soundAlert,
        pushoverToken: pushoverToken ?? this.pushoverToken,
        pushoverUserKey: pushoverUserKey ?? this.pushoverUserKey,
        telegramChatId: telegramChatId ?? this.telegramChatId,
        telegramBotToken: telegramBotToken ?? this.telegramBotToken,
        onSequenceComplete: onSequenceComplete ?? this.onSequenceComplete,
        onSequencePaused: onSequencePaused ?? this.onSequencePaused,
        onCriticalDiagnostic:
            onCriticalDiagnostic ?? this.onCriticalDiagnostic,
        onSafetyEvent: onSafetyEvent ?? this.onSafetyEvent,
        onAutofocusFailed: onAutofocusFailed ?? this.onAutofocusFailed,
        onPlateSolveFailed: onPlateSolveFailed ?? this.onPlateSolveFailed,
        onDiskSpaceLow: onDiskSpaceLow ?? this.onDiskSpaceLow,
      );
}

class NotificationsSettingsNotifier extends Notifier<NotificationsSettings>
    with SettingsSyncMixin<NotificationsSettings> {
  @override
  NotificationsSettings build() => const NotificationsSettings();

  void setInAppBanner(bool v) => state = state.copyWith(inAppBanner: v);
  void setOsDesktop(bool v) => state = state.copyWith(osDesktop: v);
  void setSoundAlert(bool v) => state = state.copyWith(soundAlert: v);
  void setPushoverToken(String s) =>
      state = state.copyWith(pushoverToken: s.trim());
  void setPushoverUserKey(String s) =>
      state = state.copyWith(pushoverUserKey: s.trim());
  void setTelegramChatId(String s) =>
      state = state.copyWith(telegramChatId: s.trim());
  void setTelegramBotToken(String s) =>
      state = state.copyWith(telegramBotToken: s.trim());
  void setOnSequenceComplete(bool v) =>
      state = state.copyWith(onSequenceComplete: v);
  void setOnSequencePaused(bool v) =>
      state = state.copyWith(onSequencePaused: v);
  void setOnCriticalDiagnostic(bool v) =>
      state = state.copyWith(onCriticalDiagnostic: v);
  void setOnSafetyEvent(bool v) => state = state.copyWith(onSafetyEvent: v);
  void setOnAutofocusFailed(bool v) =>
      state = state.copyWith(onAutofocusFailed: v);
  void setOnPlateSolveFailed(bool v) =>
      state = state.copyWith(onPlateSolveFailed: v);
  void setOnDiskSpaceLow(bool v) => state = state.copyWith(onDiskSpaceLow: v);

  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getNotificationsSettings());

  Future<NotificationsSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putNotificationsSettings(sent));
}

final notificationsSettingsProvider =
    NotifierProvider<NotificationsSettingsNotifier, NotificationsSettings>(
        NotificationsSettingsNotifier.new);
