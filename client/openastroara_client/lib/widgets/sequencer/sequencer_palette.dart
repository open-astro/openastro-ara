/// §38 sequence-editor instruction palette — the catalog of instructions a user
/// adds to the sequence, grouped by category. Tapping a tile adds that
/// instruction relative to the current selection (see
/// [SequenceEditorController.addInstruction]); drag-to-a-specific-position lands
/// in a following slice. Disabled (greyed) until a sequence is loaded.
library;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/instruction_style.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';

class SequencerPalette extends ConsumerWidget {
  const SequencerPalette({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    // Only the loaded/not-loaded transition matters here, so select on that to
    // avoid rebuilding the (static) tile list on every selection/field edit.
    final loaded = ref.watch(sequenceEditorProvider.select((s) => s != null));

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
          for (final def in entry.value)
            _PaletteTile(
              def: def,
              enabled: loaded,
              onAdd: () =>
                  ref.read(sequenceEditorProvider.notifier).addInstruction(def),
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
    // Full-width row tiles (not a Wrap of chips) so a long label ellipsizes
    // instead of overflowing in a narrow side pane. The transparent Material
    // gives the InkWell a splash surface confined to the tile (without one the
    // ripple climbs to the pane's Material and fills the whole column).
    return Material(
      color: Colors.transparent,
      child: InkWell(
        onTap: enabled ? onAdd : null,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Row(
            children: [
              // S7 — category hue on the icon only (labels stay neutral):
              // scannable by kind without turning the palette into lights.
              Icon(def.icon,
                  size: 15,
                  color: enabled
                      ? instructionCategoryColor(def.category)
                      : AraColors.textDisabled),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  def.label,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(color: fg, fontSize: 12),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
