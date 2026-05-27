import 'package:flutter/material.dart';

import '../../models/library/frame.dart';
import '../../theme/ara_colors.dart';

/// Per-frame thumbnail in the session frame strip per §40.4. Phase 12f.1
/// renders a placeholder square + filter label + rating; 12f.2 swaps the
/// placeholder for `Image.network` against `/api/v1/frames/{id}/preview`.
class FrameThumbnail extends StatelessWidget {
  final CapturedFrame frame;
  final VoidCallback? onTap;
  const FrameThumbnail({super.key, required this.frame, this.onTap});

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: 'Frame ${frame.filter} HFR ${frame.hfr.toStringAsFixed(2)}',
      child: InkWell(
        onTap: onTap,
        child: Container(
          width: 72,
          height: 72,
          margin: const EdgeInsets.symmetric(horizontal: 2),
          decoration: BoxDecoration(
            color: AraColors.bgInput,
            border: Border.all(color: AraColors.border),
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
              if (frame.rating > 0)
                Positioned(
                  right: 4,
                  bottom: 4,
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (var i = 0; i < frame.rating; i++)
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
