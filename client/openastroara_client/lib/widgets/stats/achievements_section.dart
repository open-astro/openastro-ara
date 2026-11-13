import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/achievements.dart';
import '../../state/stats/achievements_state.dart';
import '../../theme/ara_colors.dart';
import '../../l10n/l10n.dart';
import 'stat_tile.dart';
import '../adaptive_controls.dart';
import 'stats_pull_refresh.dart';

/// §50.19 Achievements section on the Stats dashboard. Unlike the demo-data
/// Overview/Targets sections, this is wired to the live daemon
/// (`GET /api/v1/stats/achievements`) — cumulative records + imaging-night
/// streaks + milestone badges. Degrades gracefully when no server is connected.
class AchievementsSection extends ConsumerStatefulWidget {
  const AchievementsSection({super.key});

  @override
  ConsumerState<AchievementsSection> createState() =>
      _AchievementsSectionState();
}

class _AchievementsSectionState extends ConsumerState<AchievementsSection>
    with StatsPullRefresh<AchievementsSection> {
  @override
  Future<void> pullRefresh() => _refresh();

  // Drives the header spinner during a manual refresh. Kept local (not derived
  // from the provider's isLoading) so the records stay on screen while it spins
  // — refresh() deliberately holds the old data instead of dropping to loading.
  bool _refreshing = false;

  // Set when a manual refresh fails while records are still shown — refresh()
  // leaves state untouched on failure (records stay), so the failure can't be
  // read off the provider's AsyncValue; this local flag drives the stale banner.
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(achievementsProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    // A fresh load from the provider (e.g. a server switch re-running build)
    // clears a stale flag left by an earlier failed refresh.
    ref.listen(achievementsProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(achievementsProvider);
    final data = async.asData?.value;
    // First load (no data yet) spins via the provider; a manual refresh spins
    // via the local flag while the previous records remain visible.
    final spinning = _refreshing || (async.isLoading && data == null);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('Achievements',
                  style: Theme.of(context).textTheme.titleMedium),
            ),
            // The page's pull down refreshes; this shows it running.
            if (spinning)
              const Padding(
                padding: EdgeInsets.all(8),
                child: SizedBox(width: 16, height: 16, child: AdaptiveProgressIndicator(strokeWidth: 2)),
              ),
          ],
        ),
        const SizedBox(height: 8),
        _body(context, async, data),
      ],
    );
  }

  Widget _body(
    BuildContext context,
    AsyncValue<StatsAchievements?> async,
    StatsAchievements? data,
  ) {
    if (async.isLoading && data == null) {
      return const _Hint('Loading achievements…');
    }
    if (async.hasError && data == null) {
      return Row(
        children: [
          const Expanded(child: _Hint('Could not load achievements.')),
          TextButton(
            onPressed: _refreshing ? null : () => unawaited(_refresh()),
            child: const Text('Retry'),
          ),
        ],
      );
    }
    if (data == null) {
      return const _Hint('Connect to a server to see your imaging achievements.');
    }
    if (data.isEmpty) {
      return const _Hint(
          'No light frames yet — your records and badges appear here once you start imaging.');
    }
    // Records are held through a failed refresh (so they don't blank), but a
    // silent stale view would read as live — surface the failure inline above
    // the still-shown tiles.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (_staleError) const _StaleBanner(),
        _Achievements(data: data),
      ],
    );
  }
}

/// Shown above the records when the last refresh failed but cached data is still
/// on screen — the tiles look live, so the staleness has to be made explicit.
class _StaleBanner extends StatelessWidget {
  const _StaleBanner();

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(bottom: 12),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(color: AraColors.accentError),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Row(
        children: const [
          Icon(Icons.warning_amber, size: 16, color: AraColors.accentError),
          SizedBox(width: 8),
          Expanded(
            child: Text(
              'Couldn’t refresh — showing the last loaded data.',
              style: TextStyle(fontSize: 12, color: AraColors.textSecondary),
            ),
          ),
        ],
      ),
    );
  }
}

class _Achievements extends StatelessWidget {
  const _Achievements({required this.data});

  final StatsAchievements data;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // The records as the dashboard's list, without the totals Overview
        // already shows; Unique targets here counts named frames only.
        LayoutBuilder(
          builder: (context, box) => StatKpiGrid(
            columns: box.maxWidth >= 720 ? 3 : 2,
            children: [
              StatKpi(
                value: '${data.currentStreakNights}',
                label: 'Current streak',
                subtitle: 'nights in a row',
              ),
              StatKpi(
                value: '${data.longestStreakNights}',
                label: 'Longest streak',
                subtitle: 'nights',
              ),
              StatKpi(
                value: '${data.totalNightsImaged}',
                label: 'Nights imaged',
              ),
              StatKpi(
                value: _hours(data.longestNightHours),
                label: 'Longest night',
              ),
              StatKpi(
                value: '${data.uniqueTargetsImaged}',
                label: context.l10n.statsNamedTargets,
              ),
              StatKpi(
                value: _firstLight(data.firstLightUtc),
                label: 'First light',
              ),
            ],
          ),
        ),
        if (data.milestones.isNotEmpty) ...[
          const SizedBox(height: 16),
          Text('Badges',
              style: Theme.of(context).textTheme.titleSmall?.copyWith(
                    color: AraColors.textSecondary,
                  )),
          const SizedBox(height: 8),
          // Medals on a shelf that scrolls sideways, earned ones first.
          SizedBox(
            height: 132,
            child: ListView(
              scrollDirection: Axis.horizontal,
              children: [
                for (final m in [
                  ...data.milestones.where((m) => m.achieved),
                  ...data.milestones.where((m) => !m.achieved),
                ])
                  _MilestoneBadge(milestone: m),
              ],
            ),
          ),
        ],
        const Padding(
          padding: EdgeInsets.only(top: 12),
          child: Text(
            'Nights use the UTC calendar day, so a session that crosses midnight UTC '
            'counts as two. Target badges only count frames that recorded a target name.',
            style: TextStyle(fontSize: 11, color: AraColors.textDisabled),
          ),
        ),
      ],
    );
  }

  // Whole-hour metrics read as "12h"; fractional ones add minutes ("12h 30m").
  // Guard non-finite / negative server values: a negative would drive the modulo
  // into a nonsense "0h 54m", and `(infinity * 60).round()` throws in Dart.
  static String _hours(double hours) {
    if (!hours.isFinite || hours < 0) return '—';
    final totalMinutes = (hours * 60).round();
    final h = totalMinutes ~/ 60;
    final m = totalMinutes % 60;
    return m == 0 ? '${h}h' : '${h}h ${m.toString().padLeft(2, '0')}m';
  }

  static String _firstLight(DateTime? utc) {
    if (utc == null) return '—';
    final d = utc.toLocal();
    const months = [
      'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
      'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
    ];
    return '${months[d.month - 1]} ${d.year}';
  }
}

class _MilestoneBadge extends StatelessWidget {
  const _MilestoneBadge({required this.milestone});

  final StatsMilestone milestone;

  @override
  Widget build(BuildContext context) {
    final achieved = milestone.achieved;
    final accent = achieved ? AraColors.accentConnected : AraColors.accentInfo;
    // A medal: the progress as a ring round the trophy or the lock, the
    // title under it and how far, or Earned, under that. One size for all.
    return Tooltip(
      message: milestone.description,
      child: SizedBox(
        key: Key('stats-medal-${milestone.id}'),
        width: 104,
        child: Column(
          spacing: 4,
          children: [
            SizedBox(
              width: 56,
              height: 56,
              child: Stack(
                fit: StackFit.expand,
                children: [
                  CircularProgressIndicator(
                    value: achieved ? 1 : milestone.progress,
                    strokeWidth: 4,
                    backgroundColor: AraColors.bgInput,
                    valueColor: AlwaysStoppedAnimation(accent),
                  ),
                  Icon(
                    achieved ? Icons.emoji_events : Icons.lock_outline,
                    size: 24,
                    color: achieved
                        ? AraColors.accentConnected
                        : AraColors.textDisabled,
                  ),
                ],
              ),
            ),
            Text(
              milestone.title,
              maxLines: 2,
              textAlign: TextAlign.center,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.labelMedium?.copyWith(
                    color: achieved
                        ? AraColors.textPrimary
                        : AraColors.textSecondary,
                  ),
            ),
            Text(
              achieved
                  ? context.l10n.statsBadgeEarned
                  : '${_trim(milestone.current)} / ${_trim(milestone.threshold)}', // l10n-ok
              style: TextStyle(
                  fontSize: 11,
                  color: achieved
                      ? AraColors.accentConnected
                      : AraColors.textDisabled),
            ),
          ],
        ),
      ),
    );
  }

  // Drop a trailing ".0" so "10.0" reads as "10", but keep a real fraction.
  // Epsilon compare rather than exact float equality so 10.0000000001 still
  // reads as "10".
  static String _trim(double v) => (v - v.roundToDouble()).abs() < 1e-9
      ? v.round().toString()
      : v.toStringAsFixed(1);
}

class _Hint extends StatelessWidget {
  const _Hint(this.message);

  final String message;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Text(
        message,
        style: const TextStyle(color: AraColors.textSecondary),
      ),
    );
  }
}
