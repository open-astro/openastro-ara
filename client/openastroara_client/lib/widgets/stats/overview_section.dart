import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/stats_overview.dart';
import '../../state/stats/stats_overview_state.dart';
import '../../theme/ara_colors.dart';
import '../../l10n/l10n.dart';
import 'responsive_tile_grid.dart';
import 'stat_tile.dart';
import 'stats_format.dart';
import '../adaptive_controls.dart';
import 'stats_pull_refresh.dart';

/// §50 Stats Overview section — headline catalog totals from the live daemon
/// (`GET /api/v1/stats/overview`), replacing the Phase 12g.1 demo-data tiles.
class OverviewSection extends ConsumerStatefulWidget {
  const OverviewSection({super.key, this.dashboard = false});

  /// Simple Mode's dashboard (headline and list); else the tile grid.
  final bool dashboard;

  @override
  ConsumerState<OverviewSection> createState() => _OverviewSectionState();
}

class _OverviewSectionState extends ConsumerState<OverviewSection>
    with StatsPullRefresh<OverviewSection> {
  @override
  Future<void> pullRefresh() => _refresh();

  // Drives the header spinner during a manual refresh. Kept local (not derived
  // from the provider's isLoading) so the tiles stay on screen while it spins —
  // refresh() deliberately holds the old data instead of dropping to loading.
  bool _refreshing = false;

  // Set when a manual refresh fails while tiles are still shown — refresh()
  // leaves state untouched on failure, so the failure can't be read off the
  // provider's AsyncValue; this local flag drives the stale banner.
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(statsOverviewProvider.notifier).refresh();
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
    ref.listen(statsOverviewProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(statsOverviewProvider);
    final data = async.asData?.value;
    // First load (no data yet) spins via the provider; a manual refresh spins
    // via the local flag while the previous tiles remain visible.
    final spinning = _refreshing || (async.isLoading && data == null);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('Overview',
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
        if (_staleError && data != null) const _StaleBanner(),
        _body(context, async, data),
      ],
    );
  }

  Widget _body(
    BuildContext context,
    AsyncValue<StatsOverview?> async,
    StatsOverview? data,
  ) {
    if (async.isLoading && data == null) {
      return const _Hint('Loading overview…');
    }
    if (async.hasError && data == null) {
      return Row(
        children: [
          const Expanded(child: _Hint('Could not load the overview.')),
          TextButton(
            onPressed: () => unawaited(_refresh()),
            child: const Text('Retry'),
          ),
        ],
      );
    }
    if (data == null) {
      return const _Hint('Connect to a server to see your imaging overview.');
    }
    if (data.isEmpty) {
      return const _Hint('No frames captured yet — your totals appear here once you start imaging.');
    }
    // Each total once: (icon, value, label). Advanced Mode keeps its tile
    // grid; Simple Mode's dashboard (mock `design/preview/stats-layouts-mock.html`
    // B) makes the total integration the headline and lists the others beside
    // it, under it on a narrow window.
    const integration = 3;
    final totals = [
      (Icons.list_alt, '${data.totalSessions}', 'Sessions'),
      (Icons.image_outlined, '${data.totalFrames}', 'Frames'),
      (Icons.wb_incandescent_outlined, '${data.totalLightFrames}', 'Light frames'),
      (Icons.timelapse, formatIntegrationHours(data.totalIntegrationHours), 'Total integration'),
      (Icons.gps_fixed, '${data.uniqueTargetsImaged}', 'Unique targets'),
      (Icons.event_available, formatStatsDate(data.lastImageUtc), 'Last imaged'),
    ];
    if (!widget.dashboard) {
      return ResponsiveTileGrid(
        children: [
          for (final (icon, value, label) in totals)
            StatTile(icon: icon, value: value, label: label),
        ],
      );
    }
    final hero = _Hero(data, label: totals[integration].$3, value: totals[integration].$2);
    final kpis = StatKpiGrid(
      children: [
        for (final (i, (_, value, label)) in totals.indexed)
          if (i != integration) StatKpi(value: value, label: label),
      ],
    );
    return LayoutBuilder(
      builder: (context, box) => box.maxWidth >= 720
          ? IntrinsicHeight(
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                spacing: 12,
                children: [
                  Expanded(flex: 6, child: hero),
                  Expanded(flex: 5, child: kpis),
                ],
              ),
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 12,
              children: [hero, kpis],
            ),
    );
  }
}

/// The dashboard's headline: the total integration, big, with what it is
/// made of under it.
class _Hero extends StatelessWidget {
  const _Hero(this.data, {required this.label, required this.value});

  final StatsOverview data;
  final String label, value;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context).textTheme;
    return Container(
      key: const Key('stats-hero'),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AraColors.bgPanel,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        mainAxisAlignment: MainAxisAlignment.center,
        spacing: 6,
        children: [
          Text(
            label,
            style: theme.labelMedium?.copyWith(color: AraColors.textSecondary),
          ),
          Text(
            value,
            style: theme.displayMedium?.copyWith(
              color: AraColors.textPrimary,
              fontWeight: FontWeight.w700,
            ),
          ),
          Text(
            context.l10n.statsIntegrationCaption(
              data.totalLightFrames,
              data.totalSessions,
            ),
            style: theme.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
        ],
      ),
    );
  }
}

/// Shown above stale tiles when a manual refresh failed but prior data remains.
class _StaleBanner extends StatelessWidget {
  const _StaleBanner();

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        children: [
          const Icon(Icons.sync_problem, size: 14, color: AraColors.accentBusy),
          const SizedBox(width: 6),
          Text(
            'Couldn’t refresh — showing the last loaded totals.',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: AraColors.accentBusy,
                ),
          ),
        ],
      ),
    );
  }
}

class _Hint extends StatelessWidget {
  const _Hint(this.message);

  final String message;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Text(message, style: const TextStyle(color: AraColors.textSecondary)),
    );
  }
}
