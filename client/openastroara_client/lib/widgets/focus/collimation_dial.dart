import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/collimation_verdict.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../help_icon.dart';

/// §59.10 / #1139 — the collimation verdict as a picture: the donut as the
/// sweep saw it, with the obstruction shadow drawn where the daemon measured
/// it (offset exaggerated so a 5 % shift is visible), the headline, the
/// numbers, and what it usually means for this optical design. Rendered in
/// the IMAGE frame, as displayed — the eyepiece view needs the train's mirror
/// flips, which the client cannot know, and the caption says so.
class CollimationCard extends ConsumerWidget {
  final CollimationVerdict verdict;
  const CollimationCard({super.key, required this.verdict});

  static Color colorFor(CollimationSeverity s) => switch (s) {
    CollimationSeverity.good => AraColors.accentConnected,
    CollimationSeverity.slight => AraColors.accentBusy,
    CollimationSeverity.significant => AraColors.accentError,
  };

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final color = colorFor(verdict.severity);
    final scopeType = ref.watch(
      autofocusSettingsProvider.select((s) => s.telescopeType.name),
    );
    final hint = collimationHintFor(scopeType, verdict.severity);
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: color.withValues(alpha: 0.4)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Semantics(
            label:
                'Donut with the shadow offset ${verdict.offsetPercent.toStringAsFixed(1)} percent toward ${verdict.clockHour} o\'clock',
            child: CustomPaint(
              size: const Size(72, 72),
              painter: CollimationDialPainter(verdict: verdict, color: color),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        verdict.headline,
                        style: theme.textTheme.titleSmall?.copyWith(
                          color: color,
                        ),
                      ),
                    ),
                    const HelpIcon(helpKey: 'focus.collimation'),
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  verdict.detail,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
                if (verdict.severity != CollimationSeverity.good) ...[
                  const SizedBox(height: 4),
                  Text(hint, style: theme.textTheme.bodySmall),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The donut: outer ring, and the central-obstruction shadow displaced by the
/// measured offset. The displacement is drawn at 3× so a slight (5 %) shift
/// reads at 72 px; a dotted cross marks the ring centre for reference.
class CollimationDialPainter extends CustomPainter {
  final CollimationVerdict verdict;
  final Color color;
  const CollimationDialPainter({required this.verdict, required this.color});

  /// Exaggeration so a 5 % offset is visibly off-centre at thumbnail size.
  static const double gain = 3;

  @override
  void paint(Canvas canvas, Size size) {
    final c = Offset(size.width / 2, size.height / 2);
    final outer = math.min(size.width, size.height) / 2 - 2;
    final ring = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2
      ..color = AraColors.textSecondary;
    canvas.drawCircle(c, outer, ring);
    // Centre reference cross.
    final cross = Paint()
      ..color = AraColors.textDisabled
      ..strokeWidth = 1;
    canvas.drawLine(
      c - Offset(outer * 0.25, 0),
      c + Offset(outer * 0.25, 0),
      cross,
    );
    canvas.drawLine(
      c - Offset(0, outer * 0.25),
      c + Offset(0, outer * 0.25),
      cross,
    );
    // The shadow: offset_percent is a fraction of the donut DIAMETER.
    final shift = (verdict.offsetPercent / 100 * (2 * outer) * gain).clamp(
      0.0,
      outer * 0.6,
    );
    final a = verdict.directionDegrees * math.pi / 180; // image frame, y down
    final shadowCentre = c + Offset(math.cos(a) * shift, math.sin(a) * shift);
    final shadow = Paint()..color = color.withValues(alpha: 0.85);
    canvas.drawCircle(shadowCentre, outer * 0.38, shadow);
    if (verdict.severity != CollimationSeverity.good) {
      final arrow = Paint()
        ..color = color
        ..strokeWidth = 1.5;
      canvas.drawLine(c, shadowCentre, arrow);
    }
  }

  @override
  bool shouldRepaint(covariant CollimationDialPainter old) =>
      old.verdict != verdict || old.color != color;
}
