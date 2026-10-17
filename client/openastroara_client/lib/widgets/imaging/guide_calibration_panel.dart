import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../models/guider_calibration_view.dart';
import '../../theme/ara_colors.dart';

/// #1364 (epic item 2) — the guider's calibration in Ara: the step plot PHD2
/// draws (RA legs blue, Dec legs red, each step a dot from the leg's start),
/// the stored result (angles, rates, orthogonality, declination) and Ara's
/// verdict with a reason. Clear lets the next guide start calibrate again.
class GuideCalibrationPanel extends StatelessWidget {
  final GuiderCalibrationView view;
  final VoidCallback? onClear;

  /// Enable the guider's Dec backlash compensation with the measured pulse;
  /// null hides the button (no measurement, nothing to apply, or busy).
  final void Function(int pulseMs)? onUseBacklash;
  const GuideCalibrationPanel({super.key, required this.view, this.onClear, this.onUseBacklash});

  @override
  Widget build(BuildContext context) {
    final text = Theme.of(context).textTheme;
    final small = text.bodySmall?.copyWith(color: AraColors.textSecondary);
    final data = view.data;
    final (verdictLabel, verdictColor, verdictIcon) = verdictStyle(view);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(
                'Calibration',
                style: text.titleSmall,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            Icon(verdictIcon, size: 14, color: verdictColor),
            const SizedBox(width: 4),
            Text(
              verdictLabel,
              key: const Key('calibration-verdict'),
              style: text.bodySmall?.copyWith(
                color: verdictColor,
                fontWeight: FontWeight.w600,
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(8),
            child: Container(
              color: const Color(0xFF07080B),
              child: view.hasSteps
                  ? CustomPaint(painter: CalibrationStepsPainter(view.steps))
                  : Center(
                      child: Text(
                        view.isRunning
                            ? 'Waiting for the first step…'
                            : 'No calibration this session',
                        style: small,
                        textAlign: TextAlign.center,
                      ),
                    ),
            ),
          ),
        ),
        const SizedBox(height: 6),
        Wrap(
          spacing: 10,
          runSpacing: 2,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                const _Swatch(color: AraColors.accentInfo),
                const SizedBox(width: 4),
                Text('RA (W/E)', style: small),
              ],
            ),
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                const _Swatch(color: AraColors.accentError),
                const SizedBox(width: 4),
                Text('Dec (N/S)', style: small),
              ],
            ),
            if (view.hasSteps) Text('${view.steps.length} steps', style: small),
          ],
        ),
        const SizedBox(height: 8),
        if (view.verdictReason != null)
          Text(
            view.verdictReason!,
            style: text.bodySmall?.copyWith(color: verdictColor),
          ),
        if (data != null && data.calibrated) ...[
          const SizedBox(height: 6),
          _fact(
            'RA axis',
            '${data.xAngleDeg?.toStringAsFixed(1) ?? '—'}°  ·  ${data.xRatePxPerSec?.toStringAsFixed(2) ?? '—'} px/s',
            small,
          ),
          _fact(
            'Dec axis',
            '${data.yAngleDeg?.toStringAsFixed(1) ?? '—'}°  ·  ${data.yRatePxPerSec?.toStringAsFixed(2) ?? '—'} px/s',
            small,
          ),
          _fact(
            'Orthogonality',
            data.orthogonalityErrorDeg == null
                ? '—'
                : '${data.orthogonalityErrorDeg!.toStringAsFixed(1)}° off square',
            small,
          ),
          _fact(
            'Declination',
            data.declinationDeg == null
                ? '—'
                : '${data.declinationDeg!.toStringAsFixed(1)}°',
            small,
          ),
        ],
        // What the calibration measured about the mount's Dec gear (2026-10-16:
        // 3–10 clearing steps every run, about 1.5 s, and compensation off).
        if (describeDecBacklash(view) case final backlash?) ...[
          const SizedBox(height: 6),
          Text(backlash, key: const Key('calibration-backlash'), style: small),
          if (view.estimatedDecBacklashMs case final ms? when ms > 0 && onUseBacklash != null)
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                key: const Key('calibration-use-backlash'),
                onPressed: () => onUseBacklash!(ms),
                icon: const Icon(Icons.tune, size: 16),
                label: Text('Use $ms ms for backlash compensation'),
              ),
            ),
        ],
        Align(
          alignment: Alignment.centerRight,
          child: TextButton.icon(
            onPressed: onClear,
            icon: const Icon(Icons.restart_alt, size: 16),
            label: const Text('Clear calibration'),
          ),
        ),
      ],
    );
  }

  static Widget _fact(String label, String value, TextStyle? small) => Padding(
    padding: const EdgeInsets.only(bottom: 2),
    child: Row(
      children: [
        SizedBox(width: 96, child: Text(label, style: small)),
        Expanded(
          child: Text(
            value,
            style: small?.copyWith(color: AraColors.textPrimary),
          ),
        ),
      ],
    ),
  );

  /// Label, colour and icon for a verdict. Pure.
  static (String, Color, IconData) verdictStyle(GuiderCalibrationView v) {
    if (!v.connected) {
      return ('No guider', AraColors.accentDisconnected, Icons.link_off);
    }
    if (v.isRunning) {
      return ('Calibrating…', AraColors.accentBusy, Icons.timelapse);
    }
    return switch (v.verdict) {
      'good' => ('Good', AraColors.accentConnected, Icons.check_circle_outline),
      'check' => ('Check', AraColors.accentWarning, Icons.error_outline),
      _ => ('Redo', AraColors.accentError, Icons.replay),
    };
  }
}

class _Swatch extends StatelessWidget {
  final Color color;
  const _Swatch({required this.color});
  @override
  Widget build(BuildContext context) => Container(
    width: 10,
    height: 3,
    decoration: BoxDecoration(
      color: color,
      borderRadius: BorderRadius.circular(2),
    ),
  );
}

/// PHD2's calibration plot: each step a dot at (dx, dy) from the leg's start,
/// RA legs blue, Dec legs red, joined in order; axes through the origin; the
/// scale fits the farthest step with a margin.
class CalibrationStepsPainter extends CustomPainter {
  final List<GuiderCalibrationStep> steps;
  CalibrationStepsPainter(this.steps);

  /// Pixels per guide-px so the farthest step sits inside the square with a
  /// 12% margin; never below a floor so a one-step plot is still visible. Pure.
  static double scaleFor(List<GuiderCalibrationStep> steps, double side) {
    var maxR = 1.0;
    for (final s in steps) {
      maxR = math.max(maxR, math.max(s.dx.abs(), s.dy.abs()));
    }
    return (side / 2) * 0.88 / maxR;
  }

  @override
  void paint(Canvas canvas, Size size) {
    final c = Offset(size.width / 2, size.height / 2);
    final axis = Paint()
      ..color = AraColors.border
      ..strokeWidth = 1;
    canvas.drawLine(Offset(0, c.dy), Offset(size.width, c.dy), axis);
    canvas.drawLine(Offset(c.dx, 0), Offset(c.dx, size.height), axis);
    final scale = scaleFor(steps, math.min(size.width, size.height));
    Offset at(GuiderCalibrationStep s) =>
        c + Offset(s.dx * scale, -s.dy * scale);
    String? lastLeg;
    Offset? last;
    for (final s in steps) {
      final color = s.isRa ? AraColors.accentInfo : AraColors.accentError;
      final p = at(s);
      final line = Paint()
        ..color = color.withValues(alpha: 0.7)
        ..strokeWidth = 1.2;
      if (last != null && lastLeg == s.direction) {
        canvas.drawLine(last, p, line);
      } else {
        canvas.drawLine(c, p, line);
      }
      canvas.drawCircle(p, 3, Paint()..color = color);
      last = p;
      lastLeg = s.direction;
    }
  }

  @override
  bool shouldRepaint(CalibrationStepsPainter old) => old.steps != steps;
}
