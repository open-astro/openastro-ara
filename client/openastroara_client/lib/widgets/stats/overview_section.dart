import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/stats_overview.dart';
import '../../state/stats/stats_overview_state.dart';
import '../../theme/ara_colors.dart';
import 'stat_tile.dart';
import 'stats_format.dart';

/// §50 Stats Overview section — headline catalog totals from the live daemon
/// (`GET /api/v1/stats/overview`), replacing the Phase 12g.1 demo-data tiles.
class OverviewSection extends ConsumerWidget {
  const OverviewSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(statsOverviewProvider);
    final data = async.asData?.value;

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
              onPressed: async.isLoading
                  ? null
                  : () => unawaited(
                      ref.read(statsOverviewProvider.notifier).refresh()),
              icon: async.isLoading
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.refresh),
            ),
          ],
        ),
        const SizedBox(height: 8),
        _body(context, ref, async, data),
      ],
    );
  }

  Widget _body(
    BuildContext context,
    WidgetRef ref,
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
            onPressed: () =>
                unawaited(ref.read(statsOverviewProvider.notifier).refresh()),
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
    return Wrap(
      spacing: 12,
      runSpacing: 12,
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
