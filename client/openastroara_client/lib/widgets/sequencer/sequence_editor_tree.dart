/// §38 sequence-editor tree view — renders the RAW NINA body held by
/// [sequenceEditorProvider] (not the lossy `SequenceNode` preview) as a flat,
/// indented, selectable list. Tapping a row selects that node (by [NodePath]),
/// which the field editor + palette react to. Drag-to-reorder and the palette
/// drop target land in following slices; this slice is render + select.
library;

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';

/// One flattened tree row: the node, its address, and its indent depth.
class _Row {
  final NodePath path;
  final Map<String, dynamic> node;
  final int depth;
  const _Row(this.path, this.node, this.depth);
}

/// The label to show for [node]: the catalog instruction label when known,
/// else a container's `Name` (or short type), else the short `$type`.
String nodeLabel(Map<String, dynamic> node) {
  final type = node[r'$type'];
  if (type is String) {
    final def = instructionForType(type);
    if (def != null) return def.label;
  }
  if (isContainer(node)) {
    final name = node['Name'];
    if (name is String && name.isNotEmpty) return name;
  }
  return _shortType(type) ?? 'Unknown';
}

/// The icon for [node]: the catalog icon when known, a folder for an unknown
/// container, else a generic instruction glyph.
IconData nodeIcon(Map<String, dynamic> node) {
  final type = node[r'$type'];
  if (type is String) {
    final def = instructionForType(type);
    if (def != null) return def.icon;
  }
  return isContainer(node) ? Icons.account_tree_outlined : Icons.help_outline;
}

/// `'A.B.C, Asm'` → `'C'`; null/non-string/degenerate (empty or trailing-dot
/// like `'A., Asm'`) → null, so `nodeLabel` falls through to `'Unknown'`.
String? _shortType(Object? type) {
  if (type is! String || type.isEmpty) return null;
  final beforeComma = type.split(',').first.trim();
  final lastDot = beforeComma.lastIndexOf('.');
  final short = lastDot >= 0 ? beforeComma.substring(lastDot + 1) : beforeComma;
  return short.isEmpty ? null : short;
}

void _flatten(Map<String, dynamic> node, NodePath path, int depth, List<_Row> out) {
  out.add(_Row(path, node, depth));
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
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}
