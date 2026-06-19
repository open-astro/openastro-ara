/// §38 sequence-editor working state — the bridge between the raw-body mutation
/// engine (`nina_dom.dart`) and the catalog (`instruction_catalog.dart`) on one
/// side, and the palette/tree/field-editor widgets (later slices) on the other.
///
/// Unlike `SequenceController` (which holds the lossy `SequenceNode` *display*
/// tree), this controller holds the RAW NINA body and edits it in place, so
/// Save round-trips faithfully. Every edit goes through `nina_dom`, which
/// returns a new body and never mutates the source — so the held body stays a
/// pure value and dirty-tracking is a deep compare against the loaded original.
library;

import 'package:collection/collection.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/sequence_summary.dart';

const DeepCollectionEquality _bodyEquality = DeepCollectionEquality();

/// Immutable snapshot of the editor: the working [body], the [originalBody] it
/// was loaded with (for dirty-tracking), and the currently-[selectedPath].
class SequenceEditorState {
  /// The id of the sequence being edited (so Save targets the right record).
  final String id;

  /// The working raw NINA body. Replaced wholesale by every edit (the
  /// `nina_dom` ops return a new root); never mutated in place.
  final Map<String, dynamic> body;

  /// The body as loaded (or last saved). [isDirty] compares [body] to this.
  final Map<String, dynamic> originalBody;

  /// The node currently selected in the tree, or null. A [NodePath] of child
  /// indices from the root (`[]` = the root container itself).
  final NodePath? selectedPath;

  /// Whether the working [body] differs from the loaded/saved [originalBody].
  /// Computed ONCE here (not per read) so widget rebuilds that watch the
  /// provider don't each pay an O(n) deep compare: an identity short-circuit
  /// makes the freshly-loaded / just-saved case (where the two alias the same
  /// map) O(1), and only an actually-edited body pays the deep compare — which
  /// still reports a revert-to-original as not dirty.
  final bool isDirty;

  SequenceEditorState({
    required this.id,
    required this.body,
    required this.originalBody,
    this.selectedPath,
  }) : isDirty = !identical(body, originalBody) &&
            !_bodyEquality.equals(body, originalBody);

  SequenceEditorState _copyWith({
    Map<String, dynamic>? body,
    Map<String, dynamic>? originalBody,
    NodePath? selectedPath,
    bool clearSelection = false,
  }) {
    assert(!(clearSelection && selectedPath != null),
        'pass either clearSelection or selectedPath, not both');
    return SequenceEditorState(
      id: id,
      body: body ?? this.body,
      originalBody: originalBody ?? this.originalBody,
      selectedPath: clearSelection ? null : (selectedPath ?? this.selectedPath),
    );
  }
}

/// Holds the [SequenceEditorState] for the sequence open in the editor, or null
/// when nothing is loaded. Widgets call the mutation methods; each one runs a
/// `nina_dom` op and swaps in the new body.
class SequenceEditorController extends Notifier<SequenceEditorState?> {
  @override
  SequenceEditorState? build() => null;

  /// Load [detail] into the editor (discarding any current edits), with nothing
  /// selected. Seeds both [SequenceEditorState.body] and `originalBody` from the
  /// detail's raw body, so a freshly-loaded sequence is not dirty.
  ///
  /// `body` and `originalBody` intentionally alias the same map: `detail.body`
  /// is deeply unmodifiable and a refresh produces a NEW [SequenceDetail] (it
  /// never mutates an existing body), so there's no aliasing hazard — and the
  /// shared instance lets [SequenceEditorState.isDirty] short-circuit on
  /// identity until the first edit replaces `body` with a fresh map.
  void load(SequenceDetail detail) {
    state = SequenceEditorState(
      id: detail.id,
      body: detail.body,
      originalBody: detail.body,
    );
  }

  /// Clear the editor (e.g. on disconnect or sequence deselect).
  void clear() => state = null;

  /// Select the node at [path], or clear selection with null. A non-null [path]
  /// that doesn't resolve to a node is ignored (stale selection guard).
  void select(NodePath? path) {
    final s = state;
    if (s == null) return;
    if (path != null && nodeAt(s.body, path) == null) return;
    state = path == null
        ? s._copyWith(clearSelection: true)
        : s._copyWith(selectedPath: path);
  }

  /// Insert a fresh node built from [def] as a child of the container at
  /// [parentPath] at [index] (clamped), and select the new node. No-op if no
  /// sequence is loaded, or [parentPath] doesn't resolve to a container (a leaf
  /// instruction can't take children — inserting would graft a spurious `Items`
  /// wrapper onto it).
  void insertInstruction(NodePath parentPath, int index, InstructionDef def) {
    final s = state;
    if (s == null) return;
    final parent = nodeAt(s.body, parentPath);
    if (parent == null || !isContainer(parent)) return;
    final landed = index.clamp(0, childrenOf(parent).length);
    final newBody = insertChild(s.body, parentPath, landed, def.build());
    state = s._copyWith(
      body: newBody,
      selectedPath: <int>[...parentPath, landed],
    );
  }

  /// Remove the node at [path] and clear selection (the selected node may have
  /// been removed or had its path shifted). No-op for the root (`[]`) or an
  /// unresolvable path.
  void removeNode(NodePath path) {
    final s = state;
    if (s == null || path.isEmpty) return;
    if (nodeAt(s.body, path) == null) return;
    state = s._copyWith(body: removeAt(s.body, path), clearSelection: true);
  }

  /// Reorder a child of the container at [parentPath]. [oldIndex]/[newIndex]
  /// follow `nina_dom.reorderChild`'s Flutter `onReorder` convention; [oldIndex]
  /// is bounds-checked here (no-op when out of range) while [newIndex] is
  /// clamped downstream by `reorderChild` (so a drop at/after the end appends).
  /// Clears selection (sibling paths shift). No-op if [parentPath] is not a
  /// resolvable node.
  void reorder(NodePath parentPath, int oldIndex, int newIndex) {
    final s = state;
    if (s == null) return;
    final parent = nodeAt(s.body, parentPath);
    if (parent == null) return;
    if (oldIndex < 0 || oldIndex >= childrenOf(parent).length) return;
    state = s._copyWith(
      body: reorderChild(s.body, parentPath, oldIndex, newIndex),
      clearSelection: true,
    );
  }

  /// Set scalar field [key] to [value] on the node at [path], keeping selection
  /// (structure is unchanged). No-op for an unresolvable path.
  void setNodeField(NodePath path, String key, Object? value) {
    final s = state;
    if (s == null || nodeAt(s.body, path) == null) return;
    state = s._copyWith(body: setField(s.body, path, key, value));
  }

  /// Mark the current body as the saved baseline (call after a successful Save),
  /// so [SequenceEditorState.isDirty] reads false until the next edit.
  void markSaved() {
    final s = state;
    if (s == null) return;
    state = s._copyWith(originalBody: s.body);
  }
}

/// The editor working-state for the open sequence (null when none is loaded).
final sequenceEditorProvider =
    NotifierProvider<SequenceEditorController, SequenceEditorState?>(
        SequenceEditorController.new);
