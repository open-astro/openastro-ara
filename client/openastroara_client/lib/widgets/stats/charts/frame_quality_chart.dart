import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/library/library_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.10 frame quality composite — per-session average HFR (lower = better)
/// rendered as a bar chart. Phase 12g.2 keeps it to HFR; the §50.10 full
/// composite (HFR + star count + ADU background) lands in 12g.3 when the
/// real `/api/v1/stats/frame-quality` endpoint provides the canonical
/// formula.
class FrameQualityChart extends ConsumerWidget {
  const FrameQualityChart({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = [...ref.watch(librarySessionsProvider)]
      ..sort((a, b) => a.date.compareTo(b.date));

    final bars = <BarChartGroupData>[];
    var observedMax = 0.0;
    for (var i = 0; i < sessions.length; i++) {
      final lights = sessions[i].frames
          .where((f) => f.frameType.toLowerCase() == 'light')
          .toList();
      if (lights.isEmpty) continue;
      final avgHfr =
          lights.map((f) => f.hfr).reduce((a, b) => a + b) / lights.length;
      if (avgHfr > observedMax) observedMax = avgHfr;
      bars.add(BarChartGroupData(x: i, barRods: [
        BarChartRodData(
          toY: avgHfr,
          color: avgHfr <= 1.7
              ? AraColors.selectionBg
              : avgHfr <= 2.0
                  ? AraColors.accentBusy
                  : Colors.redAccent,
          width: 16,
          borderRadius: const BorderRadius.vertical(top: Radius.circular(4)),
        ),
      ]));
    }
    // Pad 0.2 over the observed max but keep a sensible floor of 2.5 so the
    // color bands stay legible on calm sessions; ceiling at 6.0 prevents a
    // single bad seeing night from squashing everything else.
    final yMax = (observedMax + 0.2).clamp(2.5, 6.0);

    return ChartCard(
      title: 'Frame Quality (avg HFR per session)',
      subtitle:
          'Lower = sharper. Bands: blue ≤ 1.70, amber ≤ 2.00, red > 2.00 (§50.10 composite lands in 12g.3)',
      child: bars.isEmpty
          ? Center(
              child: Text(
                'No light frames yet — capture a session.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AraColors.textDisabled,
                    ),
              ),
            )
          : Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 16, 8),
              child: BarChart(
                BarChartData(
                  alignment: BarChartAlignment.spaceAround,
                  maxY: yMax,
                  borderData: FlBorderData(
                    show: true,
                    border: Border.all(color: AraColors.border),
                  ),
                  gridData: FlGridData(
                    show: true,
                    drawVerticalLine: false,
                    getDrawingHorizontalLine: (_) => const FlLine(
                      color: AraColors.border,
                      strokeWidth: 0.5,
                    ),
                  ),
                  titlesData: FlTitlesData(
                    leftTitles: const AxisTitles(
                      axisNameWidget: Text('HFR'),
                      sideTitles: SideTitles(showTitles: true, reservedSize: 36),
                    ),
                    bottomTitles: AxisTitles(
                      sideTitles: SideTitles(
                        showTitles: true,
                        reservedSize: 28,
                        getTitlesWidget: (v, _) {
                          final i = v.toInt();
                          if (i < 0 || i >= sessions.length) {
                            return const SizedBox.shrink();
                          }
                          final d = sessions[i].date;
                          return Padding(
                            padding: const EdgeInsets.only(top: 4),
                            child: Text(
                              '${d.month}/${d.day}',
                              style: Theme.of(context).textTheme.labelSmall,
                            ),
                          );
                        },
                      ),
                    ),
                    topTitles: const AxisTitles(),
                    rightTitles: const AxisTitles(),
                  ),
                  barGroups: bars,
                ),
              ),
            ),
    );
  }
}
