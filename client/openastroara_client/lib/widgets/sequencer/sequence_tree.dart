import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_node.dart';
import '../../state/sequencer/sequence_state.dart';
import '../../theme/ara_colors.dart';

/// Tree view of the sequence per §25.5.3. Phase 12d.1 renders a flat
/// indented ListView (always-expanded). 12d.2 adds collapse/expand state +
/// drag-and-drop reorder within a level.
class SequenceTree extends ConsumerWidget {
  const SequenceTree({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final root = ref.watch(sequenceControllerProvider);
    final selectedId = ref.watch(selectedNodeIdProvider);
    final rows = <_TreeRow>[];
    _flatten(root, 0, rows);

    return ListView.builder(
      itemCount: rows.length,
      itemBuilder: (context, i) {
        final row = rows[i];
        final selected = row.node.id == selectedId;
        return InkWell(
          onTap: () => ref
              .read(selectedNodeIdProvider.notifier)
              .select(row.node.id),
          child: Container(
            color: selected ? AraColors.selectionBg.withOpacity(0.25) : null,
            padding: EdgeInsets.only(
              left: 8 + row.depth * 20.0,
              right: 8,
              top: 4,
              bottom: 4,
            ),
            child: Row(
              children: [
                Icon(_iconFor(row.node.kind), size: 16, color: AraColors.textSecondary),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    row.node.displayName,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: selected ? AraColors.textPrimary : AraColors.textPrimary,
                        ),
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
                if (row.node.instructionType != null)
                  Text(
                    row.node.instructionType!,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        ),
                  ),
              ],
            ),
          ),
        );
      },
    );
  }

  void _flatten(SequenceNode node, int depth, List<_TreeRow> out) {
    out.add(_TreeRow(node: node, depth: depth));
    for (final c in node.children) {
      _flatten(c, depth + 1, out);
    }
  }

  IconData _iconFor(SequenceNodeKind k) => switch (k) {
        SequenceNodeKind.root => Icons.account_tree,
        SequenceNodeKind.area => Icons.folder_outlined,
        SequenceNodeKind.target => Icons.gps_fixed,
        SequenceNodeKind.sequentialContainer => Icons.format_list_numbered,
        SequenceNodeKind.parallelContainer => Icons.call_split,
        SequenceNodeKind.conditionalContainer => Icons.alt_route,
        SequenceNodeKind.loopContainer => Icons.loop,
        SequenceNodeKind.instruction => Icons.chevron_right,
      };
}

class _TreeRow {
  final SequenceNode node;
  final int depth;
  const _TreeRow({required this.node, required this.depth});
}
