import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/stats_overview.dart';
import '../../state/stats/stats_overview_state.dart';
import '../../theme/ara_colors.dart';
import 'responsive_tile_grid.dart';
import 'stat_tile.dart';
import 'stats_format.dart';

/// §50 Stats Overview section — headline catalog totals from the live daemon
/// (`GET /api/v1/stats/overview`), replacing the Phase 12g.1 demo-data tiles.
class OverviewSection extends ConsumerStatefulWidget {
  const OverviewSection({super.key});

  @override
  ConsumerState<OverviewSection> createState() => _OverviewSectionState();
}

class _OverviewSectionState extends ConsumerState<OverviewSection> {
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
            IconButton(
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
    return ResponsiveTileGrid(
      children: [
        StatTile(
          icon: Icons.list_alt,
          value: '${data.totalSessions}',
          label: 'Sessions',
        ),
        StatTile(
          icon: Icons.image_outlined,
          value: '${data.totalFrames}',
          label: 'Frames',
        ),
        StatTile(
          icon: Icons.wb_incandescent_outlined,
          value: '${data.totalLightFrames}',
          label: 'Light frames',
        ),
        StatTile(
          icon: Icons.timelapse,
          value: formatIntegrationHours(data.totalIntegrationHours),
          label: 'Total integration',
        ),
        StatTile(
          icon: Icons.gps_fixed,
          value: '${data.uniqueTargetsImaged}',
          label: 'Unique targets',
        ),
        StatTile(
          icon: Icons.event_available,
          value: formatStatsDate(data.lastImageUtc),
          label: 'Last imaged',
        ),
      ],
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
