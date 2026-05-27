// In-memory model for a sequence tree (§38.1 schema). The wire format is
// JSON matching `openastroara-sequence-v1`; this Dart class mirrors the
// canonical tree shape (Areas → Targets → Instructions) for the Sequencer
// tab's editor (§25.5.3).
//
// Phase 12d.1 ships only the data class + minimal tree manipulation. Phase
// 12d.2 wires JSON (de)serialization against the OpenAPI schema and the
// `/api/v1/sequences` endpoints. Phase 12d.3 adds NINA import handling.

import 'package:collection/collection.dart';

enum SequenceNodeKind {
  root,
  area,
  target,
  sequentialContainer,
  parallelContainer,
  conditionalContainer,
  loopContainer,
  instruction,
}

// Sentinel for `copyWith` so callers can pass `instructionType: null` to
// explicitly clear it (e.g. converting an instruction node into a container).
// Plain `instructionType: null` is indistinguishable from "not provided"
// without this.
const Object _unset = Object();

/// A single node in the sequence tree. Generic enough to cover all NINA
/// container + instruction types; per-instruction parameters live in
/// [params] (Dart Map → JSON object on the wire).
///
/// `params` and `children` are wrapped in unmodifiable views at construction
/// so external mutation of the passed Map/List can't silently invalidate
/// equality/hash assumptions.
class SequenceNode {
  final String id;
  final SequenceNodeKind kind;
  final String displayName;
  final String? instructionType;
  final Map<String, Object?> params;
  final List<SequenceNode> children;

  SequenceNode({
    required this.id,
    required this.kind,
    required this.displayName,
    this.instructionType,
    Map<String, Object?> params = const <String, Object?>{},
    List<SequenceNode> children = const <SequenceNode>[],
  })  : params = Map<String, Object?>.unmodifiable(params),
        children = List<SequenceNode>.unmodifiable(children);

  bool get isContainer => kind != SequenceNodeKind.instruction;

  SequenceNode copyWith({
    String? displayName,
    Object? instructionType = _unset,
    Map<String, Object?>? params,
    List<SequenceNode>? children,
  }) =>
      SequenceNode(
        id: id,
        kind: kind,
        displayName: displayName ?? this.displayName,
        instructionType: identical(instructionType, _unset)
            ? this.instructionType
            : instructionType as String?,
        params: params ?? this.params,
        children: children ?? this.children,
      );

  // DeepCollectionEquality handles nested List/Map values inside `params`
  // (e.g. the loop container's `filters: ['L', 'R', 'G', 'B']` list).
  // mapEquals / listEquals are shallow so two structurally-identical nodes
  // would otherwise compare unequal when params contain collections.
  static const _deepEq = DeepCollectionEquality();

  @override
  bool operator ==(Object other) =>
      other is SequenceNode &&
      other.id == id &&
      other.kind == kind &&
      other.displayName == displayName &&
      other.instructionType == instructionType &&
      _deepEq.equals(other.params, params) &&
      _deepEq.equals(other.children, children);

  @override
  int get hashCode => Object.hash(
        id,
        kind,
        displayName,
        instructionType,
        _deepEq.hash(params),
        _deepEq.hash(children),
      );
}
