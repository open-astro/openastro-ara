/// §38 sequence-editor instruction palette — the catalog of instructions a user
/// adds to the sequence, grouped by category. **Drag a tile into the tree** to
/// place it at a precise position (the tree's row/gap targets accept
/// [InstructionDef] payloads — run-redesign S8's primary gesture); tapping
/// still adds relative to the current selection (see
/// [SequenceEditorController.addInstruction]) as the quick path. Disabled
/// (greyed) until a sequence is loaded.
library;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/instruction_style.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_editor_tree.dart' show SequenceDragChip;

class SequencerPalette extends ConsumerStatefulWidget {
  const SequencerPalette({super.key});

  @override
  ConsumerState<SequencerPalette> createState() => _SequencerPaletteState();
}

class _SequencerPaletteState extends ConsumerState<SequencerPalette> {
  String _filter = '';

  @override
  Widget build(BuildContext context) {
    // Only the loaded/not-loaded transition matters here, so select on that to
    // avoid rebuilding the (static) tile list on every selection/field edit.
    final loaded = ref.watch(sequenceEditorProvider.select((s) => s != null));
    final q = _filter.trim().toLowerCase();
    bool matches(InstructionDef d) =>
        q.isEmpty ||
        d.label.toLowerCase().contains(q) ||
        (instructionDescriptions[d.label]?.toLowerCase().contains(q) ?? false);

    return Column(
      children: [
        // S8 — find an instruction without hunting nine category sections.
        Padding(
          padding: const EdgeInsets.fromLTRB(12, 10, 12, 2),
          child: TextField(
            onChanged: (v) => setState(() => _filter = v),
            style: const TextStyle(fontSize: 12.5),
            decoration: InputDecoration(
              isDense: true,
              prefixIcon:
                  const Icon(Icons.search, size: 16, color: AraColors.textSecondary),
              prefixIconConstraints:
                  const BoxConstraints(minWidth: 30, minHeight: 0),
              hintText: 'Find an instruction…',
              hintStyle: const TextStyle(
                  color: AraColors.textDisabled, fontSize: 12.5),
              contentPadding: const EdgeInsets.symmetric(vertical: 8),
              filled: true,
              fillColor: AraColors.bgInput,
              border: OutlineInputBorder(
                borderRadius: BorderRadius.circular(8),
                borderSide: BorderSide.none,
              ),
            ),
          ),
        ),
        Expanded(
          child: ListView(
            padding: const EdgeInsets.symmetric(vertical: 8),
            children: [
              for (final entry in instructionCatalogByCategory.entries)
                if (entry.value.any(matches)) ...[
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
                    if (matches(def))
                      _PaletteTile(
                        def: def,
                        enabled: loaded,
                        onAdd: () => ref
                            .read(sequenceEditorProvider.notifier)
                            .addInstruction(def),
                      ),
                ],
            ],
          ),
        ),
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
    final tile = Material(
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
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      def.label,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(color: fg, fontSize: 12.5),
                    ),
                    // S8 — the one-line "what this does" turns the palette
                    // from a jargon list into something a new user can read.
                    if (instructionDescriptions[def.label] case final d?)
                      Text(
                        d,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                            color: enabled
                                ? AraColors.textSecondary
                                : AraColors.textDisabled,
                            fontSize: 10.5),
                      ),
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
    );
    if (!enabled) return tile;
    // S8 — drag-from-palette-to-position: the tile is a plain Draggable (not
    // long-press — on desktop press-and-move should immediately drag) whose
    // payload the tree's row/gap DragTargets resolve to an insertInstruction.
    // Tap-to-add still works: InkWell handles the tap, the drag only starts
    // once the pointer moves.
    return Draggable<InstructionDef>(
      data: def,
      dragAnchorStrategy: pointerDragAnchorStrategy,
      feedback: SequenceDragChip(label: def.label),
      childWhenDragging: Opacity(opacity: 0.4, child: tile),
      child: tile,
    );
  }
}
