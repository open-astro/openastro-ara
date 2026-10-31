import 'dart:math' as math;

import 'package:flutter/material.dart';

/// Device glyphs Material Icons has no match for, drawn at icon size in the
/// icon colour so they sit beside the stock icons in the top equipment bar.
class EquipmentGlyph extends StatelessWidget {
  final void Function(Canvas canvas, Size size, Paint stroke, Paint fill) _paint;
  final double size;
  final Color color;

  /// A refractor on a tripod, tube pointing up-left: the mount chip.
  const EquipmentGlyph.telescope({super.key, this.size = 28, required this.color}) : _paint = _telescope;

  /// A filter wheel: round housing with the motor box below, five filter holes.
  const EquipmentGlyph.filterWheel({super.key, this.size = 28, required this.color}) : _paint = _filterWheel;

  @override
  Widget build(BuildContext context) => SizedBox.square(
        dimension: size,
        child: CustomPaint(painter: _GlyphPainter(_paint, color)),
      );

  static void _telescope(Canvas c, Size s, Paint stroke, Paint fill) {
    final u = s.width / 24; // drawn on a 24-unit grid like Material icons
    // Mirrored so the tube points up-left, away from the chip's status dot (top right).
    c.save();
    c.translate(s.width, 0);
    c.scale(-1, 1);
    // Tube, tilted up to the right, with a wider objective end and a small eyepiece.
    c.save();
    c.translate(11 * u, 9 * u);
    c.rotate(-math.pi / 7);
    c.drawRRect(RRect.fromLTRBR(-8 * u, -2 * u, 7 * u, 2 * u, Radius.circular(0.8 * u)), fill);
    c.drawRect(Rect.fromLTRB(7 * u, -3 * u, 10 * u, 3 * u), fill);
    c.drawRect(Rect.fromLTRB(-10 * u, -1 * u, -8 * u, 1 * u), fill);
    c.restore();
    // Tripod from the mount head.
    final head = Offset(11 * u, 11 * u);
    c.drawCircle(head, 1.6 * u, fill);
    c.drawLine(head, Offset(5 * u, 22 * u), stroke);
    c.drawLine(head, Offset(11 * u, 22 * u), stroke);
    c.drawLine(head, Offset(17 * u, 22 * u), stroke);
    c.restore();
  }

  static void _filterWheel(Canvas c, Size s, Paint stroke, Paint fill) {
    final u = s.width / 24;
    // A filter-wheel housing seen face on (the ZWO-style 5-slot wheel): a round
    // body with the motor box under it, five filter holes round a hub screw.
    final centre = Offset(12 * u, 11 * u);
    final body = Path()
      ..addOval(Rect.fromCircle(center: centre, radius: 10.5 * u))
      ..addRRect(RRect.fromLTRBR(8 * u, 17 * u, 16 * u, 23.5 * u, Radius.circular(1.2 * u)));
    final holes = Path()..fillType = PathFillType.evenOdd;
    for (var i = 0; i < 5; i++) {
      final a = -math.pi / 2 + i * 2 * math.pi / 5;
      holes.addOval(Rect.fromCircle(
          center: centre + Offset(math.cos(a), math.sin(a)) * (6 * u), radius: 2.9 * u));
    }
    holes.addOval(Rect.fromCircle(center: centre, radius: 0.9 * u));
    c.drawPath(Path.combine(PathOperation.difference, body, holes), fill);
  }
}

class _GlyphPainter extends CustomPainter {
  final void Function(Canvas canvas, Size size, Paint stroke, Paint fill) paintGlyph;
  final Color color;
  _GlyphPainter(this.paintGlyph, this.color);

  @override
  void paint(Canvas canvas, Size size) {
    final stroke = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = size.width / 12
      ..strokeCap = StrokeCap.round;
    final fill = Paint()..color = color;
    paintGlyph(canvas, size, stroke, fill);
  }

  @override
  bool shouldRepaint(_GlyphPainter old) => old.color != color || old.paintGlyph != paintGlyph;
}
