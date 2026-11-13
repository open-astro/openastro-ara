/// The observing night a moment belongs to: its local date after taking off
/// 12 h, so a run at 01:00 counts with the evening before it (the same rule the
/// daemon files calibration frames by). Pure.
String observingNight(DateTime t) {
  final d = t.toLocal().subtract(const Duration(hours: 12));
  String two(int v) => v.toString().padLeft(2, '0');
  return '${d.year}-${two(d.month)}-${two(d.day)}';
}

/// [t] falls in the observing night [now] is in. Null never is.
bool isTonight(DateTime? t, DateTime now) =>
    t != null && observingNight(t) == observingNight(now);
