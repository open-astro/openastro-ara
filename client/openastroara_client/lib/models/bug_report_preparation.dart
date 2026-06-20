import 'package:flutter/foundation.dart';

/// Result of `POST /api/v1/bugreport/prepare` (§54) — a staged bundle ready to
/// download. Mirrors the server `BugReportPreparationDto` (snake_case wire).
@immutable
class BugReportPreparation {
  final String preparationId;
  final String status;
  final int estimatedSizeBytes;
  final DateTime? completedUtc;

  const BugReportPreparation({
    required this.preparationId,
    required this.status,
    required this.estimatedSizeBytes,
    this.completedUtc,
  });

  factory BugReportPreparation.fromJson(Map<String, dynamic> json) {
    final completed = json['completed_utc'] as String?;
    return BugReportPreparation(
      preparationId: (json['preparation_id'] as String?) ?? '',
      status: (json['status'] as String?) ?? 'unknown',
      // tolerate int or num on the wire
      estimatedSizeBytes: (json['estimated_size_bytes'] as num?)?.toInt() ?? 0,
      completedUtc:
          completed != null ? DateTime.tryParse(completed)?.toUtc() : null,
    );
  }
}
