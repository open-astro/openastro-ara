/// Shared wire-timestamp parsing for the §50 stats models.
library;

/// Parses a daemon-emitted wire timestamp to a UTC [DateTime].
///
/// A string with any timezone designator (`Z` or a numeric offset like
/// `±HH:MM`) is parsed by Dart with `isUtc == true`, already converted to the
/// correct UTC instant. A zone-LESS string parses as local time — which would
/// shift it by the client's offset — so its wall-clock fields are reinterpreted
/// as UTC instead (the daemon emits UTC). Returns `null` for a non-string,
/// empty, or unparseable value.
///
/// This is the single source of truth for all stats models; the older
/// `DateTime.tryParse(v)?.toUtc()` form some of them used mis-shifted a
/// zone-less timestamp by the client offset.
DateTime? parseStatsUtc(dynamic v) {
  if (v is! String || v.isEmpty) return null;
  final parsed = DateTime.tryParse(v);
  if (parsed == null) return null;
  if (parsed.isUtc) return parsed;
  return DateTime.utc(parsed.year, parsed.month, parsed.day, parsed.hour,
      parsed.minute, parsed.second, parsed.millisecond, parsed.microsecond);
}
