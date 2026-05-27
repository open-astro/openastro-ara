import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/library/library_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.7 per-session guiding RMS trend. Two lines (RA + Dec) plotted in
/// session-date order. Sessions without guider data are skipped.
class GuidingRmsChart extends ConsumerWidget {
  const GuidingRmsChart({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = [...ref.watch(librarySessionsProvider)]
      ..sort((a, b) => a.date.compareTo(b.date));
    final withRms = sessions
        .where((s) => s.guidingRmsRa != null && s.guidingRmsDec != null)
        .toList();

    final raSpots = <FlSpot>[];
    final decSpots = <FlSpot>[];
    for (var i = 0; i < withRms.length; i++) {
      raSpots.add(FlSpot(i.toDouble(), withRms[i].guidingRmsRa!));
      decSpots.add(FlSpot(i.toDouble(), withRms[i].guidingRmsDec!));
    }

    return ChartCard(
      title: 'Guiding RMS Trends',
      subtitle: 'Per-session RA + Dec RMS in arcseconds, chronological',
      child: raSpots.isEmpty
          ? Center(
              child: Text(
                'No sessions with guider data yet.',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(
                      color: AraColors.textDisabled,
                    ),
              ),
            )
          : Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 16, 8),
              child: LineChart(
                LineChartData(
                  minX: 0,
                  maxX: (withRms.length - 1).toDouble(),
                  minY: 0,
                  maxY: 1.5,
                  borderData: FlBorderData(
                    show: true,
                    border: Border.all(color: AraColors.border),
                  ),
                  gridData: FlGridData(
                    show: true,
                    getDrawingHorizontalLine: (_) => const FlLine(
                      color: AraColors.border,
                      strokeWidth: 0.5,
                    ),
                    getDrawingVerticalLine: (_) => const FlLine(
                      color: AraColors.border,
                      strokeWidth: 0.5,
                    ),
                  ),
                  titlesData: FlTitlesData(
                    leftTitles: const AxisTitles(
                      axisNameWidget: Text('arcsec'),
                      sideTitles: SideTitles(showTitles: true, reservedSize: 36),
                    ),
                    bottomTitles: AxisTitles(
                      axisNameWidget: const Text('session #'),
                      sideTitles: SideTitles(
                        showTitles: true,
                        reservedSize: 28,
                        interval: 1,
                        getTitlesWidget: (v, _) {
                          final i = v.toInt();
                          if (i < 0 || i >= withRms.length) {
                            return const SizedBox.shrink();
                          }
                          final d = withRms[i].date;
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
                  lineBarsData: [
                    LineChartBarData(
                      spots: raSpots,
                      isCurved: false,
                      color: AraColors.selectionBg,
                      barWidth: 2,
                      dotData: const FlDotData(show: true),
                    ),
                    LineChartBarData(
                      spots: decSpots,
                      isCurved: false,
                      color: AraColors.accentBusy,
                      barWidth: 2,
                      dotData: const FlDotData(show: true),
                    ),
                  ],
                ),
              ),
            ),
    );
  }
}
