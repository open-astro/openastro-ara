/// §58.14 / #1138 — the pre-sleep checklist the daemon builds on
/// `GET /api/v1/preflight/today`: graded facts (equipment, storage, solver,
/// cooler, mount, the running sequence), the running target's meridian
/// crossing tonight, the unattended window and the device that will sound the
/// alarm. The client shows it once before bed and on Setup → Before you sleep.
class PreflightItem {
  final String key;

  /// `ok`, `warn`, `fail` or `unknown` (the daemon could not read the fact).
  final String status;
  final String title;
  final String detail;
  const PreflightItem({
    required this.key,
    required this.status,
    required this.title,
    required this.detail,
  });

  static PreflightItem? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final key = json['key'];
    final status = json['status'];
    if (key is! String || status is! String) return null;
    return PreflightItem(
      key: key,
      status: status,
      title: json['title'] is String ? json['title'] as String : key,
      detail: json['detail'] is String ? json['detail'] as String : '',
    );
  }
}

class PreflightFlip {
  final String target;
  final DateTime atUtc;
  final double altitudeDeg;

  /// `ok`, `soft_warning` (below the site's advisory altitude) or
  /// `below_floor` (the flight check will refuse that flip).
  final String standing;
  const PreflightFlip({
    required this.target,
    required this.atUtc,
    required this.altitudeDeg,
    required this.standing,
  });

  static PreflightFlip? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final at = json['at_utc'];
    final alt = json['altitude_deg'];
    if (at is! String || alt is! num) return null;
    final when = DateTime.tryParse(at);
    if (when == null) return null;
    return PreflightFlip(
      target: json['target'] is String ? json['target'] as String : '?',
      atUtc: when.toUtc(),
      altitudeDeg: alt.toDouble(),
      standing: json['standing'] is String ? json['standing'] as String : 'ok',
    );
  }
}

class PreflightChecklist {
  final DateTime generatedUtc;
  final List<PreflightItem> items;
  final List<PreflightFlip> flips;
  final int unforecastTargets;
  final String? unattendedWindowStart;
  final String? unattendedWindowEnd;

  /// `clock` (the profile's explicit window) or `sun` (astronomical darkness).
  final String unattendedWindowSource;
  final String? alarmDevice;
  final bool alarmSoundEnabled;
  final bool allGood;

  const PreflightChecklist({
    required this.generatedUtc,
    required this.items,
    this.flips = const [],
    this.unforecastTargets = 0,
    this.unattendedWindowStart,
    this.unattendedWindowEnd,
    this.unattendedWindowSource = 'sun',
    this.alarmDevice,
    this.alarmSoundEnabled = false,
    required this.allGood,
  });

  static PreflightChecklist? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    final gen = json['generated_utc'];
    final items = json['items'];
    if (gen is! String || items is! List) return null;
    final at = DateTime.tryParse(gen);
    if (at == null) return null;
    final flips = json['flips'];
    return PreflightChecklist(
      generatedUtc: at.toUtc(),
      items: items
          .map(PreflightItem.fromJson)
          .whereType<PreflightItem>()
          .toList(),
      flips: flips is List
          ? flips
                .map(PreflightFlip.fromJson)
                .whereType<PreflightFlip>()
                .toList()
          : const [],
      unforecastTargets: (json['unforecast_targets'] as num?)?.toInt() ?? 0,
      unattendedWindowStart: json['unattended_window_start'] as String?,
      unattendedWindowEnd: json['unattended_window_end'] as String?,
      unattendedWindowSource: json['unattended_window_source'] is String
          ? json['unattended_window_source'] as String
          : 'sun',
      alarmDevice: json['alarm_device'] as String?,
      alarmSoundEnabled: json['alarm_sound_enabled'] == true,
      allGood: json['all_good'] == true,
    );
  }

  bool get hasFailure =>
      items.any((i) => i.status == 'fail') ||
      flips.any((f) => f.standing == 'below_floor');
  bool get hasWarning =>
      items.any((i) => i.status == 'warn' || i.status == 'unknown') ||
      flips.any((f) => f.standing == 'soft_warning');

  /// The unattended line as the card prints it.
  String get unattendedLine =>
      unattendedWindowSource == 'clock' &&
          unattendedWindowStart != null &&
          unattendedWindowEnd != null
      ? 'Unattended hours: $unattendedWindowStart — $unattendedWindowEnd'
      : 'Unattended hours: astronomical darkness (the sun decides)';

  /// The alarm line: the controlling client is the device that sounds it.
  String get alarmLine {
    if (alarmDevice == null) return 'Alarm device: no client connected';
    return alarmSoundEnabled
        ? 'Alarm device: $alarmDevice (alarm sound on)'
        : 'Alarm device: $alarmDevice — alarm sound is OFF';
  }
}

/// The night a local wall-clock instant belongs to, as `yyyy-mm-dd` of the
/// EVENING: anything before local noon is still last night. The once-per-night
/// acknowledgement is keyed on it, so a 23:00 "all good" holds through a 03:00
/// reconnect.
String preflightNightKey(DateTime local) {
  final evening = local.hour < 12
      ? local.subtract(const Duration(days: 1))
      : local;
  String two(int n) => n.toString().padLeft(2, '0');
  return '${evening.year}-${two(evening.month)}-${two(evening.day)}';
}

/// Whether the pre-sleep card should fire now. Pure — unit-tested. It fires
/// once per night (by [nightKey]) when a sequence is running and the site is
/// dark; a user who has already answered tonight is never asked twice, and
/// nothing fires in daylight or with nothing running (there is nothing to
/// sleep through).
bool shouldPromptPreflight({
  required bool sequenceRunning,
  required bool dark,
  required String nightKey,
  required Set<String> acknowledgedNights,
}) => sequenceRunning && dark && !acknowledgedNights.contains(nightKey);
