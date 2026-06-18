/// Client model for the §38 sequencer list endpoint (`GET /api/v1/sequences`).
/// Snake_case wire; defensive parse — missing/wrong-typed fields degrade rather
/// than throw, matching the other client models. The full sequence body (the
/// §38.1 JSON DOM) is fetched separately and parsed by the tree layer.
library;

String? _str(dynamic v) => v is String ? v : null;
int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
DateTime? _dt(dynamic v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;

/// §28.12 sequence lifecycle state machine — mirrors the daemon's
/// `SequenceRunState` enum, serialized as a snake_case string on the wire.
enum SequenceRunState {
  idle,
  starting,
  running,
  paused,
  aborting,
  stopped,
  completed,
  failed;

  /// Parse the wire string (snake_case == lowercase here, all single words);
  /// returns null for null/unknown so an unrecognised state degrades to "no
  /// badge" rather than throwing.
  static SequenceRunState? fromWire(dynamic v) {
    if (v is! String) return null;
    for (final s in SequenceRunState.values) {
      if (s.name == v) return s;
    }
    return null;
  }

  /// True while a run is active (running or starting) — the toolbar uses this to
  /// gate Pause/Abort vs Start.
  bool get isActive =>
      this == SequenceRunState.running || this == SequenceRunState.starting;
}

/// One row in the sequence list — daemon's `SequenceListItemDto`.
class SequenceListItem {
  final String id;
  final String name;
  final String? description;
  final DateTime? createdUtc;
  final DateTime? modifiedUtc;
  final SequenceRunState? currentRunState;
  final int instructionCount;
  final int targetCount;
  final String? templateOrigin;

  const SequenceListItem({
    required this.id,
    this.name = '',
    this.description,
    this.createdUtc,
    this.modifiedUtc,
    this.currentRunState,
    this.instructionCount = 0,
    this.targetCount = 0,
    this.templateOrigin,
  });

  factory SequenceListItem.fromJson(Map<String, dynamic> json) {
    return SequenceListItem(
      id: _str(json['id']) ?? '',
      name: _str(json['name']) ?? '',
      description: _str(json['description']),
      createdUtc: _dt(json['created_utc']),
      modifiedUtc: _dt(json['modified_utc']),
      currentRunState: SequenceRunState.fromWire(json['current_run_state']),
      instructionCount: _int(json['instruction_count']),
      targetCount: _int(json['target_count']),
      templateOrigin: _str(json['template_origin']),
    );
  }

  @override
  bool operator ==(Object other) =>
      other is SequenceListItem &&
      other.id == id &&
      other.name == name &&
      other.description == description &&
      other.createdUtc == createdUtc &&
      other.modifiedUtc == modifiedUtc &&
      other.currentRunState == currentRunState &&
      other.instructionCount == instructionCount &&
      other.targetCount == targetCount &&
      other.templateOrigin == templateOrigin;

  @override
  int get hashCode => Object.hash(id, name, description, createdUtc, modifiedUtc,
      currentRunState, instructionCount, targetCount, templateOrigin);
}
