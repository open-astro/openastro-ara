/// §44 backup-stream wire models (`/api/v1/server/backup-stream/*`).
///
/// Tolerant parsing per the client convention: absent/mistyped keys fall back
/// to safe defaults so an older or newer daemon never crashes the panel.
library;

/// GET /backup-stream/status.
class BackupStreamStatus {
  final bool enabled;
  final String? activeTarget;
  final int pendingCount;
  final int syncedCount;
  final int queueSizeBytes;

  const BackupStreamStatus({
    required this.enabled,
    required this.activeTarget,
    required this.pendingCount,
    required this.syncedCount,
    required this.queueSizeBytes,
  });

  factory BackupStreamStatus.fromJson(Map<String, dynamic> json) => BackupStreamStatus(
        enabled: json['enabled'] is bool ? json['enabled'] as bool : false,
        activeTarget: json['active_target'] as String?,
        pendingCount: json['pending_count'] is int ? json['pending_count'] as int : 0,
        syncedCount: json['synced_count'] is int ? json['synced_count'] as int : 0,
        queueSizeBytes: json['queue_size_bytes'] is int ? json['queue_size_bytes'] as int : 0,
      );
}

/// One pending frame from GET /backup-stream/queue (oldest first). A null
/// [sha256] means the daemon couldn't hash the file yet (per-page hash budget
/// or an unreadable file) — skip it this poll; it returns hashed later.
class BackupStreamQueueEntry {
  final String id;
  final String? sha256;
  final int sizeBytes;
  final DateTime? capturedAt;
  final String sessionId;

  const BackupStreamQueueEntry({
    required this.id,
    required this.sha256,
    required this.sizeBytes,
    required this.capturedAt,
    required this.sessionId,
  });

  factory BackupStreamQueueEntry.fromJson(Map<String, dynamic> json) => BackupStreamQueueEntry(
        id: json['id'] as String? ?? '',
        sha256: json['sha256'] as String?,
        sizeBytes: json['size_bytes'] is int ? json['size_bytes'] as int : 0,
        capturedAt: DateTime.tryParse(json['captured_at'] as String? ?? '')?.toUtc(),
        sessionId: json['session_id'] as String? ?? '',
      );
}
