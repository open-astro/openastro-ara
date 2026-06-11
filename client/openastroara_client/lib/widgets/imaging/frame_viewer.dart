import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/last_frame_state.dart';
import '../../theme/ara_colors.dart';

/// Center pane of the Imaging tab — the most-recent frame's stretched preview
/// JPEG per §25.5.1. Renders the placeholder until a frame is captured, then
/// the live preview (fetched from `/api/v1/frames/{id}/preview`) with
/// pinch/scroll zoom + pan.
class FrameViewer extends ConsumerWidget {
  const FrameViewer({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final id = ref.watch(lastCapturedFrameIdProvider);
    if (id == null) {
      return const _Placeholder();
    }
    final preview = ref.watch(framePreviewProvider(id));
    return Container(
      color: AraColors.bgPanelAlt,
      child: preview.when(
        // constrained: false + an unbounded boundary lays the image out at its
        // native size so zooming walks into real pixels instead of magnifying a
        // downscaled raster; minScale lets a large frame be pinched down to fit.
        data: (bytes) => InteractiveViewer(
          constrained: false,
          boundaryMargin: const EdgeInsets.all(double.infinity),
          minScale: 0.1,
          maxScale: 8,
          child: Image.memory(bytes, gaplessPlayback: true),
        ),
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Text(
            'Preview failed: $e',
            style: Theme.of(context)
                .textTheme
                .bodySmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
        ),
      ),
    );
  }
}

class _Placeholder extends StatelessWidget {
  const _Placeholder();

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
