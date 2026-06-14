import 'dart:async';

import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/stats/frame_quality.dart';
import '../../../state/stats/frame_quality_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.10 frame-quality composite — a histogram of composite quality scores
/// (0–1, higher = better) over the catalog's scored frames, from the live
/// daemon (`GET /api/v1/stats/frame-quality`). Replaces the Phase 12g.2 demo
/// that approximated quality with per-session average HFR.
class FrameQualityChart extends ConsumerWidget {
  const FrameQualityChart({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(frameQualityProvider);
    final dist = async.asData?.value;

    final mean = dist == null || dist.isEmpty
        ? null
        : 'mean ${dist.meanScore.toStringAsFixed(2)}';
    return ChartCard(
      title: 'Frame Quality (composite score distribution)',
      subtitle: mean == null
          ? 'Higher = better. Composite of HFR, star count, and background.'
          : 'Higher = better — $mean. Composite of HFR, star count, and background.',
      child: Stack(
        children: [
          _body(context, ref, async, dist),
          Positioned(
            top: 0,
            right: 4,
            child: IconButton(
              tooltip: 'Refresh',
              iconSize: 18,
              visualDensity: VisualDensity.compact,
              onPressed: async.isLoading
                  ? null
                  : () => unawaited(
                      ref.read(frameQualityProvider.notifier).refresh()),
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

  Widget _body(
    BuildContext context,
    WidgetRef ref,
    AsyncValue<FrameQualityDistribution?> async,
    FrameQualityDistribution? dist,
  ) {
    if (async.isLoading && dist == null) {
      return const _Hint('Loading frame quality…');
    }
    if (async.hasError && dist == null) {
      return _Hint(
        'Could not load frame quality.',
        onRetry: () =>
            unawaited(ref.read(frameQualityProvider.notifier).refresh()),
      );
    }
    if (dist == null) {
      return const _Hint('Connect to a server to see frame quality.');
    }
    if (dist.isEmpty) {
      return const _Hint('No scored frames yet — quality appears here once captures are scored.');
    }

    final bars = <BarChartGroupData>[];
    var maxCount = 0;
    for (var i = 0; i < dist.buckets.length; i++) {
      final b = dist.buckets[i];
      if (b.count > maxCount) maxCount = b.count;
      final mid = (b.rangeLow + b.rangeHigh) / 2;
      bars.add(BarChartGroupData(x: i, barRods: [
        BarChartRodData(
          toY: b.count.toDouble(),
          // Higher score = better, so green at the top end, red at the bottom.
          color: mid >= 0.7
              ? AraColors.selectionBg
              : mid >= 0.4
                  ? AraColors.accentBusy
                  : Colors.redAccent,
          width: 14,
          borderRadius: const BorderRadius.vertical(top: Radius.circular(4)),
        ),
      ]));
    }
    // ~10% headroom so the tallest bar doesn't touch the chart border; ceil
    // keeps at least 1 frame of clearance for small counts. The `maxCount == 0`
    // floor is defensive — the `dist.isEmpty` early-return above already
    // prevents an all-zero distribution from reaching here, but it keeps
    // `BarChart(maxY:)` valid if that guard is ever refactored.
    final yMax = maxCount == 0 ? 1.0 : (maxCount * 1.1).ceilToDouble();

    return Padding(
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
              axisNameWidget: Text('Frames'),
              sideTitles: SideTitles(showTitles: true, reservedSize: 36),
            ),
            bottomTitles: AxisTitles(
              axisNameWidget: const Text('Composite score'),
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 24,
                getTitlesWidget: (v, _) {
                  final i = v.toInt();
                  if (i < 0 || i >= dist.buckets.length) {
                    return const SizedBox.shrink();
                  }
                  // Label every other bucket by its lower edge to avoid crowding.
                  if (i.isOdd) return const SizedBox.shrink();
                  return Padding(
                    padding: const EdgeInsets.only(top: 4),
                    child: Text(
                      dist.buckets[i].rangeLow.toStringAsFixed(1),
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
    );
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
