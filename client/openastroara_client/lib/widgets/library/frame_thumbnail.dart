import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Per-frame thumbnail in the session frame strip per §40.4. 12f.2: takes the
/// plain wire fields (works for both live `LibraryFrameItem`s and any legacy
/// caller) and renders the capture-time thumbnail via [imageUrl] when the
/// server provides one, falling back to the placeholder icon. Selection-mode
/// rendering (checkbox overlay + border highlight) per 12f.3a.
class FrameThumbnail extends StatelessWidget {
  final String filter;
  final double? hfr;
  final int rating;
  final String? imageUrl;
  final VoidCallback? onTap;
  final VoidCallback? onLongPress;
  final bool selected;
  final bool selectionMode;
  const FrameThumbnail({
    super.key,
    required this.filter,
    required this.hfr,
    required this.rating,
    this.imageUrl,
    this.onTap,
    this.onLongPress,
    this.selected = false,
    this.selectionMode = false,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: hfr is double
          ? 'Frame $filter HFR ${hfr!.toStringAsFixed(2)}'
          : 'Frame $filter',
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
              if (imageUrl != null)
                Positioned.fill(
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(3),
                    child: Image.network(
                      imageUrl!,
                      fit: BoxFit.cover,
                      // Keep the placeholder icon on 404/network failure —
                      // a thumbnail may not exist for recovered orphans.
                      errorBuilder: (_, _, _) => const SizedBox.shrink(),
                    ),
                  ),
                ),
              Positioned(
                left: 4,
                top: 4,
                child: Container(
                  padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 1),
                  color: AraColors.bgPanel.withValues(alpha: 0.85),
                  child: Text(
                    filter,
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
              if (rating > 0)
                Positioned(
                  right: 4,
                  bottom: 4,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (var i = 0; i < rating.clamp(0, 5); i++)
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
