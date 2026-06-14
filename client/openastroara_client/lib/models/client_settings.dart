/// Client model for §55.1 multi-device settings sync: the opaque UI-preferences
/// blob from `GET /api/v1/client-settings` (the daemon's `ClientSettingsDto`).
/// Snake_case wire; defensive parse — a missing/wrong-typed body degrades to an
/// empty map rather than throwing.
library;

DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;

/// The synced preferences map plus when the server last stored it. The map is
/// opaque key/value UI state owned by the client — the server never interprets
/// it. [updatedUtc] is null when nothing has been saved yet.
class ClientSettings {
  final Map<String, dynamic> settings;
  final DateTime? updatedUtc;

  const ClientSettings({
    this.settings = const <String, dynamic>{},
    this.updatedUtc,
  });

  /// True when the server has no stored preferences yet (a fresh profile).
  bool get isEmpty => settings.isEmpty;

  factory ClientSettings.fromJson(Map<String, dynamic> json) {
    final raw = json['settings'];
    return ClientSettings(
      settings: raw is Map<String, dynamic>
          ? Map<String, dynamic>.unmodifiable(raw)
          : const <String, dynamic>{},
      updatedUtc: _dt(json['updated_utc']),
    );
  }
}
