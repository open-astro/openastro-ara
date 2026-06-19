/// §38 sequence-editor tree view — renders the RAW NINA body held by
/// [sequenceEditorProvider] (not the lossy `SequenceNode` preview) as a flat,
/// indented, selectable list. Tapping a row selects that node (by [NodePath]),
/// which the field editor + palette react to; the selected row gets move-up /
/// move-down / delete actions. Drag-to-reorder is a following slice.
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
        // Ink (not Container(color:)) so the InkWell splash paints ABOVE the
        // selection tint instead of being hidden by it.
        return Ink(
          color:
              isSelected ? AraColors.selectionBg.withValues(alpha: 0.25) : null,
          child: InkWell(
            onTap: () =>
                ref.read(sequenceEditorProvider.notifier).select(row.path),
            child: Padding(
              padding: EdgeInsets.only(
                left: 8 + row.depth * 20.0,
                right: 8,
                top: 6,
                bottom: 6,
              ),
              child: Row(
                children: [
                  Icon(nodeIcon(row.node),
                      size: 16, color: AraColors.textSecondary),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      nodeLabel(row.node),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                          color: AraColors.textPrimary, fontSize: 13),
                    ),
                  ),
                  // Reorder + delete affordances on the selected row — never the
                  // root, which can't move or be removed (it's the sequence
                  // container itself).
                  if (isSelected && row.path.isNotEmpty)
                    ..._rowActions(ref, editor.body, row.path),
                ],
              ),
            ),
          ),
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
