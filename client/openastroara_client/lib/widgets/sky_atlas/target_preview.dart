import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/target_preview_service.dart';
import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/target_preview_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/mosaic_geometry.dart';

/// A landscape DSS2 cutout of [object] — what it actually looks like —
/// filling whatever width it is given (sensor-ish 1.6:1 aspect), with the
/// camera's single frame drawn over it when [frameFovArcmin] is known, turned
/// by [rotationDeg]. Tap to enlarge. Serves the disk cache first; on a dark
/// site with no internet an uncached target shows a quiet "no preview
/// cached" tile rather than an error. Never a gate on anything.
class TargetPreview extends ConsumerWidget {
  final TonightSkyObject object;

  /// The camera's single-frame FOV (width, height arcmin) to draw as a box
  /// over the field, rotated by [rotationDeg] (clockwise, the planetarium
  /// dial's convention). Null = no box, and the field is sized to the object
  /// alone.
  final (double, double)? frameFovArcmin;
  final double rotationDeg;

  /// Mosaic grid to draw instead of a single frame (1×1 = one frame).
  final MosaicGrid mosaic;

  /// Where the frame is aimed: tangent-plane offset from the object in
  /// arcmin (+east, +north). Drag the preview to change it via [onAim];
  /// without [onAim] the frame stays on the object.
  final (double, double) aimOffsetArcmin;
  final ValueChanged<(double, double)>? onAim;
  const TargetPreview({
    super.key,
    required this.object,
    this.frameFovArcmin,
    this.rotationDeg = 0,
    this.mosaic = singleFrame,
    this.aimOffsetArcmin = (0.0, 0.0),
    this.onAim,
  });

  /// The footprint the field must hold: the whole grid, not one panel.
  (double, double)? get _footprintArcmin => frameFovArcmin == null
      ? null
      : mosaicExtentArcmin(frameFovArcmin!, mosaic);

  double get _fieldDeg =>
      TargetPreviewService.fieldDegFor(object.sizeMajArcmin, _footprintArcmin);

  TargetPreviewKey get _key => (
        id: object.id,
        raDeg: object.raDeg,
        decDeg: object.decDeg,
        fieldDeg: _fieldDeg,
      );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final preview = ref.watch(targetPreviewProvider(_key));
    final fieldDeg = _fieldDeg;
    final label = '${object.name} preview, ${fieldDeg.toStringAsFixed(1)}° field';
    final Widget tile = preview.when(
      loading: () => const Center(
        child: SizedBox(
          width: 18,
          height: 18,
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
    final canAim = onAim != null && frameFovArcmin != null;
    return Semantics(
      label: label,
      button: bytes != null,
      hint: canAim ? 'Drag to aim the frame' : null,
      child: InkWell(
        borderRadius: BorderRadius.circular(6),
        onTap: bytes != null ? () => _enlarge(context, bytes) : null,
        child: LayoutBuilder(builder: (context, constraints) {
          final pxPerArcmin = constraints.maxWidth / (fieldDeg * 60);
          // Dragging moves the FRAME across the fixed field: screen right is
          // west (east-left survey), screen down is south, so both axes
          // invert into the +east/+north tangent-plane offset. Axis-specific
          // recognisers (not a pan): inside the dialog's scroll view a pan
          // loses the arena to the scroll and the card just scrolls.
          return GestureDetector(
            onVerticalDragUpdate: canAim
                ? (d) => onAim!((
                      aimOffsetArcmin.$1,
                      aimOffsetArcmin.$2 - d.delta.dy / pxPerArcmin,
                    ))
                : null,
            onHorizontalDragUpdate: canAim
                ? (d) => onAim!((
                      aimOffsetArcmin.$1 - d.delta.dx / pxPerArcmin,
                      aimOffsetArcmin.$2,
                    ))
                : null,
            child: ClipRRect(
          borderRadius: BorderRadius.circular(6),
          child: AspectRatio(
            aspectRatio: TargetPreviewService.aspect,
            child: ColoredBox(
              color: Colors.black,
              child: Stack(
                fit: StackFit.expand,
                children: [
                  tile,
                  if (frameFovArcmin != null)
                    IgnorePointer(
                      child: CustomPaint(
                        painter: _FramePainter(
                          fovArcmin: frameFovArcmin!,
                          fieldDeg: fieldDeg,
                          rotationDeg: rotationDeg,
                          mosaic: mosaic,
                          aimOffsetArcmin: aimOffsetArcmin,
                        ),
                      ),
                    ),
                  Positioned(
                    left: 6,
                    bottom: 4,
                    child: Text(
                      '${fieldDeg.toStringAsFixed(1)}° across · DSS2'
                      '${canAim ? ' · drag to aim' : ''}',
                      style: Theme.of(context).textTheme.labelSmall?.copyWith(
                            color: Colors.white70,
                            shadows: const [
                              Shadow(color: Colors.black, blurRadius: 3)
                            ],
                          ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
          );
        }),
      ),
    );
  }

  void _enlarge(BuildContext context, Uint8List bytes) {
    final fieldDeg = _fieldDeg;
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
                constraints: const BoxConstraints(maxWidth: 960),
                child: ClipRRect(
                  borderRadius: BorderRadius.circular(6),
                  child: AspectRatio(
                    aspectRatio: TargetPreviewService.aspect,
                    child: Stack(
                      fit: StackFit.expand,
                      children: [
                        Image.memory(bytes, fit: BoxFit.cover),
                        if (frameFovArcmin != null)
                          IgnorePointer(
                            child: CustomPaint(
                              painter: _FramePainter(
                                fovArcmin: frameFovArcmin!,
                                fieldDeg: fieldDeg,
                                rotationDeg: rotationDeg,
                                mosaic: mosaic,
                                aimOffsetArcmin: aimOffsetArcmin,
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 8),
              Text(object.name, style: Theme.of(ctx).textTheme.bodyMedium),
              Text(
                '${fieldDeg.toStringAsFixed(1)}° across · DSS2 colour survey '
                '(CDS hips2fits)'
                '${frameFovArcmin != null ? ' · frame at ${rotationDeg.round()}°' : ''}'
                '${mosaic.isMosaic ? ' · ${mosaic.cols}×${mosaic.rows} mosaic' : ''}',
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
              size: 24, color: AraColors.textDisabled),
        ),
      );
}

/// The camera frame as a rotated rectangle over a cutout [fieldDeg] degrees
/// across. Scale is honest: the field is sized so the frame fits at any
/// rotation, but a train wider than the 8° cap simply runs off the tile.
class _FramePainter extends CustomPainter {
  final (double, double) fovArcmin;
  final double fieldDeg;
  final double rotationDeg;
  final MosaicGrid mosaic;
  final (double, double) aimOffsetArcmin;
  const _FramePainter({
    required this.fovArcmin,
    required this.fieldDeg,
    required this.rotationDeg,
    this.mosaic = singleFrame,
    this.aimOffsetArcmin = (0.0, 0.0),
  });

  @override
  void paint(Canvas canvas, Size size) {
    if (fieldDeg <= 0) return;
    final pxPerDeg = size.width / fieldDeg;
    final w = fovArcmin.$1 / 60 * pxPerDeg;
    final h = fovArcmin.$2 / 60 * pxPerDeg;
    // The frame's centre: the object (field centre) shifted by the aim —
    // +east is screen-left, +north is screen-up on the survey cutout.
    final c = size.center(Offset(
      -aimOffsetArcmin.$1 / 60 * pxPerDeg,
      -aimOffsetArcmin.$2 / 60 * pxPerDeg,
    ));
    // A small cross on the catalogue centre so an aimed frame shows what it
    // moved away from.
    if (aimOffsetArcmin.$1 != 0 || aimOffsetArcmin.$2 != 0) {
      final o = size.center(Offset.zero);
      final cross = Paint()
        ..strokeWidth = 1.2
        ..color = Colors.white.withValues(alpha: 0.7);
      canvas.drawLine(o.translate(-6, 0), o.translate(6, 0), cross);
      canvas.drawLine(o.translate(0, -6), o.translate(0, 6), cross);
    }
    // Panel centres in the unrotated grid (pixels); the canvas rotation
    // below turns the whole grid, matching the overlay's tangent-plane math.
    final panels = mosaicPanelOffsetsArcmin(fovArcmin, mosaic)
        .map((o) => Offset(o.$1 / 60 * pxPerDeg, o.$2 / 60 * pxPerDeg))
        .toList();
    canvas.save();
    canvas.translate(c.dx, c.dy);
    canvas.rotate(rotationDeg * math.pi / 180);
    final union = Path();
    for (final p in panels) {
      union.addRect(Rect.fromCenter(center: p, width: w, height: h));
    }
    // Dim everything outside the covered sky so the layout reads at a glance:
    // paint the dim into a layer, then CLEAR the panels' union out of it —
    // overlapping panels stay fully clear (an even-odd path would re-dim
    // the overlap bands).
    final everything = Rect.fromCenter(
        center: Offset.zero,
        width: size.longestSide * 4,
        height: size.longestSide * 4);
    canvas.saveLayer(everything, Paint());
    canvas.drawRect(
        everything, Paint()..color = Colors.black.withValues(alpha: 0.45));
    canvas.drawPath(union, Paint()..blendMode = BlendMode.clear);
    canvas.restore();
    final shadow = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 3
      ..color = Colors.black.withValues(alpha: 0.6);
    final edge = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = AraColors.accentInfo;
    for (final p in panels) {
      final rect = Rect.fromCenter(center: p, width: w, height: h);
      canvas.drawRect(rect, shadow);
      canvas.drawRect(rect, edge);
    }
    // A tick on the grid's top edge so "which way is up" survives rotation.
    final top = panels.map((p) => p.dy).reduce(math.min) - h / 2;
    canvas.drawLine(
      Offset(0, top),
      Offset(0, top - 8),
      Paint()
        ..strokeWidth = 2
        ..color = AraColors.accentInfo,
    );
    canvas.restore();
  }

  @override
  bool shouldRepaint(_FramePainter old) =>
      old.fovArcmin != fovArcmin ||
      old.fieldDeg != fieldDeg ||
      old.rotationDeg != rotationDeg ||
      old.mosaic != mosaic ||
      old.aimOffsetArcmin != aimOffsetArcmin;
}
