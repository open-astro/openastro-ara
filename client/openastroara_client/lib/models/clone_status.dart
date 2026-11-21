/// Client model for §43-2b restore progress — the daemon's clone-status from
/// `GET /api/v1/backup/clone-status`: the background restore worker's live state
/// (`idle` → `running` → `done`/`failed`). Snake_case wire; defensive parse.
library;

class CloneStatus {
  /// `idle` (no restore this session), `running`, `done`, or `failed`.
  final String state;

  /// 0–100 while a restore is running/done; `null` when unknown.
  final double? progressPct;

  /// The area currently being restored, when the worker reports one.
  final String? currentArea;

  /// A human-readable detail — the restored areas on `done`, the error on `failed`.
  final String? message;

  const CloneStatus({
    required this.state,
    this.progressPct,
    this.currentArea,
    this.message,
  });

  static const idleState = 'idle';
  static const runningState = 'running';
  static const doneState = 'done';
  static const failedState = 'failed';

  /// The restore has finished (successfully or not) — stop polling.
  bool get isTerminal => state == doneState || state == failedState;

  /// The restore ended in failure.
  bool get isFailed => state == failedState;

  /// A restore is in flight.
  bool get isRunning => state == runningState;

  factory CloneStatus.fromJson(Map<String, dynamic> json) => CloneStatus(
        state: json['state'] is String ? json['state'] as String : idleState,
        progressPct: json['progress_pct'] is num ? (json['progress_pct'] as num).toDouble() : null,
        currentArea: json['current_area'] is String ? json['current_area'] as String : null,
        message: json['message'] is String ? json['message'] as String : null,
      );
}
