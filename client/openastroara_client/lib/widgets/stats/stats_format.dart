/// Shared display formatters for the §50 Stats views, so the Overview, Targets,
/// and other sections render integration time + dates identically.
library;

/// Whole-hour metrics read as "12h"; fractional ones add minutes ("12h 30m").
/// A non-finite or negative value renders as an em dash.
String formatIntegrationHours(double hours) {
  if (!hours.isFinite || hours < 0) return '—';
  final totalMinutes = (hours * 60).round();
  final h = totalMinutes ~/ 60;
  final m = totalMinutes % 60;
  return m == 0 ? '${h}h' : '${h}h ${m.toString().padLeft(2, '0')}m';
}

const _months = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

/// A UTC instant rendered in the local zone as "14 Jun 2026"; `null` → em dash.
String formatStatsDate(DateTime? utc) {
  if (utc == null) return '—';
  final d = utc.toLocal();
  return '${d.day} ${_months[d.month - 1]} ${d.year}';
}
