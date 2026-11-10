import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';

/// The OpenAstro Ara mark: the app icon's constellation
/// (icon_sources/app_icon_1024.png) without its tile, traced from the icon.
class AraMark extends StatelessWidget {
  const AraMark({super.key, required this.size});

  final double size;

  @override
  Widget build(BuildContext context) => CustomPaint(
    size: Size.square(size),
    painter: const _AraMarkPainter(),
  );
}

class _AraMarkPainter extends CustomPainter {
  const _AraMarkPainter();

  // The icon's star centres in its 1024 px canvas, in drawing order; the
  // line ends back on the bright star (514, 246).
  static const _stars = [
    Offset(242, 270),
    Offset(514, 246),
    Offset(745, 412),
    Offset(742, 556),
    Offset(782, 715),
    Offset(521, 777),
    Offset(560, 567),
    Offset(557, 525),
  ];
  static const _radii = [22.0, 0.0, 22.0, 18.0, 22.0, 22.0, 18.0, 14.0];
  static const _bright = Offset(514, 246);
  static const _box = Rect.fromLTWH(200, 200, 630, 630);
  static const _line = LinearGradient(
    colors: [AraColors.brandLineStart, AraColors.brandLineEnd],
  );

  @override
  void paint(Canvas canvas, Size size) {
    // Icon units per logical pixel. Small, the icon's hairlines would vanish
    // (the strip draws it at 30 px), so no line, ring or star goes under its
    // floor in pixels: lines 2, rings 1.5 wide and 3 across, the star 6.
    final px = _box.width / size.width;
    double atLeast(double units, double pixels) =>
        math.max(units, pixels * px);
    canvas
      ..scale(1 / px)
      ..translate(-_box.left, -_box.top);
    canvas.drawCircle(
      _bright,
      110,
      Paint()
        ..shader = RadialGradient(
          colors: [
            AraColors.starlight.withValues(alpha: .55),
            AraColors.brandGlow.withValues(alpha: 0),
          ],
        ).createShader(Rect.fromCircle(center: _bright, radius: 110)),
    );
    final stroke = Paint()
      ..shader = _line.createShader(_box)
      ..style = PaintingStyle.stroke
      ..strokeJoin = StrokeJoin.round;
    canvas.drawPath(
      Path()..addPolygon([..._stars, _bright], false),
      stroke..strokeWidth = atLeast(16, 2),
    );
    final hole = Paint()..color = AraColors.brandStarHole;
    stroke.strokeWidth = atLeast(14, 1.5);
    for (final (i, c) in _stars.indexed) {
      if (_radii[i] == 0) continue;
      final r = atLeast(_radii[i], 1.5);
      canvas
        ..drawCircle(c, r, hole)
        ..drawCircle(c, r, stroke);
    }
    canvas.drawCircle(_bright, atLeast(30, 3), Paint()..color = AraColors.starlight);
  }

  @override
  bool shouldRepaint(_AraMarkPainter old) => false;
}
