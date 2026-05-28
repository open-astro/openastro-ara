import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §54 Notifications settings — channel toggles + trigger toggles + per-
/// channel tokens. Phase 12h.2-notifications holds the values in memory;
/// 12h.2b will wire `/api/v1/profile/notifications` for daemon round-trip.

class NotificationsSettings {
  // Channels.
  final bool inAppBanner;
  final bool osDesktop;
  final bool soundAlert;
  final String pushoverToken;
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

class NotificationsSettingsNotifier extends Notifier<NotificationsSettings> {
  @override
  NotificationsSettings build() => const NotificationsSettings();

  void setInAppBanner(bool v) => state = state.copyWith(inAppBanner: v);
  void setOsDesktop(bool v) => state = state.copyWith(osDesktop: v);
  void setSoundAlert(bool v) => state = state.copyWith(soundAlert: v);
  void setPushoverToken(String s) =>
      state = state.copyWith(pushoverToken: s.trim());
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
}

final notificationsSettingsProvider =
    NotifierProvider<NotificationsSettingsNotifier, NotificationsSettings>(
        NotificationsSettingsNotifier.new);
