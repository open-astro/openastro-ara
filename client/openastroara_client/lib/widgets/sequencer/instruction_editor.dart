import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sequencer/sequence_state.dart';
import '../../theme/ara_colors.dart';

/// Right-side pane of the Sequencer tab per §25.5.3 — shows the selected
/// node's parameters in an editable form. Phase 12d.1 ships a read-only
/// summary view; 12d.2 makes parameters editable + adds the "convert to
/// Container" / "Add Trigger" actions.
class InstructionEditor extends ConsumerWidget {
  const InstructionEditor({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final root = ref.watch(sequenceControllerProvider);
    final selectedId = ref.watch(selectedNodeIdProvider);
    final node = selectedId == null ? null : findNode(root, selectedId);

    if (node == null) {
      return Container(
        color: AraColors.bgPanel,
        child: const Center(
          child: Padding(
            padding: EdgeInsets.all(16),
            child: Text(
              'Select a node in the tree to edit its parameters.',
              style: TextStyle(color: AraColors.textSecondary),
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    return Container(
      color: AraColors.bgPanel,
      padding: const EdgeInsets.all(16),
      child: ListView(
        children: [
          Text(node.displayName,
              style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(height: 4),
          Text(
            'Kind: ${node.kind.name}'
            '${node.instructionType != null ? '  ·  ${node.instructionType}' : ''}',
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AraColors.textSecondary,
                ),
          ),
          const Divider(height: 32),
          if (node.params.isEmpty)
            Text(
              'No parameters on this node.',
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            )
          else
            ...node.params.entries.map(
              (e) => Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    SizedBox(
                      width: 140,
                      child: Text(
                        e.key,
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                              color: AraColors.textSecondary,
                            ),
                      ),
                    ),
                    Expanded(
                      child: Text('${e.value}',
                          style: Theme.of(context).textTheme.bodyMedium),
                    ),
                  ],
                ),
              ),
            ),
          if (node.isContainer && node.children.isNotEmpty) ...[
            const Divider(height: 32),
            Text('Children (${node.children.length})',
                style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 8),
            ...node.children.map(
              (c) => Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: Row(children: [
                  const Icon(Icons.chevron_right,
                      size: 14, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  Expanded(
                    child: Text(c.displayName,
                        style: Theme.of(context).textTheme.bodySmall),
                  ),
                ]),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
