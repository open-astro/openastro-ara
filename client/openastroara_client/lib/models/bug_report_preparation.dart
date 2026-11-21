import 'package:flutter/foundation.dart';

/// Result of `POST /api/v1/bugreport/prepare` (§54) — a staged bundle ready to
/// download. Mirrors the server `BugReportPreparationDto` (snake_case wire).
@immutable
class BugReportPreparation {
  final String preparationId;
  final String status;
  final int estimatedSizeBytes;
  final DateTime? completedUtc;
  // §54.3 — the sharing mode the daemon built the bundle with ('full' on an older daemon).
  final String sharingMode;

  /// What went in, per source (`ara`, `ara-nina`, `alpacabridge`, `guider`,
  /// `system`, `journal`); empty while preparing or from an older daemon.
  final List<BugReportSourceSummary> sources;

  /// The range the bundle covers (files written since then); null = Ara's
  /// newest logs only.
  final DateTime? sinceUtc;

  /// Why a background preparation failed.
  final String? error;

  const BugReportPreparation({
    required this.preparationId,
    required this.status,
    required this.estimatedSizeBytes,
    this.completedUtc,
    this.sharingMode = 'full',
    this.sources = const [],
    this.sinceUtc,
    this.error,
  });

  /// A whole-rig bundle packs in the background; poll until it is ready.
  bool get isPreparing => status == 'preparing';
  bool get isFailed => status == 'failed';

  factory BugReportPreparation.fromJson(Map<String, dynamic> json) {
    final id = json['preparation_id'] as String?;
    if (id == null || id.isEmpty) {
      // Surface a malformed prepare response at the root rather than letting an
      // empty id fall through to download('') and a confusing downstream 4xx.
      throw const FormatException(
          'Bug-report prepare response is missing preparation_id.');
    }
    final completed = json['completed_utc'] as String?;
    return BugReportPreparation(
      preparationId: id,
      status: (json['status'] as String?) ?? 'unknown',
      // tolerate int or num on the wire
      estimatedSizeBytes: (json['estimated_size_bytes'] as num?)?.toInt() ?? 0,
      completedUtc:
          completed != null ? DateTime.tryParse(completed)?.toUtc() : null,
          sharingMode: json['sharing_mode'] as String? ?? 'full',
      sources: [
        for (final s in (json['sources'] as List?) ?? const [])
          if (s is Map<String, dynamic>) BugReportSourceSummary.fromJson(s),
      ],
      sinceUtc: switch (json['since_utc']) {
        final String v => DateTime.tryParse(v)?.toUtc(),
        _ => null,
      },
      error: json['error'] as String?,
    );
  }
}

/// One source's share of the bundle, as the daemon's manifest reports it.
@immutable
class BugReportSourceSummary {
  final String source;
  final int files;
  final int bytes;

  /// A file was cut to its newest part (a PHD2 debug log can reach 245 MB).
  final bool truncated;

  /// Why the source is missing or partial, when it is.
  final String? note;

  const BugReportSourceSummary({
    required this.source,
    required this.files,
    required this.bytes,
    this.truncated = false,
    this.note,
  });

  factory BugReportSourceSummary.fromJson(Map<String, dynamic> json) =>
      BugReportSourceSummary(
        source: json['source'] as String? ?? '?',
        files: (json['files'] as num?)?.toInt() ?? 0,
        bytes: (json['bytes'] as num?)?.toInt() ?? 0,
        truncated: json['truncated'] as bool? ?? false,
        note: json['note'] as String?,
      );
}
