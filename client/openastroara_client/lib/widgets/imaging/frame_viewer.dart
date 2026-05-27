import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Center pane of the Imaging tab — the most-recent frame's preview JPEG
/// per §25.5.1. Renders a placeholder for Phase 12c.1; the real
/// `Image.network(previewUrl)` + InteractiveViewer zoom/pan + plate-solve
/// overlay land in Phase 12c.2 once `/api/v1/frames/{id}/preview` is
/// service-side-functional.
class FrameViewer extends StatelessWidget {
  const FrameViewer({super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      color: AraColors.bgPanelAlt,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.image_outlined, size: 96, color: AraColors.textDisabled),
            const SizedBox(height: 12),
            Text(
              'No frame yet',
              style: Theme.of(context).textTheme.titleMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
            const SizedBox(height: 4),
            Text(
              'Take One to capture, or start a sequence in the Sequencer tab',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
            ),
          ],
        ),
      ),
    );
  }
}
