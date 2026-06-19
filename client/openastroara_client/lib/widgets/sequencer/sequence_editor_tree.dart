/// §38 sequence-editor tree view — renders the RAW NINA body held by
/// [sequenceEditorProvider] (not the lossy `SequenceNode` preview) as a flat,
/// indented, selectable list. Tapping a row selects that node (by [NodePath]),
/// which the field editor + palette react to; the selected row gets move-up /
/// move-down / delete actions. Rows are also drag sources (long-press) that drop
/// **onto a container** to move (reparent) into it; within-parent ordering stays
/// on the move-up/down buttons.
library;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/nina_sequence_parser.dart' show ninaParseMaxDepth;
import '../../models/sequence/node_display.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';

/// One flattened tree row: the node, its address, and its indent depth.
class _Row {
  final NodePath path;
  final Map<String, dynamic> node;
  final int depth;
  const _Row(this.path, this.node, this.depth);
}

/// The move-up / move-down / delete buttons for the selected non-root [path].
/// Up/down disable at the sibling boundaries.
List<Widget> _rowActions(WidgetRef ref, Map<String, dynamic> body, NodePath path) {
  final index = path.last;
  final parent = nodeAt(body, path.sublist(0, path.length - 1));
  final siblingCount = parent == null ? 0 : childrenOf(parent).length;
  final notifier = ref.read(sequenceEditorProvider.notifier);
  return [
    _RowIcon(
      icon: Icons.arrow_upward,
      tooltip: 'Move up',
      onPressed: index > 0 ? () => notifier.moveNode(path, up: true) : null,
    ),
    _RowIcon(
      icon: Icons.arrow_downward,
      tooltip: 'Move down',
      onPressed:
          index < siblingCount - 1 ? () => notifier.moveNode(path, up: false) : null,
    ),
    _RowIcon(
      icon: Icons.delete_outline,
      tooltip: 'Delete',
      onPressed: () => notifier.removeNode(path),
    ),
  ];
}

/// Whether the subtree at [dragged] can be dropped INTO the container at
/// [target] (append). True iff [target] resolves to a container, isn't [dragged]
/// itself, isn't inside [dragged] (would orphan the subtree), and the move isn't
/// a pure no-op (dropping a node onto its own parent when it's already the last
/// child). Pure so the drag policy is unit-tested without a gesture harness.
@visibleForTesting
bool canReparentInto(Map<String, dynamic> body, NodePath dragged, NodePath target) {
  if (dragged.isEmpty) return false; // the root can't be dragged (`.last` would throw)
  final targetNode = nodeAt(body, target);
  if (targetNode == null || !isContainer(targetNode)) return false;
  if (listEquals(dragged, target)) return false; // onto itself
  if (isAncestorOrSelf(dragged, target)) return false; // into self/descendant
  // No-op: dragged is already the last child of target → dropping changes nothing.
  final isDirectChild =
      dragged.length == target.length + 1 && isAncestorOrSelf(target, dragged);
  if (isDirectChild && dragged.last == childrenOf(targetNode).length - 1) {
    return false;
  }
  return true;
}

void _flatten(Map<String, dynamic> node, NodePath path, int depth, List<_Row> out) {
  out.add(_Row(path, node, depth));
  // Stop recursing past the same adversarial-depth cap the parser uses, so a
  // malformed/pathologically-nested body can't blow the stack.
  if (depth >= ninaParseMaxDepth) return;
  final kids = childrenOf(node);
  for (var i = 0; i < kids.length; i++) {
    _flatten(kids[i], <int>[...path, i], depth + 1, out);
  }
}

/// Indented, selectable view of the editor's working sequence body.
class SequenceEditorTree extends ConsumerWidget {
  const SequenceEditorTree({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final editor = ref.watch(sequenceEditorProvider);
    if (editor == null) {
      return const Center(
        child: Text('No sequence loaded',
            style: TextStyle(color: AraColors.textSecondary)),
      );
    }

    final rows = <_Row>[];
    _flatten(editor.body, const [], 0, rows);
    final selected = editor.selectedPath;

    return ListView.builder(
      itemCount: rows.length,
      itemBuilder: (context, i) {
        final row = rows[i];
        final isSelected = selected != null && listEquals(row.path, selected);
        final content = Padding(
          padding: EdgeInsets.only(
            left: 8 + row.depth * 20.0,
            right: 8,
            top: 6,
            bottom: 6,
          ),
          child: Row(
            children: [
              Icon(nodeIcon(row.node), size: 16, color: AraColors.textSecondary),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  nodeLabel(row.node),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(color: AraColors.textPrimary, fontSize: 13),
                ),
              ),
              // Reorder + delete affordances on the selected row — never the
              // root, which can't move or be removed (it's the sequence
              // container itself).
              if (isSelected && row.path.isNotEmpty)
                ..._rowActions(ref, editor.body, row.path),
            ],
          ),
        );
        // Every row (root included) is a drop target — dropping a dragged node
        // onto a container moves it inside; only non-root rows are drag sources.
        return DragTarget<NodePath>(
          onWillAcceptWithDetails: (d) =>
              canReparentInto(editor.body, d.data, row.path),
          onAcceptWithDetails: (d) {
            // Re-read current state at drop time rather than the build-time
            // snapshot, in case the body changed between onWillAccept and now.
            // (moveNodeTo re-validates and clamps too — this just keeps the
            // append index honest against the live child count.)
            final current = ref.read(sequenceEditorProvider);
            final target =
                current == null ? null : nodeAt(current.body, row.path);
            if (target == null) return;
            ref
                .read(sequenceEditorProvider.notifier)
                .moveNodeTo(d.data, row.path, childrenOf(target).length);
          },
          builder: (context, candidate, rejected) {
            final hovering = candidate.isNotEmpty;
            // Ink (not Container(color:)) so the InkWell splash paints ABOVE the
            // tint instead of being hidden by it.
            return Ink(
              color: hovering
                  ? AraColors.accentInfo.withValues(alpha: 0.22)
                  : (isSelected
                      ? AraColors.selectionBg.withValues(alpha: 0.25)
                      : null),
              child: InkWell(
                onTap: () =>
                    ref.read(sequenceEditorProvider.notifier).select(row.path),
                child: row.path.isEmpty
                    ? content
                    : LongPressDraggable<NodePath>(
                        data: row.path,
                        dragAnchorStrategy: pointerDragAnchorStrategy,
                        feedback: _DragChip(label: nodeLabel(row.node)),
                        childWhenDragging:
                            Opacity(opacity: 0.4, child: content),
                        child: content,
                      ),
              ),
            );
          },
        );
      },
    );
  }
}

/// A compact icon button for the selected-row actions; greyed when [onPressed]
/// is null (boundary).
class _RowIcon extends StatelessWidget {
  const _RowIcon({required this.icon, required this.tooltip, required this.onPressed});
  final IconData icon;
  final String tooltip;
  final VoidCallback? onPressed;

  @override
  Widget build(BuildContext context) => IconButton(
        icon: Icon(icon, size: 16),
        color: AraColors.textSecondary,
        disabledColor: AraColors.textDisabled,
        visualDensity: VisualDensity.compact,
        padding: EdgeInsets.zero,
        constraints: const BoxConstraints.tightFor(width: 28, height: 28),
        tooltip: tooltip,
        onPressed: onPressed,
      );
}

/// The floating label shown under the pointer while a row is being dragged.
class _DragChip extends StatelessWidget {
  const _DragChip({required this.label});
  final String label;

  @override
  Widget build(BuildContext context) => Material(
        color: Colors.transparent,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: AraColors.bgPanelAlt,
            borderRadius: BorderRadius.circular(6),
            border: Border.all(color: AraColors.selectionBg),
          ),
          child: Text(label,
              style: const TextStyle(color: AraColors.textPrimary, fontSize: 13)),
        ),
      );
}
