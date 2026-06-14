import 'dart:async';

import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/stats/guiding_rms.dart';
import '../../../state/stats/guiding_rms_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.7 guiding RMS trend — per-frame total guiding RMS (arcsec) over time
/// from the live daemon (`GET /api/v1/stats/guiding`), with mean + p95 summary
/// stats. Replaces the Phase-12g demo that plotted per-session RA/Dec from the
/// in-memory library. (The daemon nulls the RA/Dec breakdown until separated
/// columns exist, so today this is a single total-RMS line.)
class GuidingRmsChart extends ConsumerWidget {
  const GuidingRmsChart({super.key});

  // Plot at most the most-recent N samples so a very long capture history can't
  // hand fl_chart a huge spot list. Granularity is per captured frame (not per
  // guide pulse), so this is generous headroom; mean/p95 in the subtitle stay
  // full-history (computed server-side over every sample).
  static const int _maxPlotted = 500;

  // Y-axis band: floor so the color/grid stays legible on calm sessions, ceiling
  // so one bad-seeing outlier doesn't squash the trend. Samples above the ceiling
  // are clipped by fl_chart, so the subtitle calls out how many.
  static const double _yFloor = 1.5;
  static const double _yCeiling = 5.0;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(guidingRmsProvider);
    final series = async.asData?.value;

    final total = series?.samples.length ?? 0;
    final shown = _plotted(series);
    final overCeiling = shown.where((s) => s.rmsArcsec > _yCeiling).length;
    final summary = series == null || series.isEmpty
        ? null
        : [
            if (series.meanRmsArcsec != null)
              'mean ${series.meanRmsArcsec!.toStringAsFixed(2)}″',
            if (series.p95RmsArcsec != null)
              'p95 ${series.p95RmsArcsec!.toStringAsFixed(2)}″',
            if (total > _maxPlotted) 'latest $_maxPlotted of $total',
            if (overCeiling > 0)
              '$overCeiling above ${_yCeiling.toStringAsFixed(0)}″ (clipped)',
          ].join(' · ');

    return ChartCard(
      title: 'Guiding RMS Trend',
      subtitle: summary == null || summary.isEmpty
          ? 'Per-frame total guiding RMS (arcsec), chronological — lower is better.'
          : 'Per-frame total guiding RMS (arcsec), chronological — $summary.',
      child: Stack(
        children: [
          _body(context, ref, async, series, shown),
          Positioned(
            top: 0,
            right: 4,
            child: IconButton(
              tooltip: 'Refresh',
              iconSize: 18,
              visualDensity: VisualDensity.compact,
              onPressed: async.isLoading
                  ? null
                  : () =>
                      unawaited(ref.read(guidingRmsProvider.notifier).refresh()),
              icon: async.isLoading
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.refresh),
            ),
          ),
        ],
      ),
    );
  }

  // The most-recent _maxPlotted samples (the full list when shorter).
  static List<GuidingRmsPoint> _plotted(GuidingRmsSeries? series) {
    if (series == null) return const [];
    final s = series.samples;
    return s.length > _maxPlotted ? s.sublist(s.length - _maxPlotted) : s;
  }

  Widget _body(
    BuildContext context,
    WidgetRef ref,
    AsyncValue<GuidingRmsSeries?> async,
    GuidingRmsSeries? series,
    List<GuidingRmsPoint> shown,
  ) {
    if (async.isLoading && series == null) {
      return const _Hint('Loading guiding RMS…');
    }
    if (async.hasError && series == null) {
      return _Hint(
        'Could not load guiding RMS.',
        onRetry: () => unawaited(ref.read(guidingRmsProvider.notifier).refresh()),
      );
    }
    if (series == null) {
      return const _Hint('Connect to a server to see guiding RMS.');
    }
    if (series.isEmpty) {
      return const _Hint('No guiding data yet — RMS appears here once guided frames are captured.');
    }

    final spots = <FlSpot>[];
    var observedMax = 0.0;
    for (var i = 0; i < shown.length; i++) {
      final rms = shown[i].rmsArcsec;
      spots.add(FlSpot(i.toDouble(), rms));
      if (rms > observedMax) observedMax = rms;
    }
    final yMax = (observedMax + 0.2).clamp(_yFloor, _yCeiling).toDouble();
    // `shown` is non-empty here (the isEmpty guard above), so length - 1 >= 0.
    final lastIdx = shown.length - 1;

    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 16, 8),
      child: LineChart(
        LineChartData(
          minX: 0,
          maxX: lastIdx == 0 ? 1 : lastIdx.toDouble(),
          minY: 0,
          maxY: yMax,
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
              axisNameWidget: const Text('Capture time'),
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 28,
                // Label a handful of points by date to avoid crowding a dense
                // per-frame series.
                interval: _labelInterval(shown.length),
                getTitlesWidget: (v, _) {
                  final i = v.toInt();
                  if (i < 0 || i >= shown.length) {
                    return const SizedBox.shrink();
                  }
                  final ts = shown[i].timestamp;
                  if (ts == null) return const SizedBox.shrink();
                  final d = ts.toLocal();
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
              spots: spots,
              isCurved: false,
              color: AraColors.selectionBg,
              barWidth: 2,
              // A dense series reads better as a line; show dots only when sparse.
              dotData: FlDotData(show: shown.length <= 30),
            ),
          ],
        ),
      ),
    );
  }

  // Aim for ~6 x-axis labels regardless of series length.
  static double _labelInterval(int count) {
    if (count <= 6) return 1;
    return (count / 6).ceilToDouble();
  }
}

class _Hint extends StatelessWidget {
  const _Hint(this.message, {this.onRetry});

  final String message;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 24),
            child: Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
            ),
          ),
          if (onRetry != null)
            TextButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}
