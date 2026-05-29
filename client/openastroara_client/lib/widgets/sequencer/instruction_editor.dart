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

    final isRoot = node.id == root.id;
    final controller = ref.read(sequenceControllerProvider.notifier);
    return Container(
      color: AraColors.bgPanel,
      padding: const EdgeInsets.all(16),
      child: ListView(
        children: [
          Row(children: [
            Expanded(
              child: Text(node.displayName,
                  style: Theme.of(context).textTheme.titleMedium),
            ),
            // Phase 12d.5 — add child / sibling. Add-sibling is disabled
            // when the root is selected (the root has no parent).
            _AddNodeMenu(
              icon: Icons.add,
              tooltip: 'Add child node',
              enabled: node.isContainer,
              onSelected: (spec) => controller.addChild(
                node.id,
                kind: spec.kind,
                instructionType: spec.instructionType,
              ),
            ),
            _AddNodeMenu(
              icon: Icons.subdirectory_arrow_right,
              tooltip: 'Add sibling node after this one',
              enabled: !isRoot,
              onSelected: (spec) => controller.addSiblingAfter(
                node.id,
                kind: spec.kind,
                instructionType: spec.instructionType,
              ),
            ),
            // Phase 12d.4 — reorder + delete. Disabled for the root since
            // moving it sideways is undefined. Drag-and-drop lands in 12d.6.
            IconButton(
              onPressed: isRoot ? null : controller.moveSelectedUp,
              icon: const Icon(Icons.arrow_upward),
              tooltip: 'Move up among siblings',
              iconSize: 18,
            ),
            IconButton(
              onPressed: isRoot ? null : controller.moveSelectedDown,
              icon: const Icon(Icons.arrow_downward),
              tooltip: 'Move down among siblings',
              iconSize: 18,
            ),
            IconButton(
              onPressed: isRoot ? null : controller.deleteSelected,
              icon: const Icon(Icons.delete_outline),
              tooltip: 'Delete this node',
              iconSize: 18,
              color: isRoot ? null : AraColors.accentBusy,
            ),
          ]),
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

/// Spec for one entry in the Add-node menu — (kind, instructionType?) pair.
class _NewNodeSpec {
  final SequenceNodeKind kind;
  final String? instructionType;
  final String label;
  const _NewNodeSpec(this.label, this.kind, [this.instructionType]);
}

const _kNewNodeMenu = <_NewNodeSpec>[
  _NewNodeSpec('Sequential container', SequenceNodeKind.sequentialContainer),
  _NewNodeSpec('Parallel container', SequenceNodeKind.parallelContainer),
  _NewNodeSpec('Conditional (IfCondition)',
      SequenceNodeKind.conditionalContainer, 'IfCondition'),
  _NewNodeSpec(
      'Loop (ForEachFilter)', SequenceNodeKind.loopContainer, 'ForEachFilter'),
  _NewNodeSpec('Target', SequenceNodeKind.target),
  // Common instructions per §38.1 — fast-path entries so users don't have
  // to type the type name each time.
  _NewNodeSpec(
      'Instruction · SlewToTarget', SequenceNodeKind.instruction, 'SlewToTarget'),
  _NewNodeSpec('Instruction · AutoFocus', SequenceNodeKind.instruction, 'AutoFocus'),
  _NewNodeSpec('Instruction · TakeManyExposures', SequenceNodeKind.instruction,
      'TakeManyExposures'),
  _NewNodeSpec('Instruction · SwitchFilter', SequenceNodeKind.instruction,
      'SwitchFilter'),
  _NewNodeSpec('Instruction · WaitForAltitude', SequenceNodeKind.instruction,
      'WaitForAltitude'),
  _NewNodeSpec('Instruction · WaitForTime', SequenceNodeKind.instruction,
      'WaitForTime'),
  _NewNodeSpec(
      'Instruction · DitherBetweenFrames', SequenceNodeKind.instruction, 'DitherBetweenFrames'),
  _NewNodeSpec('Instruction · ParkMount', SequenceNodeKind.instruction, 'ParkMount'),
];

class _AddNodeMenu extends StatelessWidget {
  final IconData icon;
  final String tooltip;
  final bool enabled;
  final void Function(_NewNodeSpec) onSelected;
  const _AddNodeMenu({
    required this.icon,
    required this.tooltip,
    required this.enabled,
    required this.onSelected,
  });

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<_NewNodeSpec>(
      enabled: enabled,
      tooltip: tooltip,
      icon: Icon(icon, size: 18),
      onSelected: onSelected,
      itemBuilder: (_) => [
        for (final spec in _kNewNodeMenu)
          PopupMenuItem<_NewNodeSpec>(value: spec, child: Text(spec.label)),
      ],
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
