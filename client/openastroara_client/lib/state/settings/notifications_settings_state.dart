import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// §54 Notifications settings — channel toggles + trigger toggles + per-
/// channel tokens. Phase 12h.6d wires the daemon round-trip via
/// [ProfileApi]; local state is still the source of truth between syncs.

class NotificationsSettings {
  // Channels.
  // Both kept only so saved profiles round-trip: no banner or OS notification
  // channel is wired and the toggles are hidden (#1189). Don't read these
  // until one is.
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
  }) => NotificationsSettings(
    inAppBanner: inAppBanner ?? this.inAppBanner,
    osDesktop: osDesktop ?? this.osDesktop,
    soundAlert: soundAlert ?? this.soundAlert,
    pushoverToken: pushoverToken ?? this.pushoverToken,
    pushoverUserKey: pushoverUserKey ?? this.pushoverUserKey,
    telegramChatId: telegramChatId ?? this.telegramChatId,
    telegramBotToken: telegramBotToken ?? this.telegramBotToken,
    onSequenceComplete: onSequenceComplete ?? this.onSequenceComplete,
    onSequencePaused: onSequencePaused ?? this.onSequencePaused,
    onCriticalDiagnostic: onCriticalDiagnostic ?? this.onCriticalDiagnostic,
    onSafetyEvent: onSafetyEvent ?? this.onSafetyEvent,
    onAutofocusFailed: onAutofocusFailed ?? this.onAutofocusFailed,
    onPlateSolveFailed: onPlateSolveFailed ?? this.onPlateSolveFailed,
    onDiskSpaceLow: onDiskSpaceLow ?? this.onDiskSpaceLow,
  );
}

class NotificationsSettingsNotifier
    extends SectionNotifier<NotificationsSettings> {
  void setInAppBanner(bool v) => edit((c) => c.copyWith(inAppBanner: v));
  void setOsDesktop(bool v) => edit((c) => c.copyWith(osDesktop: v));
  void setSoundAlert(bool v) => edit((c) => c.copyWith(soundAlert: v));
  void setPushoverToken(String s) =>
      edit((c) => c.copyWith(pushoverToken: s.trim()));
  void setPushoverUserKey(String s) =>
      edit((c) => c.copyWith(pushoverUserKey: s.trim()));
  void setTelegramChatId(String s) =>
      edit((c) => c.copyWith(telegramChatId: s.trim()));
  void setTelegramBotToken(String s) =>
      edit((c) => c.copyWith(telegramBotToken: s.trim()));
  void setOnSequenceComplete(bool v) =>
      edit((c) => c.copyWith(onSequenceComplete: v));
  void setOnSequencePaused(bool v) =>
      edit((c) => c.copyWith(onSequencePaused: v));
  void setOnCriticalDiagnostic(bool v) =>
      edit((c) => c.copyWith(onCriticalDiagnostic: v));
  void setOnSafetyEvent(bool v) => edit((c) => c.copyWith(onSafetyEvent: v));
  void setOnAutofocusFailed(bool v) =>
      edit((c) => c.copyWith(onAutofocusFailed: v));
  void setOnPlateSolveFailed(bool v) =>
      edit((c) => c.copyWith(onPlateSolveFailed: v));
  void setOnDiskSpaceLow(bool v) => edit((c) => c.copyWith(onDiskSpaceLow: v));

  @override
  Future<Tagged<NotificationsSettings>> fetch(ProfileApi api) =>
      api.getNotificationsSection();

  @override
  Future<Tagged<NotificationsSettings>> send(
    ProfileApi api,
    NotificationsSettings value,
    String? etag,
  ) => api.putNotificationsSection(value, ifMatch: etag);
}

final notificationsSettingsProvider =
    NotifierProvider<
      NotificationsSettingsNotifier,
      LoadedSection<NotificationsSettings>
    >(NotificationsSettingsNotifier.new);
