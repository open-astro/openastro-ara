import 'package:flutter/material.dart';

import '../../models/library/frame.dart';
import '../../theme/ara_colors.dart';

/// Per-frame thumbnail in the session frame strip per §40.4. Phase 12f.1
/// renders a placeholder square + filter label + rating; 12f.2 swaps the
/// placeholder for `Image.network` against `/api/v1/frames/{id}/preview`.
/// Phase 12f.3a adds selection-mode rendering (checkbox overlay + border
/// highlight + long-press to enter selection mode).
class FrameThumbnail extends StatelessWidget {
  final CapturedFrame frame;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;
  final bool selected;
  final bool selectionMode;
  const FrameThumbnail({
    super.key,
    required this.frame,
    this.onTap,
    this.onLongPress,
    this.selected = false,
    this.selectionMode = false,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: 'Frame ${frame.filter} HFR ${frame.hfr.toStringAsFixed(2)}',
      selected: selected,
      child: InkWell(
        onTap: onTap,
        onLongPress: onLongPress,
        child: Container(
          width: 72,
          height: 72,
          margin: const EdgeInsets.symmetric(horizontal: 2),
          decoration: BoxDecoration(
            color: AraColors.bgInput,
            border: Border.all(
              color: selected ? AraColors.selectionBg : AraColors.border,
              width: selected ? 2 : 1,
            ),
            borderRadius: BorderRadius.circular(4),
          ),
          child: Stack(
            children: [
              const Center(
                child: Icon(Icons.image_outlined,
                    color: AraColors.textDisabled, size: 32),
              ),
              Positioned(
                left: 4,
                top: 4,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                  color: AraColors.bgPanel.withValues(alpha: 0.85),
                  child: Text(
                    frame.filter,
                    style: Theme.of(context).textTheme.labelSmall,
                  ),
                ),
              ),
              // Selection-mode checkbox overlay. Visible whenever the
              // library is in selection mode (one or more frames selected);
              // shows a filled checkmark when this frame is part of the
              // selection.
              if (selectionMode)
                Positioned(
                  left: 4,
                  bottom: 4,
                  child: Container(
                    width: 18,
                    height: 18,
                    decoration: BoxDecoration(
                      color: selected
                          ? AraColors.selectionBg
                          : AraColors.bgPanel.withValues(alpha: 0.85),
                      border:
                          Border.all(color: AraColors.selectionBg, width: 1.5),
                      borderRadius: BorderRadius.circular(3),
                    ),
                    child: selected
                        ? const Icon(Icons.check,
                            size: 14, color: Colors.white)
                        : null,
                  ),
                ),
              // Defensive clamp — malformed payload could push rating
              // outside 0..5 and overflow the thumbnail. Real validation
              // lands at the API/model boundary in Phase 12f.2.
              if (frame.rating > 0)
                Positioned(
                  right: 4,
                  bottom: 4,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (var i = 0; i < frame.rating.clamp(0, 5); i++)
                        const Icon(Icons.star, size: 8, color: AraColors.accentBusy),
                    ],
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
