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

import '../../models/sequence/condition_catalog.dart';
import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/trigger_catalog.dart';
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

  // §Run-redesign S12 — undo/redo. Bodies are immutable maps replaced
  // wholesale by every mutator, so a snapshot is an O(1) reference push.
  // Selection is NOT snapshotted: restoring a stale NodePath post-undo is a
  // correctness trap, so undo/redo clear it (matching removeNode's behavior).
  static const int undoCap = 50;
  final List<Map<String, dynamic>> _undoStack = [];
  final List<Map<String, dynamic>> _redoStack = [];
  // Consecutive edits to the SAME scalar field coalesce into one undo step
  // (per-keystroke snapshots would make ⌘Z a character-eraser).
  String? _lastCoalesceKey;

  bool get canUndo => _undoStack.isNotEmpty;
  bool get canRedo => _redoStack.isNotEmpty;

  /// Route for every body mutation: snapshot the pre-edit body, clear redo,
  /// then apply. [coalesceKey] marks a scalar-field edit; repeats on the same
  /// key reuse the existing snapshot.
  void _commitBody(SequenceEditorState prev, SequenceEditorState next,
      {String? coalesceKey}) {
    final coalesce = coalesceKey != null && coalesceKey == _lastCoalesceKey;
    if (!coalesce) {
      _undoStack.add(prev.body);
      if (_undoStack.length > undoCap) _undoStack.removeAt(0);
    }
    _lastCoalesceKey = coalesceKey;
    _redoStack.clear();
    state = next;
  }

  void undo() {
    final s = state;
    if (s == null || _undoStack.isEmpty) return;
    _redoStack.add(s.body);
    _lastCoalesceKey = null;
    state = s._copyWith(body: _undoStack.removeLast(), clearSelection: true);
  }

  void redo() {
    final s = state;
    if (s == null || _redoStack.isEmpty) return;
    _undoStack.add(s.body);
    _lastCoalesceKey = null;
    state = s._copyWith(body: _redoStack.removeLast(), clearSelection: true);
  }

  void _resetHistory() {
    _undoStack.clear();
    _redoStack.clear();
    _lastCoalesceKey = null;
  }

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
    _resetHistory();
    state = SequenceEditorState(
      id: detail.id,
      body: detail.body,
      originalBody: detail.body,
    );
  }

  /// Clear the editor (e.g. on disconnect or sequence deselect).
  void clear() {
    _resetHistory();
    state = null;
  }

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

  /// Move the selection one row up/down in depth-first (tree display) order —
  /// the arrow-key navigation (run-redesign S12). With nothing selected, Down
  /// selects the first row (the root) and Up the last; at either end the
  /// selection stays put. Pure navigation: never mutates the body.
  void selectAdjacent({required bool next}) {
    final s = state;
    if (s == null) return;
    final paths = <NodePath>[];
    // Same flattening order as the tree view; the parser's depth cap bounds a
    // pathological body the same way the tree's renderer does.
    void walk(Map<String, dynamic> node, NodePath path, int depth) {
      paths.add(path);
      if (depth >= 64) return;
      final kids = childrenOf(node);
      for (var i = 0; i < kids.length; i++) {
        walk(kids[i], <int>[...path, i], depth + 1);
      }
    }

    walk(s.body, const [], 0);
    if (paths.isEmpty) return;
    final sel = s.selectedPath;
    final current = sel == null
        ? -1
        : paths.indexWhere((p) => const ListEquality<int>().equals(p, sel));
    final target = current < 0
        ? (next ? 0 : paths.length - 1)
        : (next ? current + 1 : current - 1).clamp(0, paths.length - 1);
    state = s._copyWith(selectedPath: paths[target]);
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
    _commitBody(
        s,
        s._copyWith(
          body: newBody,
          selectedPath: <int>[...parentPath, landed],
        ));
  }

  /// Add [def] relative to the current selection — the palette's tap-to-add
  /// target resolution, so the user doesn't have to think about paths:
  /// - nothing selected → appended to the root container;
  /// - a container selected → appended inside it;
  /// - a leaf instruction selected → inserted right after it in its parent.
  /// The new node is selected (via [insertInstruction]). No-op if no sequence
  /// is loaded.
  void addInstruction(InstructionDef def) {
    final s = state;
    if (s == null) return;
    final sel = s.selectedPath;
    if (sel == null) {
      insertInstruction(const [], childrenOf(s.body).length, def);
      return;
    }
    final node = nodeAt(s.body, sel);
    if (node != null && isContainer(node)) {
      insertInstruction(sel, childrenOf(node).length, def);
    } else {
      // Selected leaf → insert right after it in its parent. (A structurally
      // stale selection can't normally occur — structural edits clear it — but
      // if it did, insertInstruction's parent/clamp guards keep this safe: it
      // either lands in the still-valid parent or no-ops.)
      insertInstruction(sel.sublist(0, sel.length - 1), sel.last + 1, def);
    }
  }

  /// Remove the node at [path] and clear selection (the selected node may have
  /// been removed or had its path shifted). No-op for the root (`[]`) or an
  /// unresolvable path.
  void removeNode(NodePath path) {
    final s = state;
    if (s == null || path.isEmpty) return;
    if (nodeAt(s.body, path) == null) return;
    _commitBody(s, s._copyWith(body: removeAt(s.body, path), clearSelection: true));
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
    _commitBody(
        s,
        s._copyWith(
          body: reorderChild(s.body, parentPath, oldIndex, newIndex),
          clearSelection: true,
        ));
  }

  /// Move the node at [path] one slot up or down among its siblings, keeping it
  /// selected (selection follows the node to its new position). No-op at a
  /// boundary (already first when [up], already last when not), for the root, or
  /// an unresolvable path. The reorder goes through `nina_dom.reorderChild`,
  /// whose Flutter `onReorder` (pre-removal) convention is handled here.
  void moveNode(NodePath path, {required bool up}) {
    final s = state;
    if (s == null || path.isEmpty) return;
    final parentPath = path.sublist(0, path.length - 1);
    final parent = nodeAt(s.body, parentPath);
    if (parent == null) return;
    final i = path.last;
    final count = childrenOf(parent).length;
    if (up ? i <= 0 : i >= count - 1) return; // already at the boundary
    final newIndex = up ? i - 1 : i + 1; // destination sibling index
    // reorderChild takes a pre-removal newIndex: moving down, the slot is one
    // past the destination; moving up it's the destination itself.
    final preRemoval = up ? newIndex : newIndex + 1;
    _commitBody(
        s,
        s._copyWith(
          body: reorderChild(s.body, parentPath, i, preRemoval),
          selectedPath: <int>[...parentPath, newIndex],
        ));
  }

  /// Move the subtree at [fromPath] into the container at [toParentPath] at
  /// [toIndex] (a post-removal index, clamped) — the drag-and-drop reparent.
  /// Clears selection (paths shift wholesale). No-op if no sequence is loaded,
  /// [fromPath] is the root or unresolvable, [toParentPath] isn't a container, or
  /// the destination is the node itself or one of its descendants (which would
  /// orphan it). Goes through `nina_dom.moveSubtree`.
  void moveNodeTo(NodePath fromPath, NodePath toParentPath, int toIndex) {
    final s = state;
    if (s == null || fromPath.isEmpty) return;
    if (nodeAt(s.body, fromPath) == null) return;
    final dest = nodeAt(s.body, toParentPath);
    if (dest == null || !isContainer(dest)) return;
    // A leaf can't take children, and a node can't move inside itself.
    if (isAncestorOrSelf(fromPath, toParentPath)) return;
    _commitBody(
        s,
        s._copyWith(
          body: moveSubtree(s.body, fromPath, toParentPath, toIndex),
          clearSelection: true,
        ));
  }

  /// Set scalar field [key] to [value] on the node at [path], keeping selection
  /// (structure is unchanged). No-op for an unresolvable path.
  void setNodeField(NodePath path, String key, Object? value) {
    final s = state;
    if (s == null || nodeAt(s.body, path) == null) return;
    _commitBody(s, s._copyWith(body: setField(s.body, path, key, value)),
        coalesceKey: 'f:${path.join('.')}/$key');
  }

  /// Append a fresh condition built from [def] to the container at
  /// [containerPath]; keeps selection (the container stays selected — a
  /// condition isn't a tree node and Item paths don't shift). No-op if no
  /// sequence is loaded, or [containerPath] doesn't resolve to a container (a
  /// leaf can't carry conditions — adding would graft a spurious `Conditions`
  /// wrapper onto it).
  void addConditionTo(NodePath containerPath, ConditionDef def) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null || !isContainer(container)) return;
    _commitBody(
        s, s._copyWith(body: addCondition(s.body, containerPath, def.build())));
  }

  /// Remove the condition at [index] from the container at [containerPath];
  /// keeps selection. No-op for an unresolvable container or an out-of-range
  /// [index].
  void removeConditionFrom(NodePath containerPath, int index) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= conditionsOf(container).length) return;
    _commitBody(
        s, s._copyWith(body: removeConditionAt(s.body, containerPath, index)));
  }

  /// Set scalar field [key] to [value] on the condition at [index] of the
  /// container at [containerPath]; keeps selection. No-op for an unresolvable
  /// container or an out-of-range [index].
  void setConditionFieldOn(
      NodePath containerPath, int index, String key, Object? value) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= conditionsOf(container).length) return;
    _commitBody(
        s,
        s._copyWith(
            body: setConditionField(s.body, containerPath, index, key, value)),
        coalesceKey: 'c:${containerPath.join('.')}/$index/$key');
  }

  /// Append a fresh trigger built from [def] to the container at [containerPath];
  /// keeps selection. No-op if no sequence is loaded, or [containerPath] doesn't
  /// resolve to a container (a leaf can't carry triggers — adding would graft a
  /// spurious `Triggers` wrapper onto it). Mirrors [addConditionTo].
  void addTriggerTo(NodePath containerPath, TriggerDef def) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null || !isContainer(container)) return;
    _commitBody(
        s, s._copyWith(body: addTrigger(s.body, containerPath, def.build())));
  }

  /// Remove the trigger at [index] from the container at [containerPath]; keeps
  /// selection. No-op for an unresolvable container or an out-of-range [index].
  void removeTriggerFrom(NodePath containerPath, int index) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= triggersOf(container).length) return;
    _commitBody(
        s, s._copyWith(body: removeTriggerAt(s.body, containerPath, index)));
  }

  /// Set scalar field [key] to [value] on the trigger at [index] of the container
  /// at [containerPath]; keeps selection. No-op for an unresolvable container or
  /// an out-of-range [index].
  void setTriggerFieldOn(
      NodePath containerPath, int index, String key, Object? value) {
    final s = state;
    if (s == null) return;
    final container = nodeAt(s.body, containerPath);
    if (container == null) return;
    if (index < 0 || index >= triggersOf(container).length) return;
    _commitBody(
        s,
        s._copyWith(
            body: setTriggerField(s.body, containerPath, index, key, value)),
        coalesceKey: 't:${containerPath.join('.')}/$index/$key');
  }

  /// Rebaseline dirty-tracking to [savedBody] — the exact body that was just
  /// persisted (call after a successful Save). Passing the *sent* snapshot
  /// (not re-reading live state) is deliberate: if an edit landed while the
  /// PATCH was in flight, the working body now differs from [savedBody], so
  /// [SequenceEditorState.isDirty] correctly stays true and that edit isn't
  /// silently marked clean. No-op if nothing is loaded.
  void markSaved(Map<String, dynamic> savedBody) {
    final s = state;
    if (s == null) return;
    state = s._copyWith(originalBody: savedBody);
  }
}

/// The editor working-state for the open sequence (null when none is loaded).
final sequenceEditorProvider =
    NotifierProvider<SequenceEditorController, SequenceEditorState?>(
        SequenceEditorController.new);
