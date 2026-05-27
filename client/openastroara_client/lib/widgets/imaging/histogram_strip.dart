import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Compact histogram strip rendered below the FrameViewer. Phase 12c.1
/// renders only an empty bin strip; Phase 12c.2 plots the real per-channel
/// luminance histogram once the frame fetch lands.
class HistogramStrip extends StatelessWidget {
  const HistogramStrip({super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 72,
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: List<Widget>.generate(64, (i) {
          // Placeholder gradient — empty bins from left to right.
          final alpha = (0.05 + 0.25 * (i / 63)).clamp(0.0, 1.0);
          return Expanded(
            child: Container(
              margin: const EdgeInsets.symmetric(horizontal: 0.5),
              height: 8,
              color: AraColors.textDisabled.withValues(alpha: alpha),
            ),
          );
        }),
      ),
    );
  }
}
