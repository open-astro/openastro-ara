import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/library/library_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.4 Focus & Temperature scatter. Each captured frame is a point at
/// (sensor temp, focus position). Slope tells the user the focuser's
/// temperature coefficient — useful for §37.4 temp-comp setup.
class FocusTempScatterChart extends ConsumerWidget {
  const FocusTempScatterChart({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(librarySessionsProvider);
    final points = <ScatterSpot>[];
    for (final session in sessions) {
      for (final frame in session.frames) {
        points.add(ScatterSpot(
          frame.sensorTempC,
          frame.focusSteps.toDouble(),
          dotPainter: FlDotCirclePainter(
            color: AraColors.selectionBg.withValues(alpha: 0.7),
            radius: 4,
          ),
        ));
      }
    }

    return ChartCard(
      title: 'Focus & Temperature',
      subtitle: 'Focus position vs sensor temperature — slope = temp-comp coefficient',
      child: points.isEmpty
          ? const _EmptyHint()
          : Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 16, 8),
              child: ScatterChart(
                ScatterChartData(
                  scatterSpots: points,
                  minX: _minX(points) - 1,
                  maxX: _maxX(points) + 1,
                  minY: _minY(points) - 20,
                  maxY: _maxY(points) + 20,
                  borderData: FlBorderData(
                    show: true,
                    border: Border.all(
                      color: AraColors.border,
                    ),
                  ),
                  gridData: FlGridData(
                    show: true,
                    drawVerticalLine: true,
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
                    leftTitles: AxisTitles(
                      axisNameWidget: const Text('Focus steps'),
                      sideTitles: const SideTitles(showTitles: true, reservedSize: 48),
                    ),
                    bottomTitles: AxisTitles(
                      axisNameWidget: const Text('Sensor temp (°C)'),
                      sideTitles: const SideTitles(showTitles: true, reservedSize: 28),
                    ),
                    topTitles: const AxisTitles(),
                    rightTitles: const AxisTitles(),
                  ),
                ),
              ),
            ),
    );
  }

  double _minX(List<ScatterSpot> p) => p.map((s) => s.x).reduce((a, b) => a < b ? a : b);
  double _maxX(List<ScatterSpot> p) => p.map((s) => s.x).reduce((a, b) => a > b ? a : b);
  double _minY(List<ScatterSpot> p) => p.map((s) => s.y).reduce((a, b) => a < b ? a : b);
  double _maxY(List<ScatterSpot> p) => p.map((s) => s.y).reduce((a, b) => a > b ? a : b);
}

class _EmptyHint extends StatelessWidget {
  const _EmptyHint();
  @override
  Widget build(BuildContext context) => Center(
        child: Text(
          'No frames yet — capture a session to populate this chart.',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
                color: AraColors.textDisabled,
              ),
        ),
      );
}
