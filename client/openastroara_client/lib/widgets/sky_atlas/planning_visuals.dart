import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../services/tonight_sky_api.dart';
import '../../theme/ara_colors.dart';

/// §Planning redesign S4/S5/S6 — the three data-pictures that replace prose
/// in the Tonight's Sky rows: the dark-window strip, the framing glyph and
/// the banked-hours budget ring. Pure widgets over wire values; each carries
/// a [Semantics] label so the prose it replaced survives for a11y.

/// S4 — the object's dark window as a 4 px timeline: a night-span track, the
/// window as a filled segment, the transit as a tick, "now" as a dot. The
/// track spans window ± 1 h so the segment reads in context.
class DarkWindowStrip extends StatelessWidget {
  const DarkWindowStrip({
    super.key,
    required this.windowStartUtc,
    required this.windowEndUtc,
    this.transitUtc,
    this.nowUtc,
    this.color = AraColors.accentInfo,
  });

  final DateTime windowStartUtc;
  final DateTime windowEndUtc;
  final DateTime? transitUtc;

  /// Injectable clock for tests; defaults to now.
  final DateTime? nowUtc;
  final Color color;

  static String _hhmm(DateTime utc) {
    final t = utc.toLocal();
    return '${t.hour.toString().padLeft(2, '0')}:'
        '${t.minute.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final now = nowUtc ?? DateTime.now().toUtc();
    final trackStart = windowStartUtc.subtract(const Duration(hours: 1));
    final trackEnd = windowEndUtc.add(const Duration(hours: 1));
    final span = trackEnd.difference(trackStart).inSeconds.toDouble();
    double frac(DateTime t) =>
        (t.difference(trackStart).inSeconds / span).clamp(0.0, 1.0);

    final label = 'Dark window ${_hhmm(windowStartUtc)} to '
        '${_hhmm(windowEndUtc)}'
        '${transitUtc == null ? '' : ', transit ${_hhmm(transitUtc!)}'}';

    return Semantics(
      label: label,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(
            height: 8,
            child: CustomPaint(
              size: const Size(double.infinity, 8),
              painter: _StripPainter(
                windowStart: frac(windowStartUtc),
                windowEnd: frac(windowEndUtc),
                transit: transitUtc == null ? null : frac(transitUtc!),
                now: now.isAfter(trackStart) && now.isBefore(trackEnd)
                    ? frac(now)
                    : null,
                color: color,
              ),
            ),
          ),
          const SizedBox(height: 2),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(_hhmm(windowStartUtc), style: _tinyTime),
              if (transitUtc != null)
                Text('▲ ${_hhmm(transitUtc!)}', style: _tinyTime),
              Text(_hhmm(windowEndUtc), style: _tinyTime),
            ],
          ),
        ],
      ),
    );
  }

  static const _tinyTime = TextStyle(
    color: AraColors.textDisabled,
    fontSize: 9.5,
    fontFeatures: [FontFeature.tabularFigures()],
  );
}

class _StripPainter extends CustomPainter {
  _StripPainter({
    required this.windowStart,
    required this.windowEnd,
    required this.transit,
    required this.now,
    required this.color,
  });

  final double windowStart;
  final double windowEnd;
  final double? transit;
  final double? now;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final midY = size.height / 2;
    const trackH = 3.0;
    final track = Paint()
      ..color = AraColors.bgInput
      ..style = PaintingStyle.fill;
    RRect bar(double x0, double x1, double h) => RRect.fromRectAndRadius(
          Rect.fromLTRB(x0 * size.width, midY - h / 2, x1 * size.width,
              midY + h / 2),
          const Radius.circular(2),
        );
    canvas.drawRRect(bar(0, 1, trackH), track);
    canvas.drawRRect(
        bar(windowStart, windowEnd, trackH + 1), Paint()..color = color);
    if (transit case final t?) {
      canvas.drawRect(
        Rect.fromCenter(
            center: Offset(t * size.width, midY), width: 1.5, height: 8),
        Paint()..color = AraColors.textSecondary,
      );
    }
    if (now case final n?) {
      canvas.drawCircle(Offset(n * size.width, midY), 3,
          Paint()..color = AraColors.accentConnected);
    }
  }

  @override
  bool shouldRepaint(_StripPainter old) =>
      old.windowStart != windowStart ||
      old.windowEnd != windowEnd ||
      old.transit != transit ||
      old.now != now ||
      old.color != color;
}

/// S5 — the framing glyph: YOUR sensor rectangle with the object's catalog
/// ellipse drawn to scale inside it, tinted by the framing tier. The word
/// "too small" becomes visible geometry. Renders nothing without both a FOV
/// and an object size (an empty box would be noise, not signal).
class FramingGlyph extends StatelessWidget {
  const FramingGlyph({
    super.key,
    required this.fovWArcmin,
    required this.fovHArcmin,
    required this.object,
    this.side = 44,
  });

  /// The rig's single-frame FOV in arcminutes (0/negative = unknown).
  final double fovWArcmin;
  final double fovHArcmin;
  final TonightSkyObject object;
  final double side;

  @override
  Widget build(BuildContext context) {
    final maj = object.sizeMajArcmin;
    if (fovWArcmin <= 0 || fovHArcmin <= 0 || maj == null || maj <= 0) {
      return const SizedBox.shrink();
    }
    final min = object.sizeMinArcmin ?? maj;
    final color = switch (object.framing) {
      TonightFraming.good => AraColors.accentConnected,
      TonightFraming.goodFit => AraColors.accentInfo,
      TonightFraming.tooSmall || TonightFraming.tooBig => AraColors.accentBusy,
      TonightFraming.unknown => AraColors.textSecondary,
    };
    return Semantics(
      label: 'Framing: object ${maj.toStringAsFixed(0)} arcminutes across in '
          'a ${fovWArcmin.toStringAsFixed(0)} by '
          '${fovHArcmin.toStringAsFixed(0)} arcminute field',
      child: SizedBox(
        width: side,
        height: side,
        child: CustomPaint(
          painter: _GlyphPainter(
            fovW: fovWArcmin,
            fovH: fovHArcmin,
            objMaj: maj,
            objMin: min,
            posAngleDeg: object.posAngleDeg ?? 0,
            color: color,
          ),
        ),
      ),
    );
  }
}

class _GlyphPainter extends CustomPainter {
  _GlyphPainter({
    required this.fovW,
    required this.fovH,
    required this.objMaj,
    required this.objMin,
    required this.posAngleDeg,
    required this.color,
  });

  final double fovW;
  final double fovH;
  final double objMaj;
  final double objMin;
  final double posAngleDeg;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    // One scale for both shapes, chosen so the LARGER of (FOV, object) fits
    // the box with margin — an oversized object honestly overflows the frame
    // rectangle (clipped to the widget), a small one honestly rattles in it.
    final maxArc = math.max(math.max(fovW, fovH), objMaj) * 1.15;
    final pxPerArc = math.min(size.width, size.height) / maxArc;
    final c = Offset(size.width / 2, size.height / 2);

    final frame = Rect.fromCenter(
      center: c,
      width: fovW * pxPerArc,
      height: fovH * pxPerArc,
    );
    canvas.drawRRect(
      RRect.fromRectAndRadius(frame, const Radius.circular(2)),
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = AraColors.textSecondary.withValues(alpha: 0.9),
    );

    canvas.save();
    canvas.clipRect(Offset.zero & size);
    canvas.translate(c.dx, c.dy);
    canvas.rotate(posAngleDeg * math.pi / 180);
    final ellipse = Rect.fromCenter(
      center: Offset.zero,
      width: objMaj * pxPerArc,
      height: objMin * pxPerArc,
    );
    canvas.drawOval(
        ellipse, Paint()..color = color.withValues(alpha: 0.30));
    canvas.drawOval(
      ellipse,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = color,
    );
    canvas.restore();
  }

  @override
  bool shouldRepaint(_GlyphPainter old) =>
      old.fovW != fovW ||
      old.fovH != fovH ||
      old.objMaj != objMaj ||
      old.objMin != objMin ||
      old.posAngleDeg != posAngleDeg ||
      old.color != color;
}

/// S6 — the banked-hours ring: [banked] ÷ [needed] as an Activity-style arc;
/// a filled check state once the full tier is banked. Null [banked] renders
/// an empty ring (nothing captured yet).
class BudgetRing extends StatelessWidget {
  const BudgetRing({
    super.key,
    required this.banked,
    required this.needed,
    this.size = 26,
  });

  final double? banked;
  final double needed;
  final double size;

  @override
  Widget build(BuildContext context) {
    final fraction =
        needed > 0 ? ((banked ?? 0) / needed).clamp(0.0, 1.0) : 0.0;
    final complete = fraction >= 1.0;
    return Semantics(
      label:
          '${(banked ?? 0).toStringAsFixed(1)} of ${needed.toStringAsFixed(0)} '
          'hours captured',
      child: SizedBox(
        width: size,
        height: size,
        child: Stack(
          alignment: Alignment.center,
          children: [
            CustomPaint(
              size: Size.square(size),
              painter: _RingPainter(
                fraction: fraction,
                color: complete
                    ? AraColors.accentConnected
                    : AraColors.accentInfo,
              ),
            ),
            if (complete)
              const Icon(Icons.check,
                  size: 13, color: AraColors.accentConnected),
          ],
        ),
      ),
    );
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter({required this.fraction, required this.color});
  final double fraction;
  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final c = Offset(size.width / 2, size.height / 2);
    final r = size.width / 2 - 2;
    canvas.drawCircle(
      c,
      r,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 3
        ..color = AraColors.bgInput,
    );
    if (fraction > 0) {
      canvas.drawArc(
        Rect.fromCircle(center: c, radius: r),
        -math.pi / 2,
        2 * math.pi * fraction,
        false,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 3
          ..strokeCap = StrokeCap.round
          ..color = color,
      );
    }
  }

  @override
  bool shouldRepaint(_RingPainter old) =>
      old.fraction != fraction || old.color != color;
}
