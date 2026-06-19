/// §38 sequence-editor instruction palette — the catalog of instructions a user
/// adds to the sequence, grouped by category. Tapping a tile adds that
/// instruction relative to the current selection (see
/// [SequenceEditorController.addInstruction]); drag-to-a-specific-position lands
/// in a following slice. Disabled (greyed) until a sequence is loaded.
library;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';

class SequencerPalette extends ConsumerWidget {
  const SequencerPalette({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final loaded = ref.watch(sequenceEditorProvider) != null;

    return ListView(
      padding: const EdgeInsets.symmetric(vertical: 8),
      children: [
        for (final entry in instructionCatalogByCategory.entries) ...[
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
            child: Text(
              entry.key.label.toUpperCase(),
              style: const TextStyle(
                color: AraColors.textSecondary,
                fontSize: 11,
                fontWeight: FontWeight.w600,
                letterSpacing: 0.5,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: Wrap(
              spacing: 6,
              runSpacing: 6,
              children: [
                for (final def in entry.value)
                  _PaletteTile(
                    def: def,
                    enabled: loaded,
                    onAdd: () =>
                        ref.read(sequenceEditorProvider.notifier).addInstruction(def),
                  ),
              ],
            ),
          ),
        ],
      ],
    );
  }
}

class _PaletteTile extends StatelessWidget {
  const _PaletteTile({required this.def, required this.enabled, required this.onAdd});

  final InstructionDef def;
  final bool enabled;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final fg = enabled ? AraColors.textPrimary : AraColors.textDisabled;
    return Material(
      color: AraColors.bgInput,
      borderRadius: BorderRadius.circular(4),
      child: InkWell(
        onTap: enabled ? onAdd : null,
        borderRadius: BorderRadius.circular(4),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(def.icon, size: 15, color: fg),
              const SizedBox(width: 6),
              Text(def.label, style: TextStyle(color: fg, fontSize: 12)),
            ],
          ),
        ),
      ),
    );
  }
}
