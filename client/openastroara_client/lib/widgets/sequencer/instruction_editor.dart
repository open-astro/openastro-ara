import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/sequence_node.dart';
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
          if (node.kind == SequenceNodeKind.conditionalContainer)
            _ConditionBanner(condition: '${node.params['condition'] ?? '—'}'),
          if (node.kind == SequenceNodeKind.loopContainer)
            _LoopBanner(
              filters: (node.params['filters'] as List?)?.cast<Object?>(),
              iterationLabel: '${node.params['iterationLabel'] ?? '—'}',
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
          if (node.kind == SequenceNodeKind.conditionalContainer) ...[
            const Divider(height: 32),
            Text('Else branch',
                style: Theme.of(context).textTheme.titleSmall),
            const SizedBox(height: 4),
            Text(
              switch (node.params['elseBranch']) {
                'skipTarget' =>
                  'Skip remainder of the parent target if condition is false.',
                'skipArea' =>
                  'Skip remainder of the parent area if condition is false.',
                'abortSequence' =>
                  'Abort the entire sequence if condition is false (§35).',
                _ => 'Continue with the next sibling instruction if condition is false.',
              },
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ],
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

class _ConditionBanner extends StatelessWidget {
  final String condition;
  const _ConditionBanner({required this.condition});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Container(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
        decoration: BoxDecoration(
          color: AraColors.selectionBg.withValues(alpha: 0.15),
          border: Border.all(color: AraColors.selectionBg),
          borderRadius: BorderRadius.circular(4),
        ),
        child: Row(
          children: [
            const Icon(Icons.alt_route,
                size: 16, color: AraColors.selectionBg),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                'Children run when:  $condition',
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _LoopBanner extends StatelessWidget {
  final List<Object?>? filters;
  final String iterationLabel;
  const _LoopBanner({required this.filters, required this.iterationLabel});

  @override
  Widget build(BuildContext context) {
    final f = filters?.map((e) => '$e').join(' → ') ?? '—';
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Container(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 8),
        decoration: BoxDecoration(
          color: AraColors.accentBusy.withValues(alpha: 0.12),
          border: Border.all(color: AraColors.accentBusy),
          borderRadius: BorderRadius.circular(4),
        ),
        child: Row(
          children: [
            const Icon(Icons.loop, size: 16, color: AraColors.accentBusy),
            const SizedBox(width: 8),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'Iterates over:  $f',
                    style: Theme.of(context).textTheme.bodyMedium,
                  ),
                  Text(
                    'Per-iteration label:  $iterationLabel',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        ),
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
