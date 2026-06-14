import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/stats_target.dart';
import '../../state/stats/stats_targets_state.dart';
import '../../theme/ara_colors.dart';
import 'stats_format.dart';

/// §50 Stats dashboard Targets section — per-target imaging rollups from the
/// live daemon (`GET /api/v1/stats/targets`), replacing the Phase 12g.1 demo
/// rollups derived from the in-memory library.
class TargetsSection extends ConsumerStatefulWidget {
  const TargetsSection({super.key});

  @override
  ConsumerState<TargetsSection> createState() => _TargetsSectionState();
}

class _TargetsSectionState extends ConsumerState<TargetsSection> {
  // Local spinner/banner flags so the list stays on screen during and after a
  // manual refresh (refresh() holds the old data instead of dropping to
  // loading). See StatsRefreshMixin.
  bool _refreshing = false;
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(statsTargetsProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(statsTargetsProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(statsTargetsProvider);
    final targets = async.asData?.value;
    final spinning = _refreshing || (async.isLoading && targets == null);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('Targets',
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
        if (_staleError && targets != null) const _StaleBanner(),
        _body(async, targets),
      ],
    );
  }

  Widget _body(
    AsyncValue<List<StatsTarget>?> async,
    List<StatsTarget>? targets,
  ) {
    if (async.isLoading && targets == null) {
      return const _Hint('Loading targets…');
    }
    if (async.hasError && targets == null) {
      return Row(
        children: [
          const Expanded(child: _Hint('Could not load targets.')),
          TextButton(
            onPressed: () => unawaited(_refresh()),
            child: const Text('Retry'),
          ),
        ],
      );
    }
    if (targets == null) {
      return const _Hint('Connect to a server to see your imaged targets.');
    }
    if (targets.isEmpty) {
      return const _Hint('No targets imaged yet — they appear here once you capture light frames.');
    }
    return Column(
      children: [for (final t in targets) _TargetTile(target: t)],
    );
  }
}

/// Shown above the stale list when a manual refresh failed but data remains.
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
            'Couldn’t refresh — showing the last loaded targets.',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: AraColors.accentBusy,
                ),
          ),
        ],
      ),
    );
  }
}

class _TargetTile extends StatelessWidget {
  const _TargetTile({required this.target});

  final StatsTarget target;

  @override
  Widget build(BuildContext context) {
    final frames =
        '${target.frameCount} frame${target.frameCount == 1 ? '' : 's'}';
    final integration =
        '${formatIntegrationHours(target.integrationHours)} integration';
    final score = target.compositeQualityScore;
    final lastImaged = target.lastImagedUtc;
    final subtitle = [
      frames,
      integration,
      if (score != null) 'Quality ${score.toStringAsFixed(2)}',
      if (lastImaged != null) 'Last ${formatStatsDate(lastImaged)}',
    ].join(' · ');

    return Card(
      margin: const EdgeInsets.only(bottom: 8),
      child: ListTile(
        leading: const Icon(Icons.gps_fixed, color: AraColors.textSecondary),
        title: Text(target.targetName),
        subtitle: Text(subtitle),
        trailing:
            const Icon(Icons.chevron_right, color: AraColors.textDisabled),
        onTap: null, // per-target detail drill-down lands in a later slice
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
      child: Text(message,
          style: const TextStyle(color: AraColors.textSecondary)),
    );
  }
}
