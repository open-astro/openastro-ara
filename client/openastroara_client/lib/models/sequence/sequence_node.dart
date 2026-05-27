// In-memory model for a sequence tree (§38.1 schema). The wire format is
// JSON matching `openastroara-sequence-v1`; this Dart class mirrors the
// canonical tree shape (Areas → Targets → Instructions) for the Sequencer
// tab's editor (§25.5.3).
//
// Phase 12d.1 ships only the data class + minimal tree manipulation. Phase
// 12d.2 wires JSON (de)serialization against the OpenAPI schema and the
// `/api/v1/sequences` endpoints. Phase 12d.3 adds NINA import handling.

import 'package:flutter/foundation.dart';

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

/// A single node in the sequence tree. Generic enough to cover all NINA
/// container + instruction types; per-instruction parameters live in
/// [params] (Dart Map → JSON object on the wire).
class SequenceNode {
  final String id;
  final SequenceNodeKind kind;
  final String displayName;
  final String? instructionType;
  final Map<String, Object?> params;
  final List<SequenceNode> children;

  const SequenceNode({
    required this.id,
    required this.kind,
    required this.displayName,
    this.instructionType,
    this.params = const <String, Object?>{},
    this.children = const <SequenceNode>[],
  });

  bool get isContainer => kind != SequenceNodeKind.instruction;

  SequenceNode copyWith({
    String? displayName,
    String? instructionType,
    Map<String, Object?>? params,
    List<SequenceNode>? children,
  }) =>
      SequenceNode(
        id: id,
        kind: kind,
        displayName: displayName ?? this.displayName,
        instructionType: instructionType ?? this.instructionType,
        params: params ?? this.params,
        children: children ?? this.children,
      );

  @override
  bool operator ==(Object other) =>
      other is SequenceNode &&
      other.id == id &&
      other.kind == kind &&
      other.displayName == displayName &&
      other.instructionType == instructionType &&
      mapEquals(other.params, params) &&
      listEquals(other.children, children);

  @override
  int get hashCode => Object.hashAll([
        id,
        kind,
        displayName,
        instructionType,
        Object.hashAllUnordered(params.entries),
        Object.hashAll(children),
      ]);
}
