import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/last_frame_state.dart';
import '../../util/friendly_error.dart';
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
        data: (bytes) => _ZoomableFrame(bytes: bytes),
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Text(
            friendlyError(e, action: 'load the preview'),
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

/// The captured-frame viewer: pinch/scroll zoom + pan, opening at fit-to-window,
/// with double-click toggling between fit and 1:1 pixels at the click point —
/// the quick look first, the pixels when you ask for them.
class _ZoomableFrame extends StatefulWidget {
  final Uint8List bytes;
  const _ZoomableFrame({required this.bytes});

  @override
  State<_ZoomableFrame> createState() => _ZoomableFrameState();
}

class _ZoomableFrameState extends State<_ZoomableFrame> {
  final TransformationController _transform = TransformationController();
  Size? _imageSize;
  Size? _fittedFor;
  Offset _doubleTapAt = Offset.zero;

  @override
  void initState() {
    super.initState();
    _resolveSize();
  }

  @override
  void didUpdateWidget(covariant _ZoomableFrame old) {
    super.didUpdateWidget(old);
    if (!identical(old.bytes, widget.bytes)) {
      // A new frame opens at fit, like the first one.
      _imageSize = null;
      _fittedFor = null;
      _resolveSize();
    }
  }

  Future<void> _resolveSize() async {
    final image = await decodeImageFromList(widget.bytes);
    if (!mounted) return;
    setState(() =>
        _imageSize = Size(image.width.toDouble(), image.height.toDouble()));
    image.dispose();
  }

  @override
  void dispose() {
    _transform.dispose();
    super.dispose();
  }

  double _fitScale(Size viewport, Size image) => math.min(
      viewport.width / image.width, viewport.height / image.height);

  Matrix4 _fitMatrix(Size viewport, Size image) {
    final s = _fitScale(viewport, image);
    return Matrix4.identity()
      ..translateByDouble((viewport.width - image.width * s) / 2,
          (viewport.height - image.height * s) / 2, 0, 1)
      ..scaleByDouble(s, s, 1, 1);
  }

  void _onDoubleTap(Size viewport) {
    final image = _imageSize;
    if (image == null) return;
    final fit = _fitScale(viewport, image);
    final current = _transform.value.getMaxScaleOnAxis();
    if ((current - fit).abs() < 0.01) {
      // At fit → 1:1, keeping the double-clicked spot under the cursor.
      final scenePoint = _transform.toScene(_doubleTapAt);
      _transform.value = Matrix4.identity()
        ..translateByDouble(_doubleTapAt.dx - scenePoint.dx,
            _doubleTapAt.dy - scenePoint.dy, 0, 1);
    } else {
      _transform.value = _fitMatrix(viewport, image);
    }
  }

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, constraints) {
      final viewport = Size(constraints.maxWidth, constraints.maxHeight);
      final image = _imageSize;
      if (image != null && _fittedFor != viewport) {
        // First layout with known dimensions: open at fit-to-window rather
        // than a 1:1 corner crop. On a plain window RESIZE, only re-fit when
        // the user was still at fit — a zoomed/panned view they set up
        // deliberately must survive the resize.
        final wasAtFit = _fittedFor == null ||
            (_transform.value.getMaxScaleOnAxis() -
                        _fitScale(_fittedFor!, image))
                    .abs() <
                0.01;
        _fittedFor = viewport;
        if (wasAtFit) {
          _transform.value = _fitMatrix(viewport, image);
        }
      }
      return GestureDetector(
        onDoubleTapDown: (d) => _doubleTapAt = d.localPosition,
        onDoubleTap: () => _onDoubleTap(viewport),
        child: InteractiveViewer(
          transformationController: _transform,
          constrained: false,
          boundaryMargin: const EdgeInsets.all(double.infinity),
          minScale: 0.05,
          maxScale: 8,
          child: Image.memory(widget.bytes, gaplessPlayback: true),
        ),
      );
    });
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
