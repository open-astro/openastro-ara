import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/stats/calendar_stats.dart';
import '../../../state/stats/calendar_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';
import 'chart_stale_chip.dart';

/// §50.6 GitHub-style calendar heatmap — one cell per day in a rolling
/// [_daysShown]-day window, shaded by that night's integration minutes, from
/// the live daemon (`GET /api/v1/stats/calendar`). Replaces the Phase-12g demo
/// that summed integration from the in-memory library. fl_chart has no native
/// heatmap, so this is a Wrap of square tiles.
class CalendarHeatmap extends ConsumerStatefulWidget {
  const CalendarHeatmap({super.key});

  @override
  ConsumerState<CalendarHeatmap> createState() => _CalendarHeatmapState();
}

class _CalendarHeatmapState extends ConsumerState<CalendarHeatmap> {
  static const int _daysShown = 49;

  // Local spinner/banner flags so the heatmap stays on screen during and after
  // a manual refresh (refresh() holds the old data). See StatsRefreshMixin.
  bool _refreshing = false;
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(calendarProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(calendarProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(calendarProvider);
    final stats = async.asData?.value;
    final spinning = _refreshing || (async.isLoading && stats == null);

    // Real wall-clock — the rolling window advances as time moves.
    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final start = today.subtract(const Duration(days: _daysShown - 1));

    // Integration minutes per day within the rendered window. Days outside it
    // (the server may return a slightly wider range) are dropped so they don't
    // inflate `maxMins` and wash out contrast. `minutesByDay` already has one
    // entry per day, so this is a straight copy of the in-window entries.
    final perDay = <String, int>{};
    if (stats != null) {
      stats.minutesByDay.forEach((key, mins) {
        final d = DateTime.tryParse(key);
        if (d == null || d.isBefore(start) || d.isAfter(today)) return;
        perDay[key] = mins;
      });
    }
    final maxMins =
        perDay.values.isEmpty ? 0 : perDay.values.reduce((a, b) => a > b ? a : b);

    return ChartCard(
      title: 'Calendar Heatmap',
      subtitle:
          'Integration minutes per night, last $_daysShown days (peak: ${maxMins}m)',
      height: 200,
      child: Stack(
        children: [
          _body(async, stats, start, perDay, maxMins),
          if (_staleError && stats != null)
            const Positioned(
              top: 0,
              left: 4,
              child: ChartStaleChip(
                tooltip: 'Couldn’t refresh — showing the last loaded calendar.',
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

  Widget _body(
    AsyncValue<CalendarStats?> async,
    CalendarStats? stats,
    DateTime start,
    Map<String, int> perDay,
    int maxMins,
  ) {
    if (async.isLoading && stats == null) {
      return const _Hint('Loading calendar…');
    }
    if (async.hasError && stats == null) {
      return _Hint(
        'Could not load the calendar.',
        onRetry: () => unawaited(_refresh()),
      );
    }
    if (stats == null) {
      return const _Hint('Connect to a server to see your capture calendar.');
    }

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(
            child: Wrap(
              spacing: 4,
              runSpacing: 4,
              children: [
                for (var i = 0; i < _daysShown; i++)
                  _Cell(
                    date: start.add(Duration(days: i)),
                    minutes:
                        perDay[CalendarStats.dayKey(start.add(Duration(days: i)))] ?? 0,
                    maxMinutes: maxMins,
                  ),
              ],
            ),
          ),
          const SizedBox(height: 6),
          Row(children: [
            Text('Less',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textDisabled,
                    )),
            const SizedBox(width: 6),
            for (final s in [0.15, 0.3, 0.6, 0.85, 1.0])
              Container(
                margin: const EdgeInsets.only(right: 3),
                width: 12,
                height: 12,
                decoration: BoxDecoration(
                  color: _shade(s),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            const SizedBox(width: 4),
            Text('More',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textDisabled,
                    )),
          ]),
        ],
      ),
    );
  }

}

// Cell shade scales with that day's integration vs the window peak.
Color _shade(double t) {
  if (t <= 0) return AraColors.bgPanel;
  final base = AraColors.selectionBg;
  return Color.lerp(AraColors.border, base, t.clamp(0.05, 1.0)) ?? base;
}

class _Cell extends StatelessWidget {
  final DateTime date;
  final int minutes;
  final int maxMinutes;
  const _Cell({
    required this.date,
    required this.minutes,
    required this.maxMinutes,
  });

  @override
  Widget build(BuildContext context) {
    final t = maxMinutes == 0 ? 0.0 : minutes / maxMinutes;
    return Tooltip(
      message:
          '${date.year}-${date.month}-${date.day}: ${minutes == 0 ? 'no captures' : '$minutes min'}',
      child: Container(
        width: 16,
        height: 16,
        decoration: BoxDecoration(
          color: _shade(t),
          borderRadius: BorderRadius.circular(3),
          border: Border.all(color: AraColors.border, width: 0.5),
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
