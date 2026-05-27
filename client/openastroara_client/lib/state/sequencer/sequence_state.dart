import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_node.dart';

/// Owns the currently-loaded sequence + the selected-node selection state.
///
/// Phase 12d.1 ships an in-memory demo sequence so the tree view isn't
/// empty on first open. 12d.2 wires `loadFromServer(id)` against
/// `GET /api/v1/sequences/{id}` and `saveToServer()` against `PUT`.
class SequenceController extends Notifier<SequenceNode> {
  @override
  SequenceNode build() => _demoSequence();

  /// Replace the root with a freshly loaded sequence. Also clears the
  /// selected-node id since the old selection may not exist in the new tree
  /// (per CR finding on PR #59 — stale selection could point at nothing).
  void load(SequenceNode root) {
    state = root;
    ref.read(selectedNodeIdProvider.notifier).select(null);
  }

  /// Move the selected node up among its siblings. No-op if nothing is
  /// selected, the node is the root, or it's already first.
  void moveSelectedUp() => _moveSelected(-1);

  /// Move the selected node down among its siblings. No-op if nothing is
  /// selected, the node is the root, or it's already last.
  void moveSelectedDown() => _moveSelected(1);

  void _moveSelected(int delta) {
    final id = ref.read(selectedNodeIdProvider);
    if (id == null || id == state.id) return;
    final next = _withParentModified(state, id, (parent, idx) {
      final newIdx = idx + delta;
      if (newIdx < 0 || newIdx >= parent.children.length) return parent;
      final reordered = [...parent.children];
      final moved = reordered.removeAt(idx);
      reordered.insert(newIdx, moved);
      return parent.copyWith(children: reordered);
    });
    if (next != null) state = next;
  }

  /// Append a new node as a child of the parent with id [parentId]. Selects
  /// the new node so its params can be edited immediately. No-op if the
  /// parent doesn't exist or isn't a container — instructions can't have
  /// children; the API enforces the invariant even if the UI gate is
  /// bypassed.
  void addChild(
    String parentId, {
    required SequenceNodeKind kind,
    String? instructionType,
    String? displayName,
  }) {
    if (kind == SequenceNodeKind.root) return;
    final parent = findNode(state, parentId);
    if (parent == null || !parent.isContainer) return;

    final id = _nextId();
    final newNode = SequenceNode(
      id: id,
      kind: kind,
      displayName: displayName ?? _defaultName(kind, instructionType),
      instructionType: instructionType,
    );
    final next = _withNodeReplaced(state, parentId, (parent) {
      return parent.copyWith(children: [...parent.children, newNode]);
    });
    if (next != null) {
      state = next;
      ref.read(selectedNodeIdProvider.notifier).select(id);
    }
  }

  /// Insert a new node immediately after the sibling with id [refId]. Selects
  /// the new node so its params can be edited immediately. Refuses to insert
  /// a root-kind node (the tree can only have one root, set at construction).
  void addSiblingAfter(
    String refId, {
    required SequenceNodeKind kind,
    String? instructionType,
    String? displayName,
  }) {
    if (kind == SequenceNodeKind.root) return;
    final id = _nextId();
    final newNode = SequenceNode(
      id: id,
      kind: kind,
      displayName: displayName ?? _defaultName(kind, instructionType),
      instructionType: instructionType,
    );
    final next = _withParentModified(state, refId, (parent, idx) {
      final reordered = [...parent.children];
      reordered.insert(idx + 1, newNode);
      return parent.copyWith(children: reordered);
    });
    if (next != null) {
      state = next;
      ref.read(selectedNodeIdProvider.notifier).select(id);
    }
  }

  /// Replace the node with id [nodeId] using [op]. Walks the tree and rebuilds
  /// the path with copyWith so the immutable-tree invariants hold. Returns
  /// the new root or null if [nodeId] wasn't found.
  static SequenceNode? _withNodeReplaced(
    SequenceNode current,
    String nodeId,
    SequenceNode Function(SequenceNode node) op,
  ) {
    if (current.id == nodeId) return op(current);
    for (var i = 0; i < current.children.length; i++) {
      final replaced =
          _withNodeReplaced(current.children[i], nodeId, op);
      if (replaced != null) {
        final children = [...current.children];
        children[i] = replaced;
        return current.copyWith(children: children);
      }
    }
    return null;
  }

  static int _idCounter = 0;
  static String _nextId() {
    _idCounter++;
    return 'node-${DateTime.now().millisecondsSinceEpoch}-$_idCounter';
  }

  static String _defaultName(SequenceNodeKind kind, String? instructionType) {
    if (instructionType != null) return instructionType;
    return switch (kind) {
      SequenceNodeKind.root => 'Untitled sequence',
      SequenceNodeKind.area => 'New area',
      SequenceNodeKind.target => 'New target',
      SequenceNodeKind.sequentialContainer => 'Sequential block',
      SequenceNodeKind.parallelContainer => 'Parallel block',
      SequenceNodeKind.conditionalContainer => 'If condition',
      SequenceNodeKind.loopContainer => 'Loop',
      SequenceNodeKind.instruction => 'New instruction',
    };
  }

  /// Delete the selected node. Also clears the selection since the id is now
  /// dangling. No-op if nothing is selected or the node is the root.
  void deleteSelected() {
    final id = ref.read(selectedNodeIdProvider);
    if (id == null || id == state.id) return;
    final next = _withParentModified(state, id, (parent, idx) {
      final reordered = [...parent.children]..removeAt(idx);
      return parent.copyWith(children: reordered);
    });
    if (next != null) {
      state = next;
      ref.read(selectedNodeIdProvider.notifier).select(null);
    }
  }

  /// Recursively find the parent of [id] and apply [op]. Returns the new
  /// root (or `null` if the id wasn't found anywhere). Each ancestor on the
  /// path gets a fresh copyWith so the immutable tree's identity invariants
  /// hold all the way back up to root.
  static SequenceNode? _withParentModified(
    SequenceNode current,
    String id,
    SequenceNode Function(SequenceNode parent, int idx) op,
  ) {
    for (var i = 0; i < current.children.length; i++) {
      final child = current.children[i];
      if (child.id == id) {
        return op(current, i);
      }
      final replaced = _withParentModified(child, id, op);
      if (replaced != null) {
        final children = [...current.children];
        children[i] = replaced;
        return current.copyWith(children: children);
      }
    }
    return null;
  }

  /// Returns a placeholder root that contains one Area + two Targets so the
  /// tree view shows something on first launch. Phase 12d.3 expanded the
  /// demo with a conditional container (skip-if-unsafe) and a loop container
  /// (per-filter sub-cycle) so the new node kinds render in the tree.
  static SequenceNode _demoSequence() => SequenceNode(
        id: 'root',
        kind: SequenceNodeKind.root,
        displayName: 'Untitled sequence',
        children: [
          SequenceNode(
            id: 'area-1',
            kind: SequenceNodeKind.area,
            displayName: 'Tonight (Backyard)',
            children: [
              SequenceNode(
                id: 'target-1',
                kind: SequenceNodeKind.target,
                displayName: 'M42 — Orion Nebula',
                children: [
                  SequenceNode(
                    id: 'instr-1-1',
                    kind: SequenceNodeKind.instruction,
                    instructionType: 'SlewToTarget',
                    displayName: 'Slew to target',
                  ),
                  SequenceNode(
                    id: 'instr-1-2',
                    kind: SequenceNodeKind.instruction,
                    instructionType: 'AutoFocus',
                    displayName: 'Run autofocus',
                  ),
                  // §38.5 loop container — runs its children once per filter
                  // in `filters`. Same pattern NINA uses for filter sub-loops.
                  SequenceNode(
                    id: 'loop-1',
                    kind: SequenceNodeKind.loopContainer,
                    displayName: 'For each filter',
                    instructionType: 'ForEachFilter',
                    params: const <String, Object?>{
                      'filters': <String>['L', 'R', 'G', 'B'],
                      'iterationLabel': r'filter $$ITER$$',
                    },
                    children: [
                      SequenceNode(
                        id: 'instr-loop-1',
                        kind: SequenceNodeKind.instruction,
                        instructionType: 'SwitchFilter',
                        displayName: r'Switch to $$ITER$$',
                      ),
                      SequenceNode(
                        id: 'instr-loop-2',
                        kind: SequenceNodeKind.instruction,
                        instructionType: 'TakeManyExposures',
                        displayName: 'Capture × 20 (60s)',
                        params: const <String, Object?>{
                          'count': 20,
                          'exposureSeconds': 60,
                        },
                      ),
                    ],
                  ),
                ],
              ),
              SequenceNode(
                id: 'target-2',
                kind: SequenceNodeKind.target,
                displayName: 'NGC 7000 — North America Nebula',
                children: [
                  SequenceNode(
                    id: 'instr-2-1',
                    kind: SequenceNodeKind.instruction,
                    instructionType: 'WaitForAltitude',
                    displayName: 'Wait until altitude > 30°',
                    params: const <String, Object?>{'altitudeDeg': 30},
                  ),
                  // §38.6 conditional container — children only execute when
                  // the condition is true. Skip-if-unsafe is the canonical
                  // safety pattern from playbook §35.
                  SequenceNode(
                    id: 'cond-1',
                    kind: SequenceNodeKind.conditionalContainer,
                    displayName: 'If weather is safe',
                    instructionType: 'IfCondition',
                    params: const <String, Object?>{
                      'condition': 'safetyMonitor.isSafe',
                      'elseBranch': 'skipTarget',
                    },
                    children: [
                      SequenceNode(
                        id: 'instr-cond-1',
                        kind: SequenceNodeKind.instruction,
                        instructionType: 'TakeManyExposures',
                        displayName: 'Capture Hα × 30 (300s)',
                        params: const <String, Object?>{
                          'count': 30,
                          'exposureSeconds': 300,
                          'filter': 'Hα',
                        },
                      ),
                      SequenceNode(
                        id: 'instr-cond-2',
                        kind: SequenceNodeKind.instruction,
                        instructionType: 'DitherBetweenFrames',
                        displayName: 'Dither every 1 frame (5px)',
                        params: const <String, Object?>{
                          'everyN': 1,
                          'pixels': 5.0,
                        },
                      ),
                    ],
                  ),
                ],
              ),
            ],
          ),
        ],
      );
}

final sequenceControllerProvider =
    NotifierProvider<SequenceController, SequenceNode>(
        SequenceController.new);

/// Currently-selected node id (or null = nothing selected; toolbar shows
/// the root summary). Riverpod 3.x removed `StateProvider`; use a thin
/// Notifier instead.
class SelectedNodeIdNotifier extends Notifier<String?> {
  @override
  String? build() => null;
  void select(String? id) => state = id;
}

final selectedNodeIdProvider =
    NotifierProvider<SelectedNodeIdNotifier, String?>(
        SelectedNodeIdNotifier.new);

/// Walk the tree looking for a node with the given id. Returns null if not
/// found. O(N) — fine for sequences of any reasonable size.
SequenceNode? findNode(SequenceNode root, String id) {
  if (root.id == id) return root;
  for (final c in root.children) {
    final hit = findNode(c, id);
    if (hit != null) return hit;
  }
  return null;
}
