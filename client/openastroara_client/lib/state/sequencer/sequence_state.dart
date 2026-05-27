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
