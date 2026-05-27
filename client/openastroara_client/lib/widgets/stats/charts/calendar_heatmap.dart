import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/library/library_state.dart';
import '../../../theme/ara_colors.dart';
import 'chart_card.dart';

/// §50.6 GitHub-style calendar heatmap — one cell per day in a rolling
/// 49-day window. Cell shade scales with integration minutes (lights only)
/// for that day. fl_chart has no native heatmap, so this is a CustomPaint
/// over a Wrap of square tiles.
class CalendarHeatmap extends ConsumerWidget {
  const CalendarHeatmap({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(librarySessionsProvider);
    final today = DateTime(2026, 5, 27); // matches demo "now"; replaced
    // by real wall-clock in Phase 12g.3.
    const daysShown = 49;
    final start = today.subtract(const Duration(days: daysShown - 1));

    // Sum integration minutes per date.
    final perDay = <String, int>{};
    for (final s in sessions) {
      final mins = s.totalIntegration.inMinutes;
      final key = _dayKey(s.date);
      perDay[key] = (perDay[key] ?? 0) + mins;
    }
    final maxMins = perDay.values.isEmpty
        ? 0
        : perDay.values.reduce((a, b) => a > b ? a : b);

    return ChartCard(
      title: 'Calendar Heatmap',
      subtitle:
          'Integration minutes per night, last $daysShown days (peak: ${maxMins}m)',
      height: 200,
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Wrap(
                spacing: 4,
                runSpacing: 4,
                children: [
                  for (var i = 0; i < daysShown; i++)
                    _Cell(
                      date: start.add(Duration(days: i)),
                      minutes: perDay[_dayKey(start.add(Duration(days: i)))] ?? 0,
                      maxMinutes: maxMins,
                    ),
                ],
              ),
            ),
            const SizedBox(height: 6),
            Row(children: [
              Text(
                'Less',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textDisabled,
                    ),
              ),
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
              Text(
                'More',
                style: Theme.of(context).textTheme.labelSmall?.copyWith(
                      color: AraColors.textDisabled,
                    ),
              ),
            ]),
          ],
        ),
      ),
    );
  }

  static String _dayKey(DateTime d) =>
      '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  static Color _shade(double t) {
    if (t <= 0) return AraColors.bgPanel;
    final base = AraColors.selectionBg;
    return Color.lerp(AraColors.border, base, t.clamp(0.05, 1.0)) ?? base;
  }
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
          color: CalendarHeatmap._shade(t),
          borderRadius: BorderRadius.circular(3),
          border: Border.all(color: AraColors.border, width: 0.5),
        ),
      ),
    );
  }
}
