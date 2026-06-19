/// Client model for the §38 sequencer list endpoint (`GET /api/v1/sequences`).
/// Snake_case wire; defensive parse — missing/wrong-typed fields degrade rather
/// than throw, matching the other client models. The full sequence body (the
/// §38.1 JSON DOM) is fetched separately and parsed by the tree layer.
library;

import 'package:collection/collection.dart' show DeepCollectionEquality;
import 'package:flutter/foundation.dart' show listEquals;

String? _str(dynamic v) => v is String ? v : null;
int _int(dynamic v) => v is int ? v : (v is num ? v.toInt() : 0);
int? _intOrNull(dynamic v) => v is int ? v : (v is num ? v.toInt() : null);
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

  /// True while a run is in progress — i.e. NOT idle and NOT in a terminal
  /// state. The toolbar uses this to gate Start (enabled only when inactive) vs
  /// Pause/Abort (only when active). Exhaustive switch so a newly-added state
  /// can't silently fall through to one side (e.g. `aborting` must count as
  /// active, or Start would wrongly appear enabled mid-abort).
  bool get isActive => switch (this) {
        SequenceRunState.idle ||
        SequenceRunState.completed ||
        SequenceRunState.stopped ||
        SequenceRunState.failed =>
          false,
        SequenceRunState.starting ||
        SequenceRunState.running ||
        SequenceRunState.paused ||
        SequenceRunState.aborting =>
          true,
      };
}

/// One page of the sequence list — the daemon's `CursorPage<SequenceListItemDto>`.
/// Carries [hasMore] / [nextCursor] so a caller can tell "all loaded" from
/// "first page of many" and page further; dropping them would silently truncate
/// a long list.
class SequencePage {
  final List<SequenceListItem> items;
  final bool hasMore;
  final String? nextCursor;

  const SequencePage({
    required this.items,
    this.hasMore = false,
    this.nextCursor,
  });

  @override
  bool operator ==(Object other) =>
      other is SequencePage &&
      other.hasMore == hasMore &&
      other.nextCursor == nextCursor &&
      listEquals(other.items, items);

  @override
  int get hashCode => Object.hash(hasMore, nextCursor, Object.hashAll(items));
}

/// Result of a NINA sequence-file import (`POST /api/v1/sequences/import`) —
/// daemon's `SequenceImportResultDto` (§38.4). [warnings] /
/// [droppedInstructionTypes] explain what the translation couldn't carry over;
/// [lossyTranslation] is true when anything was dropped or approximated.
class SequenceImportResult {
  /// Null when the daemon didn't return an id — distinct from an empty string so
  /// the UI never treats an absent id as a real, selectable sequence.
  final String? createdSequenceId;
  final String name;
  final List<String> warnings;
  final List<String> droppedInstructionTypes;
  final bool lossyTranslation;

  const SequenceImportResult({
    this.createdSequenceId,
    this.name = '',
    this.warnings = const [],
    this.droppedInstructionTypes = const [],
    this.lossyTranslation = false,
  });

  factory SequenceImportResult.fromJson(Map<String, dynamic> json) {
    List<String> strings(dynamic v) =>
        v is List ? v.whereType<String>().toList(growable: false) : const [];
    return SequenceImportResult(
      createdSequenceId: _str(json['created_sequence_id']),
      name: _str(json['name']) ?? '',
      warnings: strings(json['warnings']),
      droppedInstructionTypes: strings(json['dropped_instruction_types']),
      lossyTranslation: json['lossy_translation'] == true,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is SequenceImportResult &&
      other.createdSequenceId == createdSequenceId &&
      other.name == name &&
      other.lossyTranslation == lossyTranslation &&
      listEquals(other.warnings, warnings) &&
      listEquals(other.droppedInstructionTypes, droppedInstructionTypes);

  @override
  int get hashCode => Object.hash(createdSequenceId, name, lossyTranslation,
      Object.hashAll(warnings), Object.hashAll(droppedInstructionTypes));
}

/// Full detail of one saved sequence (`GET /api/v1/sequences/{id}`) — daemon's
/// `SequenceDto`. [body] is the raw §38.1 / NINA JSON DOM the daemon stores
/// VERBATIM; it is the source of truth the client keeps so Save (`PATCH`) and
/// Export round-trip faithfully (no lossy reconstruction from the display tree).
/// The display tree is derived from [body] via `parseNinaSequenceBody`.
class SequenceDetail {
  final String id;
  final String name;
  final String? description;
  final Map<String, dynamic> body;
  final String? templateOrigin;

  const SequenceDetail({
    required this.id,
    this.name = '',
    this.description,
    this.body = const <String, dynamic>{},
    this.templateOrigin,
  });

  factory SequenceDetail.fromJson(Map<String, dynamic> json) => SequenceDetail(
        id: _str(json['id']) ?? '',
        name: _str(json['name']) ?? '',
        description: _str(json['description']),
        body: json['body'] is Map<String, dynamic>
            ? json['body'] as Map<String, dynamic>
            : const <String, dynamic>{},
        templateOrigin: _str(json['template_origin']),
      );

  // NOTE: like the PATCH payload, a null arg means "keep current" — there's no
  // way to CLEAR description back to null here. Fine for the create/rename flows;
  // if save-b's editor needs to blank a description, add a clear sentinel.
  SequenceDetail copyWith({String? name, String? description, Map<String, dynamic>? body}) =>
      SequenceDetail(
        id: id,
        name: name ?? this.name,
        description: description ?? this.description,
        body: body ?? this.body,
        templateOrigin: templateOrigin,
      );

  // DeepCollectionEquality (not mapEquals): the NINA body is deeply nested JSON,
  // and mapEquals compares nested-Map values by identity — so two fresh parses
  // of the same body would never be ==, breaking save-b's "did the body change?"
  // check. Deep equality is also order-independent, matching the deep hash below.
  static const _bodyEq = DeepCollectionEquality();

  @override
  bool operator ==(Object other) =>
      other is SequenceDetail &&
      other.id == id &&
      other.name == name &&
      other.description == description &&
      other.templateOrigin == templateOrigin &&
      _bodyEq.equals(other.body, body);

  @override
  int get hashCode => Object.hash(
      id, name, description, templateOrigin, _bodyEq.hash(body));
}

/// A starting-point sequence template (`GET /api/v1/sequences/templates`) —
/// daemon's `SequenceTemplateDto` (§38.6/§38.7). The full template body (the
/// NINA DOM) isn't carried here; "New" instantiates by [name] server-side, which
/// returns the created sequence. Snake_case wire; defensive parse.
class SequenceTemplate {
  final String name;
  final String category;
  final String? description;
  final bool isBuiltIn;

  const SequenceTemplate({
    required this.name,
    this.category = '',
    this.description,
    this.isBuiltIn = false,
  });

  factory SequenceTemplate.fromJson(Map<String, dynamic> json) =>
      SequenceTemplate(
        name: _str(json['name']) ?? '',
        category: _str(json['category']) ?? '',
        description: _str(json['description']),
        isBuiltIn: json['is_built_in'] == true,
      );

  @override
  bool operator ==(Object other) =>
      other is SequenceTemplate &&
      other.name == name &&
      other.category == category &&
      other.description == description &&
      other.isBuiltIn == isBuiltIn;

  @override
  int get hashCode => Object.hash(name, category, description, isBuiltIn);
}

/// Live run state of an active sequence — daemon's `SequenceRunStateDto`
/// (`GET /api/v1/sequences/{id}/state`). null from the API means no active run.
class SequenceRunStateInfo {
  final String sequenceId;
  final String runId;
  /// Nullable on purpose: an unrecognized/absent wire state stays `null` rather
  /// than silently degrading to `idle` (which would wrongly enable Start on a
  /// running sequence). Mirrors [SequenceListItem.currentRunState].
  final SequenceRunState? state;
  final int? currentInstructionIndex;
  final String? currentTargetName;
  final DateTime? startedUtc;
  final DateTime? completedUtc;
  final int framesCompleted;
  final int framesTotal;
  final String? currentInstructionDescription;

  const SequenceRunStateInfo({
    this.sequenceId = '',
    this.runId = '',
    this.state,
    this.currentInstructionIndex,
    this.currentTargetName,
    this.startedUtc,
    this.completedUtc,
    this.framesCompleted = 0,
    this.framesTotal = 0,
    this.currentInstructionDescription,
  });

  factory SequenceRunStateInfo.fromJson(Map<String, dynamic> json) {
    return SequenceRunStateInfo(
      sequenceId: _str(json['sequence_id']) ?? '',
      runId: _str(json['run_id']) ?? '',
      state: SequenceRunState.fromWire(json['state']),
      currentInstructionIndex: _intOrNull(json['current_instruction_index']),
      currentTargetName: _str(json['current_target_name']),
      startedUtc: _dt(json['started_utc']),
      completedUtc: _dt(json['completed_utc']),
      framesCompleted: _int(json['frames_completed']),
      framesTotal: _int(json['frames_total']),
      currentInstructionDescription: _str(json['current_instruction_description']),
    );
  }

  /// Fold a live `sequence.*` WS frame onto this snapshot. The WS payload carries
  /// only the fast-changing fields (state, instruction index, frame counts, run
  /// id); the slower fields the REST `getRunState` provides — target name,
  /// start/complete times, instruction description — are preserved so a live
  /// update doesn't blank them. An unparseable/absent `state` keeps the current
  /// one rather than degrading a running sequence to "no state".
  SequenceRunStateInfo applyWsProgress(Map<String, dynamic> payload) {
    return SequenceRunStateInfo(
      sequenceId: _str(payload['sequence_id']) ?? sequenceId,
      runId: _str(payload['run_id']) ?? runId,
      state: SequenceRunState.fromWire(payload['state']) ?? state,
      // `?? <current>` (not `_int`'s 0-fallback) so a present-but-unparseable
      // count preserves the last-known value instead of flashing 0/0.
      currentInstructionIndex:
          _intOrNull(payload['current_instruction_index']) ??
              currentInstructionIndex,
      currentTargetName: currentTargetName,
      startedUtc: startedUtc,
      completedUtc: completedUtc,
      framesCompleted:
          _intOrNull(payload['frames_completed']) ?? framesCompleted,
      framesTotal: _intOrNull(payload['frames_total']) ?? framesTotal,
      currentInstructionDescription: currentInstructionDescription,
    );
  }

  // Value equality so a poll that returns identical state doesn't churn rebuilds
  // (the run controls/status line watch this) — matches the other models here.
  @override
  bool operator ==(Object other) =>
      other is SequenceRunStateInfo &&
      other.sequenceId == sequenceId &&
      other.runId == runId &&
      other.state == state &&
      other.currentInstructionIndex == currentInstructionIndex &&
      other.currentTargetName == currentTargetName &&
      other.startedUtc == startedUtc &&
      other.completedUtc == completedUtc &&
      other.framesCompleted == framesCompleted &&
      other.framesTotal == framesTotal &&
      other.currentInstructionDescription == currentInstructionDescription;

  @override
  int get hashCode => Object.hash(
      sequenceId,
      runId,
      state,
      currentInstructionIndex,
      currentTargetName,
      startedUtc,
      completedUtc,
      framesCompleted,
      framesTotal,
      currentInstructionDescription);
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
