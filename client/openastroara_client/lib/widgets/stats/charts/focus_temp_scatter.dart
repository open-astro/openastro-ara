import 'dart:async';

import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/stats/focus_temp.dart';
import '../../../state/stats/focus_temp_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';
import 'chart_stale_chip.dart';

/// §50.4 Focus & Temperature scatter — each captured frame that recorded a
/// focuser position is a point at (sensor temp, focus position), read from the
/// live daemon (`GET /api/v1/stats/focus-temp`). The Pearson r² in the subtitle
/// tells the user how strongly the two track — useful for §37.4 temp-comp setup.
/// Replaces the Phase-12g demo that derived points from the in-memory library.
class FocusTempScatterChart extends ConsumerStatefulWidget {
  const FocusTempScatterChart({super.key});

  @override
  ConsumerState<FocusTempScatterChart> createState() =>
      _FocusTempScatterChartState();
}

class _FocusTempScatterChartState extends ConsumerState<FocusTempScatterChart> {
  // Plot at most the most-recent N samples so a very long capture history can't
  // hand fl_chart a huge spot list. r² in the subtitle stays full-history
  // (computed server-side over every sample).
  static const int _maxPlotted = 1000;

  // Local spinner/banner flags so the scatter stays on screen during and after
  // a manual refresh (refresh() holds the old data). See StatsRefreshMixin.
  bool _refreshing = false;
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(focusTempProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(focusTempProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(focusTempProvider);
    final series = async.asData?.value;
    final spinning = _refreshing || (async.isLoading && series == null);

    final total = series?.samples.length ?? 0;
    final shown = _plotted(series);
    final summary = series == null || series.isEmpty
        ? null
        : [
            if (series.correlationR2 != null)
              'r² ${series.correlationR2!.toStringAsFixed(2)}',
            if (total > _maxPlotted) 'latest $_maxPlotted of $total',
          ].join(' · ');

    return ChartCard(
      title: 'Focus & Temperature',
      subtitle: summary == null || summary.isEmpty
          ? 'Focus position vs sensor temperature — slope = temp-comp coefficient.'
          : 'Focus position vs sensor temperature — $summary.',
      child: Stack(
        children: [
          _body(async, series, shown),
          if (_staleError && series != null)
            const Positioned(
              top: 0,
              left: 4,
              child: ChartStaleChip(
                tooltip: 'Couldn’t refresh — showing the last loaded scatter.',
              ),
            ),
          Positioned(
            top: 0,
            right: 4,
            child: IconButton(
              tooltip: 'Refresh',
              iconSize: 18,
              visualDensity: VisualDensity.compact,
              onPressed: spinning ? null : () => unawaited(_refresh()),
              icon: spinning
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
  static List<FocusTempPoint> _plotted(FocusTempSeries? series) {
    if (series == null) return const [];
    final s = series.samples;
    return s.length > _maxPlotted ? s.sublist(s.length - _maxPlotted) : s;
  }

  Widget _body(
    AsyncValue<FocusTempSeries?> async,
    FocusTempSeries? series,
    List<FocusTempPoint> shown,
  ) {
    if (async.isLoading && series == null) {
      return const _Hint('Loading focus & temperature…');
    }
    if (async.hasError && series == null) {
      return _Hint(
        'Could not load focus & temperature.',
        onRetry: () => unawaited(_refresh()),
      );
    }
    if (series == null) {
      return const _Hint('Connect to a server to see focus & temperature.');
    }
    if (series.isEmpty) {
      return const _Hint(
          'No frames with a focuser position yet — this chart fills in as positioned frames are captured.');
    }

    final spots = [
      for (final p in shown)
        ScatterSpot(
          p.temperatureC,
          p.focuserPosition.toDouble(),
          dotPainter: FlDotCirclePainter(
            color: AraColors.selectionBg.withValues(alpha: 0.7),
            radius: 4,
          ),
        ),
    ];

    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 16, 8),
      child: ScatterChart(
        ScatterChartData(
          scatterSpots: spots,
          minX: _minX(spots) - 1,
          maxX: _maxX(spots) + 1,
          minY: _minY(spots) - 20,
          maxY: _maxY(spots) + 20,
          borderData: FlBorderData(
            show: true,
            border: Border.all(color: AraColors.border),
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
            leftTitles: const AxisTitles(
              axisNameWidget: Text('Focus steps'),
              sideTitles: SideTitles(showTitles: true, reservedSize: 48),
            ),
            bottomTitles: const AxisTitles(
              axisNameWidget: Text('Sensor temp (°C)'),
              sideTitles: SideTitles(showTitles: true, reservedSize: 28),
            ),
            topTitles: const AxisTitles(),
            rightTitles: const AxisTitles(),
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
