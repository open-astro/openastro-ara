import 'package:flutter/material.dart';

/// The OpenAstro Ara mark: the app icon's constellation
/// (icon_sources/app_icon_1024.png) without its tile, traced from the icon.
class AraMark extends StatelessWidget {
  const AraMark({super.key, required this.size});

  final double size;

  @override
  Widget build(BuildContext context) =>
      CustomPaint(size: Size.square(size), painter: const _AraMarkPainter());
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
    colors: [Color(0xFF4FC3F7), Color(0xFF7C8CF8)],
  );

  @override
  void paint(Canvas canvas, Size size) {
    canvas
      ..scale(size.width / _box.width)
      ..translate(-_box.left, -_box.top);
    canvas.drawCircle(
      _bright,
      110,
      Paint()
        ..shader = RadialGradient(
          colors: [
            Colors.white.withValues(alpha: .55),
            const Color(0x008FA8FF),
          ],
        ).createShader(Rect.fromCircle(center: _bright, radius: 110)),
    );
    final stroke = Paint()
      ..shader = _line.createShader(_box)
      ..style = PaintingStyle.stroke
      ..strokeJoin = StrokeJoin.round;
    canvas.drawPath(
      Path()..addPolygon([..._stars, _bright], false),
      stroke..strokeWidth = 16,
    );
    final hole = Paint()..color = const Color(0xFF0B0F24);
    stroke.strokeWidth = 14;
    for (final (i, c) in _stars.indexed) {
      if (_radii[i] == 0) continue;
      canvas
        ..drawCircle(c, _radii[i], hole)
        ..drawCircle(c, _radii[i], stroke);
    }
    canvas.drawCircle(_bright, 30, Paint()..color = Colors.white);
  }

  @override
  bool shouldRepaint(_AraMarkPainter old) => false;
}
