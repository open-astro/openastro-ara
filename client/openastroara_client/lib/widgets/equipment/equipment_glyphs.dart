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

  /// A flat panel seen face on: a frame round a lit panel.
  const EquipmentGlyph.flatPanel({super.key, this.size = 28, required this.color}) : _paint = _flatPanel;

  /// A guide star held in a reticle: the guider chip.
  const EquipmentGlyph.guider({super.key, this.size = 28, required this.color}) : _paint = _guider;

  /// An observatory dome on its wall, with the shutter slit open.
  const EquipmentGlyph.dome({super.key, this.size = 28, required this.color}) : _paint = _dome;

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

  static void _flatPanel(Canvas c, Size s, Paint stroke, Paint fill) {
    final u = s.width / 24;
    final frame = Path()
      ..fillType = PathFillType.evenOdd
      ..addRect(Rect.fromLTRB(3 * u, 5 * u, 21 * u, 19 * u))
      ..addRect(Rect.fromLTRB(5 * u, 7 * u, 19 * u, 17 * u));
    c.drawPath(frame, fill);
    c.drawRect(Rect.fromLTRB(7 * u, 9 * u, 17 * u, 15 * u), fill);
  }

  static void _guider(Canvas c, Size s, Paint stroke, Paint fill) {
    final u = s.width / 24;
    // The reticle: a ring with four ticks reaching out to the edges.
    c.drawCircle(Offset(12 * u, 12 * u), 6.5 * u,
        Paint()
          ..color = fill.color
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.6 * u);
    for (final r in [
      Rect.fromLTRB(11 * u, 2 * u, 13 * u, 7 * u),
      Rect.fromLTRB(11 * u, 17 * u, 13 * u, 22 * u),
      Rect.fromLTRB(2 * u, 11 * u, 7 * u, 13 * u),
      Rect.fromLTRB(17 * u, 11 * u, 22 * u, 13 * u),
    ]) {
      c.drawRect(r, fill);
    }
    // The guide star: a five-point star in the middle.
    final star = Path();
    for (var i = 0; i < 10; i++) {
      final a = -math.pi / 2 + i * math.pi / 5;
      final r = (i.isEven ? 3.6 : 1.5) * u;
      final p = Offset(12 * u + r * math.cos(a), 12 * u + r * math.sin(a));
      i == 0 ? star.moveTo(p.dx, p.dy) : star.lineTo(p.dx, p.dy);
    }
    c.drawPath(star..close(), fill);
  }

  static void _dome(Canvas c, Size s, Paint stroke, Paint fill) {
    final u = s.width / 24;
    final dome = Path()
      ..moveTo(3 * u, 12 * u)
      ..arcToPoint(Offset(21 * u, 12 * u), radius: Radius.circular(9 * u))
      ..lineTo(21 * u, 14 * u)
      ..lineTo(3 * u, 14 * u)
      ..close();
    final slit = Path()
      ..moveTo(11 * u, 5.1 * u)
      ..lineTo(11 * u, 12 * u)
      ..lineTo(8.2 * u, 12 * u)
      ..arcToPoint(Offset(11 * u, 5.1 * u), radius: Radius.circular(7 * u))
      ..close();
    c.drawPath(Path.combine(PathOperation.difference, dome, slit), fill);
    c.drawRect(Rect.fromLTRB(2 * u, 16 * u, 22 * u, 20 * u), fill);
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
