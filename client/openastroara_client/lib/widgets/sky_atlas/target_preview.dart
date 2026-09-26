import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/target_preview_service.dart';
import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/target_preview_state.dart';
import '../../theme/ara_colors.dart';

/// A square DSS2 thumbnail of [object] — what it actually looks like — with
/// tap-to-enlarge. Serves the disk cache first; on a dark site with no
/// internet an uncached target shows a quiet "no preview cached" tile rather
/// than an error. Never a gate on anything.
class TargetPreview extends ConsumerWidget {
  final TonightSkyObject object;
  final double size;

  /// The camera's single-frame FOV (width, height arcmin) to draw as a box
  /// over the field, rotated by [rotationDeg] (clockwise, the planetarium
  /// dial's convention). Null = no box.
  final (double, double)? frameFovArcmin;
  final double rotationDeg;
  const TargetPreview({
    super.key,
    required this.object,
    this.size = 72,
    this.frameFovArcmin,
    this.rotationDeg = 0,
  });

  TargetPreviewKey get _key => (
        id: object.id,
        raDeg: object.raDeg,
        decDeg: object.decDeg,
        sizeMajArcmin: object.sizeMajArcmin,
      );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final preview = ref.watch(targetPreviewProvider(_key));
    final fov = TargetPreviewService.fovDegFor(object.sizeMajArcmin);
    final label = '${object.name} preview, ${fov.toStringAsFixed(1)}° field';
    final Widget tile = preview.when(
      loading: () => const Center(
        child: SizedBox(
          width: 16,
          height: 16,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
      ),
      error: (_, _) => const _NoPreview(),
      data: (bytes) => bytes == null
          ? const _NoPreview()
          : Image.memory(
              bytes,
              fit: BoxFit.cover,
              gaplessPlayback: true,
              semanticLabel: label,
            ),
    );
    final bytes = preview.hasValue ? preview.value : null;
    return Semantics(
      label: label,
      button: bytes != null,
      child: InkWell(
        borderRadius: BorderRadius.circular(6),
        onTap: bytes != null ? () => _enlarge(context, bytes) : null,
        child: ClipRRect(
          borderRadius: BorderRadius.circular(6),
          child: Container(
            width: size,
            height: size,
            color: Colors.black,
            child: frameFovArcmin == null
                ? tile
                : Stack(
                    fit: StackFit.expand,
                    children: [
                      tile,
                      IgnorePointer(
                        child: CustomPaint(
                          painter: _FramePainter(
                            fovArcmin: frameFovArcmin!,
                            fieldDeg: fov,
                            rotationDeg: rotationDeg,
                          ),
                        ),
                      ),
                    ],
                  ),
          ),
        ),
      ),
    );
  }

  void _enlarge(BuildContext context, Uint8List bytes) {
    final fov = TargetPreviewService.fovDegFor(object.sizeMajArcmin);
    showDialog<void>(
      context: context,
      builder: (ctx) => Dialog(
        backgroundColor: AraColors.bgPanel,
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 480, maxHeight: 480),
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(6),
                  child: Image.memory(bytes, fit: BoxFit.contain),
                ),
              ),
              const SizedBox(height: 8),
              Text(object.name, style: Theme.of(ctx).textTheme.bodyMedium),
              Text(
                '${fov.toStringAsFixed(1)}° field · DSS2 colour survey '
                '(CDS hips2fits)',
                style: Theme.of(ctx)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _NoPreview extends StatelessWidget {
  const _NoPreview();

  @override
  Widget build(BuildContext context) => Tooltip(
        message:
            'No preview cached — connect to the internet once to fetch it',
        child: Center(
          child: Icon(Icons.image_not_supported_outlined,
              size: 20, color: AraColors.textDisabled),
        ),
      );
}

/// The camera frame as a rotated rectangle over a square cutout of
/// [fieldDeg] degrees across. Scale is honest: a frame wider than the field
/// simply runs off the tile.
class _FramePainter extends CustomPainter {
  final (double, double) fovArcmin;
  final double fieldDeg;
  final double rotationDeg;
  const _FramePainter({
    required this.fovArcmin,
    required this.fieldDeg,
    required this.rotationDeg,
  });

  @override
  void paint(Canvas canvas, Size size) {
    if (fieldDeg <= 0) return;
    final pxPerDeg = size.width / fieldDeg;
    final w = fovArcmin.$1 / 60 * pxPerDeg;
    final h = fovArcmin.$2 / 60 * pxPerDeg;
    final c = size.center(Offset.zero);
    canvas.save();
    canvas.translate(c.dx, c.dy);
    canvas.rotate(rotationDeg * math.pi / 180);
    final rect = Rect.fromCenter(center: Offset.zero, width: w, height: h);
    canvas.drawRect(
      rect,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2.5
        ..color = Colors.black.withValues(alpha: 0.6),
    );
    canvas.drawRect(
      rect,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = AraColors.accentInfo,
    );
    // A tick on the frame's top edge so "which way is up" survives rotation.
    canvas.drawLine(
      Offset(0, -h / 2),
      Offset(0, -h / 2 - 5),
      Paint()
        ..strokeWidth = 1.5
        ..color = AraColors.accentInfo,
    );
    canvas.restore();
  }

  @override
  bool shouldRepaint(_FramePainter old) =>
      old.fovArcmin != fovArcmin ||
      old.fieldDeg != fieldDeg ||
      old.rotationDeg != rotationDeg;
}
