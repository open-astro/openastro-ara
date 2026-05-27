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

  /// Replace the root with a freshly loaded sequence.
  void load(SequenceNode root) => state = root;

  /// Returns a placeholder root that contains one Area + two Targets so the
  /// tree view shows something on first launch.
  static SequenceNode _demoSequence() => const SequenceNode(
        id: 'root',
        kind: SequenceNodeKind.root,
        displayName: 'Untitled sequence',
        children: <SequenceNode>[
          SequenceNode(
            id: 'area-1',
            kind: SequenceNodeKind.area,
            displayName: 'Tonight (Backyard)',
            children: <SequenceNode>[
              SequenceNode(
                id: 'target-1',
                kind: SequenceNodeKind.target,
                displayName: 'M42 — Orion Nebula',
                children: <SequenceNode>[
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
                  SequenceNode(
                    id: 'instr-1-3',
                    kind: SequenceNodeKind.instruction,
                    instructionType: 'TakeManyExposures',
                    displayName: 'Capture L × 30 (60s)',
                    params: <String, Object?>{
                      'count': 30,
                      'exposureSeconds': 60,
                      'filter': 'L',
                    },
                  ),
                ],
              ),
              SequenceNode(
                id: 'target-2',
                kind: SequenceNodeKind.target,
                displayName: 'NGC 7000 — North America Nebula',
                children: <SequenceNode>[
                  SequenceNode(
                    id: 'instr-2-1',
                    kind: SequenceNodeKind.instruction,
                    instructionType: 'WaitForAltitude',
                    displayName: 'Wait until altitude > 30°',
                    params: <String, Object?>{'altitudeDeg': 30},
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
