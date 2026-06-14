/// Client model for §50.6 Calendar heatmap — the daemon's `StatsCalendarDto`
/// from `GET /api/v1/stats/calendar`: per-day frame counts + integration hours
/// + the targets imaged that night, for days that have captures in the queried
/// range. Snake_case wire; defensive parse.
library;

int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;

/// A `DateOnly` wire value ("yyyy-MM-dd") as a date-only [DateTime]. The daemon
/// sends a bare date (the calendar day), so we keep its y/m/d fields verbatim.
/// Were a full timestamp ever sent, `tryParse` yields the parsed fields and we
/// still take its y/m/d — which for the daemon's UTC days is the intended day.
DateTime? _date(dynamic v) {
  if (v is! String || v.isEmpty) return null;
  final d = DateTime.tryParse(v);
  if (d == null) return null;
  return DateTime(d.year, d.month, d.day);
}

/// One captured day.
class CalendarDay {
  final DateTime date;
  final int frameCount;
  final double integrationHours;
  final List<String> targetsImaged;

  const CalendarDay({
    required this.date,
    this.frameCount = 0,
    this.integrationHours = 0.0,
    this.targetsImaged = const [],
  });

  int get integrationMinutes => (integrationHours * 60).round();

  static CalendarDay? fromJson(Map<String, dynamic> json) {
    final date = _date(json['date']);
    if (date == null) return null; // a day without a parseable date is unusable
    final targets = json['targets_imaged'];
    return CalendarDay(
      date: date,
      frameCount: _int(json['frame_count']),
      integrationHours: _dbl(json['integration_hours']),
      targetsImaged: targets is List
          ? [for (final t in targets) if (t is String) t]
          : const [],
    );
  }
}

/// The captured days in the queried window, keyed by date for the heatmap.
class CalendarStats {
  final List<CalendarDay> days;

  const CalendarStats({this.days = const []});

  bool get isEmpty => days.isEmpty;

  /// Integration minutes per `yyyy-MM-dd` key, for shading heatmap cells.
  Map<String, int> get minutesByDay => {
        for (final d in days) dayKey(d.date): d.integrationMinutes,
      };

  /// Canonical `yyyy-MM-dd` key — also the wire format `CalendarApi` sends for
  /// the `fromDate`/`toDate` query, so both sides format dates identically.
  static String dayKey(DateTime d) =>
      '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  factory CalendarStats.fromJson(Map<String, dynamic> json) {
    final raw = json['days'];
    final days = raw is List
        ? [
            for (final e in raw)
              if (e is Map<String, dynamic>) CalendarDay.fromJson(e),
          ].whereType<CalendarDay>().toList()
        : const <CalendarDay>[];
    return CalendarStats(days: days);
  }
}
