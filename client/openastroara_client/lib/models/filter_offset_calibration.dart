import 'filter_wheel_status.dart';

/// #1675 — one filter of an offset calibration
/// (`GET /api/v1/equipment/filterwheel/offsets/calibration` → `filters[]`).
/// [status] is `pending` | `measured` | `failed` | `skipped`; [offset] is
/// steps from the reference, null until there is a baseline.
class FilterOffsetRow {
  final int position;
  final String name;
  final String status;
  final int? bestPosition;
  final int? offset;
  final double? rSquared;
  final int previousOffset;
  final String? failureReason;

  const FilterOffsetRow({
    required this.position,
    required this.name,
    required this.status,
    this.bestPosition,
    this.offset,
    this.rSquared,
    this.previousOffset = 0,
    this.failureReason,
  });

  bool get isMeasured => status == 'measured';
  bool get isFailed => status == 'failed';

  /// The full UI's weak-fit line: a fit under R² 0.8 (`MainFocusCard`).
  bool get isWeak => isMeasured && rSquared != null && rSquared! < 0.8;

  factory FilterOffsetRow.fromJson(Map<String, dynamic> json) => FilterOffsetRow(
    position: (json['position'] as num?)?.toInt() ?? 0,
    name: json['name'] as String? ?? '',
    status: json['status'] as String? ?? 'pending',
    bestPosition: (json['best_position'] as num?)?.toInt(),
    offset: (json['offset'] as num?)?.toInt(),
    rSquared: (json['r_squared'] as num?)?.toDouble(),
    previousOffset: (json['previous_offset'] as num?)?.toInt() ?? 0,
    failureReason: json['failure_reason'] as String?,
  );
}

/// #1675 — the daemon's offset calibration: running, or its results waiting
/// for Save or Discard. [state] is `running` | `complete` | `failed` |
/// `cancelled`; [runIndex] of [runCount] is the autofocus in progress, on the
/// filter at [currentPosition].
class FilterOffsetCalibration {
  final String state;
  final String jobId;
  final int referencePosition;
  final String referenceName;
  final int? referenceFirstPosition;
  final int? referenceSecondPosition;
  final int? cfzSteps;
  final bool driftChecked;
  final bool driftWarning;
  final int? currentPosition;
  final int runIndex;
  final int runCount;
  final String? reason;
  final List<FilterOffsetRow> filters;

  const FilterOffsetCalibration({
    required this.state,
    this.jobId = '',
    required this.referencePosition,
    required this.referenceName,
    this.referenceFirstPosition,
    this.referenceSecondPosition,
    this.cfzSteps,
    this.driftChecked = false,
    this.driftWarning = false,
    this.currentPosition,
    this.runIndex = 0,
    this.runCount = 0,
    this.reason,
    this.filters = const [],
  });

  bool get isRunning => state == 'running';
  bool get isComplete => state == 'complete';

  /// The filter the run is on: its name, else the slot's number.
  String? get currentName {
    final p = currentPosition;
    if (p == null) return null;
    for (final f in filters) {
      if (f.position == p && f.name.isNotEmpty) return f.name;
    }
    return p == referencePosition && referenceName.isNotEmpty ? referenceName : null;
  }

  factory FilterOffsetCalibration.fromJson(Map<String, dynamic> json) {
    final rows = json['filters'];
    return FilterOffsetCalibration(
      state: json['state'] as String? ?? 'running',
      jobId: json['job_id'] as String? ?? '',
      referencePosition: (json['reference_position'] as num?)?.toInt() ?? 0,
      referenceName: json['reference_name'] as String? ?? '',
      referenceFirstPosition: (json['reference_first_position'] as num?)?.toInt(),
      referenceSecondPosition: (json['reference_second_position'] as num?)?.toInt(),
      cfzSteps: (json['cfz_steps'] as num?)?.toInt(),
      driftChecked: json['drift_checked'] as bool? ?? false,
      driftWarning: json['drift_warning'] as bool? ?? false,
      currentPosition: (json['current_position'] as num?)?.toInt(),
      runIndex: (json['run_index'] as num?)?.toInt() ?? 0,
      runCount: (json['run_count'] as num?)?.toInt() ?? 0,
      reason: json['reason'] as String?,
      filters: rows is List
          ? rows.whereType<Map<String, dynamic>>().map(FilterOffsetRow.fromJson).toList(growable: false)
          : const [],
    );
  }
}

/// The picks a calibration starts with, as the daemon's: every named slot
/// except a dark. Pure — unit-tested through the panel.
Set<int> defaultOffsetPicks(List<FilterSlot> slots) => {
  for (final s in slots)
    if (s.name.trim().isNotEmpty && !s.name.toLowerCase().contains('dark')) s.position,
};

/// The reference filter the daemon will choose: the profile's autofocus
/// filter, else a slot named L or Lum, else the lowest pick (ignoring case).
int? offsetReference(List<FilterSlot> slots, Set<int> picks, String afFilter) {
  int? named(bool Function(String) match) {
    for (final s in slots) {
      final n = s.name.trim().toLowerCase();
      if (n.isNotEmpty && match(n)) return s.position;
    }
    return null;
  }

  final af = afFilter.trim().toLowerCase();
  return (af.isEmpty ? null : named((n) => n == af)) ??
      named((n) => n == 'l' || n == 'lum') ??
      (picks.isEmpty ? null : picks.reduce((a, b) => a < b ? a : b));
}
