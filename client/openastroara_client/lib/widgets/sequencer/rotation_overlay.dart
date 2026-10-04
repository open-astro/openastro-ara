import 'dart:math' as math;
import 'dart:typed_data';

import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Screen angle (degrees, 0 = right, clockwise positive — Flutter's y points
/// down) of celestial north in a frame whose up axis sits at
/// [positionAngleDeg] (east of north). Unflipped, east is to the LEFT of the
/// picture: north is the up axis turned clockwise by the position angle. A
/// mirrored train puts east on the right and reverses the sense. Pure —
/// unit-tested.
double northScreenAngleDeg(double positionAngleDeg, {required bool flipped}) =>
    -90 + (flipped ? -positionAngleDeg : positionAngleDeg);

/// How far (degrees, clockwise positive on screen) the planned framing
/// rectangle is turned relative to the picture's own edges while
/// [deltaDeg] of rotation remains. A positive delta turns the sensor's up
/// axis toward east — counterclockwise on an unflipped picture — so the
/// rectangle sits at −delta; a flipped train reverses it. The user turns the
/// camera until the rectangle lines up with the picture. Pure — unit-tested.
double plannedFrameScreenRotationDeg(
  double deltaDeg, {
  required bool flipped,
}) => flipped ? deltaDeg : -deltaDeg;

/// The latest solved frame with the framing drawn over it: a north arrow
/// where north actually is in this picture, and the planned framing rectangle
/// turned by the remaining delta — when it lines up with the picture's edges,
/// the camera is at the planned angle.
class RotationFrameView extends StatelessWidget {
  final Uint8List frame;
  final int frameWidth;
  final int frameHeight;
  final double solvedPositionAngleDeg;
  final double targetPositionAngleDeg;
  final double deltaDeg;
  final bool flipped;
  final bool onTarget;
  final double height;

  const RotationFrameView({
    super.key,
    required this.frame,
    required this.frameWidth,
    required this.frameHeight,
    required this.solvedPositionAngleDeg,
    required this.targetPositionAngleDeg,
    required this.deltaDeg,
    required this.flipped,
    required this.onTarget,
    this.height = 240,
  });

  @override
  Widget build(BuildContext context) {
    final aspect = frameWidth > 0 && frameHeight > 0
        ? frameWidth / frameHeight
        : 3 / 2;
    return SizedBox(
      height: height,
      child: Center(
        child: AspectRatio(
          aspectRatio: aspect,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(8),
            child: Stack(
              fit: StackFit.expand,
              children: [
                Image.memory(frame, fit: BoxFit.fill, gaplessPlayback: true),
                CustomPaint(
                  key: const Key('rotation-overlay'),
                  painter: RotationOverlayPainter(
                    solvedPositionAngleDeg: solvedPositionAngleDeg,
                    targetPositionAngleDeg: targetPositionAngleDeg,
                    deltaDeg: deltaDeg,
                    flipped: flipped,
                    onTarget: onTarget,
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class RotationOverlayPainter extends CustomPainter {
  final double solvedPositionAngleDeg;
  final double targetPositionAngleDeg;
  final double deltaDeg;
  final bool flipped;
  final bool onTarget;

  const RotationOverlayPainter({
    required this.solvedPositionAngleDeg,
    required this.targetPositionAngleDeg,
    required this.deltaDeg,
    required this.flipped,
    required this.onTarget,
  });

  @override
  void paint(Canvas canvas, Size size) {
    final centre = Offset(size.width / 2, size.height / 2);
    final colour = onTarget
        ? AraColors.accentConnected
        : AraColors.accentWarning;

    // Planned framing: the picture's own rectangle, turned by the remaining delta.
    final rotation =
        plannedFrameScreenRotationDeg(deltaDeg, flipped: flipped) *
        math.pi /
        180;
    canvas.save();
    canvas.translate(centre.dx, centre.dy);
    canvas.rotate(rotation);
    final inset = 6.0;
    final rect = Rect.fromCenter(
      center: Offset.zero,
      width: size.width - inset * 2,
      height: size.height - inset * 2,
    );
    canvas.drawRect(
      rect,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2
        ..color = colour.withValues(alpha: 0.95),
    );
    // A tick on the planned top edge, so "which way is up" survives the symmetry of a rectangle.
    canvas.drawLine(
      Offset(0, rect.top),
      Offset(0, rect.top + 14),
      Paint()
        ..strokeWidth = 3
        ..color = colour,
    );
    canvas.restore();

    // North arrow from the centre: where north actually is in this picture.
    final n =
        northScreenAngleDeg(solvedPositionAngleDeg, flipped: flipped) *
        math.pi /
        180;
    final len = math.min(size.width, size.height) * 0.28;
    final tip = centre + Offset(math.cos(n), math.sin(n)) * len;
    final arrow = Paint()
      ..strokeWidth = 2
      ..color = AraColors.accentInfo
      ..style = PaintingStyle.stroke;
    canvas.drawLine(centre, tip, arrow);
    final head = 9.0;
    for (final side in [-1, 1]) {
      final a = n + math.pi + side * 0.45;
      canvas.drawLine(
        tip,
        tip + Offset(math.cos(a), math.sin(a)) * head,
        arrow,
      );
    }
    _label(
      canvas,
      'N',
      tip + Offset(math.cos(n), math.sin(n)) * 12,
      AraColors.accentInfo,
    );
    _label(
      canvas,
      onTarget
          ? 'framing matches'
          : 'planned framing · ${targetPositionAngleDeg.toStringAsFixed(1)}°',
      Offset(10, size.height - 18),
      colour,
      align: false,
    );
  }

  void _label(
    Canvas canvas,
    String text,
    Offset at,
    Color colour, {
    bool align = true,
  }) {
    final painter = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(
          color: colour,
          fontSize: 12,
          fontWeight: FontWeight.w700,
          shadows: const [Shadow(color: Colors.black, blurRadius: 3)],
        ),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    painter.paint(
      canvas,
      align ? at - Offset(painter.width / 2, painter.height / 2) : at,
    );
  }

  @override
  bool shouldRepaint(RotationOverlayPainter old) =>
      old.solvedPositionAngleDeg != solvedPositionAngleDeg ||
      old.targetPositionAngleDeg != targetPositionAngleDeg ||
      old.deltaDeg != deltaDeg ||
      old.flipped != flipped ||
      old.onTarget != onTarget;
}
