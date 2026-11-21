import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';

import '../../models/guide_focus.dart';
import '../../theme/ara_colors.dart';

/// The guide-camera focus trend: HFR per frame (newest right) with the best HFR
/// so far as a dashed floor. Starless frames (HFR 0) leave a gap rather than
/// dragging the line to zero.
class HfrTrendChart extends StatelessWidget {
  final List<GuideFocusSample> samples;
  final double? bestHfr;
  const HfrTrendChart({super.key, required this.samples, this.bestHfr});

  @override
  Widget build(BuildContext context) {
    final measured = samples.where((s) => s.hfr > 0).toList(growable: false);
    if (measured.length < 2) {
      return Center(
        child: Text(
          samples.isEmpty
              ? 'The HFR trend appears after the first frames.'
              : 'No stars measured yet — point at a star field or lengthen the exposure.',
          style: Theme.of(context)
              .textTheme
              .bodySmall
              ?.copyWith(color: AraColors.textSecondary),
          textAlign: TextAlign.center,
        ),
      );
    }
    final spots = <FlSpot>[];
    var maxY = 0.0;
    for (var i = 0; i < samples.length; i++) {
      final s = samples[i];
      if (s.hfr <= 0) continue;
      spots.add(FlSpot(i.toDouble(), s.hfr));
      if (s.hfr > maxY) maxY = s.hfr;
    }
    maxY = (maxY * 1.2).clamp(1.0, 50.0);
    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 12, 16, 8),
      child: LineChart(
        LineChartData(
          minX: 0,
          maxX: (samples.length - 1).clamp(1, 1 << 20).toDouble(),
          minY: 0,
          maxY: maxY,
          borderData: FlBorderData(
            show: true,
            border: Border.all(color: AraColors.border),
          ),
          gridData: FlGridData(
            show: true,
            drawVerticalLine: false,
            getDrawingHorizontalLine: (_) =>
                const FlLine(color: AraColors.border, strokeWidth: 0.5),
          ),
          titlesData: const FlTitlesData(
            leftTitles: AxisTitles(
              axisNameWidget: Text('HFR (px)', style: TextStyle(fontSize: 11)),
              sideTitles: SideTitles(showTitles: true, reservedSize: 36),
            ),
            bottomTitles: AxisTitles(
              axisNameWidget: Text('frames', style: TextStyle(fontSize: 11)),
            ),
            topTitles: AxisTitles(),
            rightTitles: AxisTitles(),
          ),
          extraLinesData: ExtraLinesData(
            horizontalLines: [
              if (bestHfr != null && bestHfr! > 0)
                HorizontalLine(
                  y: bestHfr!,
                  color: AraColors.accentConnected,
                  strokeWidth: 1,
                  dashArray: const [4, 4],
                  label: HorizontalLineLabel(
                    show: true,
                    alignment: Alignment.topLeft,
                    style: const TextStyle(fontSize: 10, color: AraColors.accentConnected),
                    labelResolver: (_) => 'best ${bestHfr!.toStringAsFixed(2)}',
                  ),
                ),
            ],
          ),
          lineTouchData: const LineTouchData(enabled: false),
          lineBarsData: [
            LineChartBarData(
              spots: spots,
              isCurved: false,
              color: AraColors.selectionBg,
              barWidth: 2,
              dotData: FlDotData(show: spots.length <= 40),
            ),
          ],
        ),
        duration: Duration.zero,
      ),
    );
  }
}
