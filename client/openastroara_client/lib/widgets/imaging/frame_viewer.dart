import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/last_frame_state.dart';
import '../../state/imaging/live_view_frame_state.dart';
import '../../theme/ara_colors.dart';

/// Center pane of the Imaging tab. While §64 Live View is running it shows the
/// live frame stream; otherwise it shows the most-recent captured frame's
/// stretched preview JPEG per §25.5.1 (fetched from `/api/v1/frames/{id}/preview`),
/// with pinch/scroll zoom + pan in both cases.
class FrameViewer extends ConsumerWidget {
  const FrameViewer({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final live = ref.watch(liveViewFrameProvider);
    if (live.active) {
      return _LiveView(state: live);
    }
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

/// The live frame stream while §64 Live View is running. Shows a "starting"
/// spinner until the first frame, then the latest JPEG (gaplessPlayback so the
/// image doesn't flicker between frames), with a small LIVE badge and any error.
class _LiveView extends StatelessWidget {
  final LiveFrameState state;
  const _LiveView({required this.state});

  @override
  Widget build(BuildContext context) {
    final jpeg = state.jpeg;
    return Container(
      color: AraColors.bgPanelAlt,
      child: Stack(
        children: [
          Positioned.fill(
            child: jpeg == null
                ? Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        const CircularProgressIndicator(),
                        const SizedBox(height: 12),
                        Text('Starting Live View…',
                            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                                color: AraColors.textSecondary)),
                      ],
                    ),
                  )
                : InteractiveViewer(
                    constrained: false,
                    boundaryMargin: const EdgeInsets.all(double.infinity),
                    minScale: 0.1,
                    maxScale: 8,
                    child: Image.memory(jpeg, gaplessPlayback: true),
                  ),
          ),
          Positioned(
            top: 8,
            left: 8,
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
              decoration: BoxDecoration(
                color: AraColors.accentBusy,
                borderRadius: BorderRadius.circular(4),
              ),
              child: const Text('LIVE',
                  style: TextStyle(
                      // Pin to black for legibility on the amber accentBusy badge,
                      // independent of the theme's default foreground.
                      color: Colors.black,
                      fontSize: 11,
                      fontWeight: FontWeight.bold)),
            ),
          ),
          if (state.error != null)
            Positioned(
              bottom: 8,
              left: 8,
              right: 8,
              child: Text('Live View: ${state.error}',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AraColors.accentError)),
            ),
        ],
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
