import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';

import '../../models/autofocus_run.dart';
import '../../theme/ara_colors.dart';

/// The autofocus V-curve: HFR against focuser position. Kept fine probes are
/// solid dots, dropped probes hollow, coarse-search probes small and dim; the
/// daemon's sampled fit is the line; the fitted best position and the final
/// (confirmed) position are dashed markers. Everything the sweep measured is
/// on the chart, so a bad night reads as a bad night, not a tidy parabola.
class VCurveChart extends StatelessWidget {
  final AutofocusRun run;
  const VCurveChart({super.key, required this.run});

  @override
  Widget build(BuildContext context) {
    final sweep = run.sweepProbes;
    final coarse = run.coarseProbes;
    final fit = run.fit;
    if (sweep.isEmpty && coarse.isEmpty) {
      return Center(
        child: Text(
          run.isRunning
              ? 'Waiting for the first probe…'
              : 'Run autofocus to see the V-curve.',
          style: Theme.of(context)
              .textTheme
              .bodySmall
              ?.copyWith(color: AraColors.textSecondary),
        ),
      );
    }

    final xs = <double>[];
    final ys = <double>[];
    void include(double x, double y) {
      xs.add(x);
      if (y > 0) ys.add(y);
    }
    for (final p in sweep) {
      include(p.position.toDouble(), p.hfr);
    }
    // Coarse probes can sit thousands of steps away; only show the ones close
    // to the sweep (a quarter of its width beyond either end) so the fine
    // curve keeps its scale — the rest are in Details as a count.
    final sweepMin = sweep.isEmpty
        ? null
        : sweep.map((p) => p.position).reduce((a, b) => a < b ? a : b).toDouble();
    final sweepMax = sweep.isEmpty
        ? null
        : sweep.map((p) => p.position).reduce((a, b) => a > b ? a : b).toDouble();
    final span = sweepMin != null && sweepMax != null ? (sweepMax - sweepMin) : 0.0;
    final coarseShown = coarse.where((p) {
      if (sweepMin == null || sweepMax == null) return true;
      return p.position >= sweepMin - span * 0.25 && p.position <= sweepMax + span * 0.25;
    }).toList(growable: false);
    for (final p in coarseShown) {
      include(p.position.toDouble(), p.hfr);
    }
    if (fit != null) {
      for (final c in fit.curve) {
        include(c.position, c.hfr);
      }
      if (fit.bestPosition > 0) xs.add(fit.bestPosition);
    }
    if (run.finalPosition != null) xs.add(run.finalPosition!.toDouble());

    var minX = xs.reduce((a, b) => a < b ? a : b);
    var maxX = xs.reduce((a, b) => a > b ? a : b);
    if (maxX - minX < 1) {
      minX -= 50;
      maxX += 50;
    }
    final padX = (maxX - minX) * 0.06;
    minX -= padX;
    maxX += padX;
    final maxY = ys.isEmpty ? 5.0 : ys.reduce((a, b) => a > b ? a : b) * 1.15;

    List<FlSpot> spots(Iterable<AutofocusProbe> probes) => probes
        .where((p) => p.hfr > 0)
        .map((p) => FlSpot(p.position.toDouble(), p.hfr))
        .toList()
      ..sort((a, b) => a.x.compareTo(b.x));

    final kept = spots(sweep.where((p) => p.kept));
    final dropped = spots(sweep.where((p) => !p.kept));
    final coarseSpots = spots(coarseShown.where((p) => p.kept));
    final curve = fit == null
        ? const <FlSpot>[]
        : (fit.curve
            .where((c) => c.hfr > 0)
            .map((c) => FlSpot(c.position, c.hfr))
            .toList()
          ..sort((a, b) => a.x.compareTo(b.x)));

    final bars = <LineChartBarData>[
      if (curve.length >= 2)
        LineChartBarData(
          spots: curve,
          isCurved: false,
          color: AraColors.accentInfo.withValues(alpha: 0.85),
          barWidth: 2,
          dotData: const FlDotData(show: false),
        ),
      if (coarseSpots.isNotEmpty)
        _dots(coarseSpots, radius: 2.5, fill: AraColors.textDisabled, stroke: AraColors.textDisabled),
      if (dropped.isNotEmpty)
        _dots(dropped, radius: 4, fill: Colors.transparent, stroke: AraColors.textSecondary),
      if (kept.isNotEmpty)
        _dots(kept, radius: 4.5, fill: AraColors.textPrimary, stroke: AraColors.bgPanel),
    ];

    final verticals = <VerticalLine>[];
    if (fit != null && fit.bestPosition > 0) {
      verticals.add(VerticalLine(
        x: fit.bestPosition,
        color: AraColors.accentInfo,
        strokeWidth: 1,
        dashArray: const [4, 4],
        label: VerticalLineLabel(
          show: true,
          alignment: Alignment.topRight,
          style: const TextStyle(fontSize: 10, color: AraColors.accentInfo),
          labelResolver: (_) => fit.algorithm == 'calibration'
              ? 'calibrated ${fit.bestPosition.round()}'
              : 'fit ${fit.bestPosition.round()}',
        ),
      ));
    }
    if (run.finalPosition != null && run.isComplete) {
      verticals.add(VerticalLine(
        x: run.finalPosition!.toDouble(),
        color: AraColors.accentConnected,
        strokeWidth: 1.5,
        label: VerticalLineLabel(
          show: true,
          alignment: Alignment.bottomRight,
          style: const TextStyle(fontSize: 10, color: AraColors.accentConnected),
          labelResolver: (_) => 'focus ${run.finalPosition}',
        ),
      ));
    }

    return Padding(
      padding: const EdgeInsets.fromLTRB(8, 12, 16, 8),
      child: LineChart(
        LineChartData(
          minX: minX,
          maxX: maxX,
          minY: 0,
          maxY: maxY,
          clipData: const FlClipData.none(),
          borderData: FlBorderData(
            show: true,
            border: Border.all(color: AraColors.border),
          ),
          gridData: FlGridData(
            show: true,
            getDrawingHorizontalLine: (_) =>
                const FlLine(color: AraColors.border, strokeWidth: 0.5),
            getDrawingVerticalLine: (_) =>
                const FlLine(color: AraColors.border, strokeWidth: 0.5),
          ),
          titlesData: FlTitlesData(
            leftTitles: const AxisTitles(
              axisNameWidget: Text('HFR (px)', style: TextStyle(fontSize: 11)),
              sideTitles: SideTitles(showTitles: true, reservedSize: 36),
            ),
            bottomTitles: AxisTitles(
              axisNameWidget: const Text('Focuser position', style: TextStyle(fontSize: 11)),
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 26,
                interval: _niceInterval(maxX - minX),
                getTitlesWidget: (v, meta) => Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text(
                    v.round().toString(),
                    style: Theme.of(context).textTheme.labelSmall,
                  ),
                ),
              ),
            ),
            topTitles: const AxisTitles(),
            rightTitles: const AxisTitles(),
          ),
          extraLinesData: ExtraLinesData(verticalLines: verticals),
          lineTouchData: LineTouchData(
            touchTooltipData: LineTouchTooltipData(
              getTooltipItems: (touched) => touched
                  .map((t) => LineTooltipItem(
                        '${t.x.round()} · HFR ${t.y.toStringAsFixed(2)}',
                        const TextStyle(fontSize: 11, color: AraColors.textPrimary),
                      ))
                  .toList(),
            ),
          ),
          lineBarsData: bars,
        ),
        duration: const Duration(milliseconds: 150),
      ),
    );
  }

  static LineChartBarData _dots(List<FlSpot> spots,
          {required double radius, required Color fill, required Color stroke}) =>
      LineChartBarData(
        spots: spots,
        color: Colors.transparent,
        barWidth: 0,
        dotData: FlDotData(
          show: true,
          getDotPainter: (spot, pct, bar, index) => FlDotCirclePainter(
            radius: radius,
            color: fill,
            strokeColor: stroke,
            strokeWidth: 1.5,
          ),
        ),
      );

  /// ~5 labels across whatever the range is, rounded to 1/2/5 × 10ⁿ steps.
  static double _niceInterval(double range) {
    if (range <= 0) return 1;
    final raw = range / 5;
    var mag = 1.0;
    while (mag * 10 <= raw) {
      mag *= 10;
    }
    for (final m in [1, 2, 5, 10]) {
      if (mag * m >= raw) return mag * m;
    }
    return mag * 10;
  }
}
