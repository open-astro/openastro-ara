import 'package:flutter/foundation.dart';

/// One daemon log line from `POST /api/v1/server/logs/tail` (§29.9).
///
/// Mirrors the server `LogEntryDto` (snake_case wire): `timestamp`, `level`,
/// `source`, `message`. The `properties` blob is intentionally not surfaced —
/// the tail view shows the rendered message.
@immutable
class LogEntry {
  final DateTime timestamp;
  final String level;
  final String source;
  final String message;

  const LogEntry({
    required this.timestamp,
    required this.level,
    required this.source,
    required this.message,
  });

  factory LogEntry.fromJson(Map<String, dynamic> json) {
    final ts = json['timestamp'] as String?;
    return LogEntry(
      // A torn/absent timestamp degrades to the epoch rather than throwing, so a
      // single malformed entry can't blank the whole tail.
      timestamp: (ts != null ? DateTime.tryParse(ts)?.toUtc() : null) ??
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      level: (json['level'] as String?) ?? 'Information',
      source: (json['source'] as String?) ?? '',
      message: (json['message'] as String?) ?? '',
    );
  }
}
