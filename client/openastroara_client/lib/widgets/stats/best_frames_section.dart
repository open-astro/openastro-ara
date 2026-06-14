import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/stats/best_frame.dart';
import '../../state/stats/best_frames_state.dart';
import '../../theme/ara_colors.dart';
import 'stats_format.dart';

/// §50 Stats dashboard Best Frames section — the catalog's top-ranked frames
/// (best composite quality first) from the live daemon
/// (`GET /api/v1/stats/best-frames`), replacing the Phase 12g.1 demo list.
class BestFramesSection extends ConsumerStatefulWidget {
  const BestFramesSection({super.key});

  @override
  ConsumerState<BestFramesSection> createState() => _BestFramesSectionState();
}

class _BestFramesSectionState extends ConsumerState<BestFramesSection> {
  // Local spinner/banner flags so the list stays on screen during and after a
  // manual refresh (refresh() holds the old data instead of dropping to
  // loading). See StatsRefreshMixin.
  bool _refreshing = false;
  bool _staleError = false;

  Future<void> _refresh() async {
    if (_refreshing) return;
    setState(() => _refreshing = true);
    try {
      await ref.read(bestFramesProvider.notifier).refresh();
      if (mounted) setState(() => _staleError = false);
    } catch (_) {
      if (mounted) setState(() => _staleError = true);
    } finally {
      if (mounted) setState(() => _refreshing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(bestFramesProvider, (_, next) {
      if (_staleError && next.hasValue && !next.isLoading) {
        setState(() => _staleError = false);
      }
    });
    final async = ref.watch(bestFramesProvider);
    final frames = async.asData?.value;
    final spinning = _refreshing || (async.isLoading && frames == null);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Expanded(
              child: Text('Best Frames',
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
        if (_staleError && frames != null) const _StaleBanner(),
        _body(async, frames),
      ],
    );
  }

  Widget _body(
    AsyncValue<List<BestFrame>?> async,
    List<BestFrame>? frames,
  ) {
    if (async.isLoading && frames == null) {
      return const _Hint('Loading best frames…');
    }
    if (async.hasError && frames == null) {
      return Row(
        children: [
          const Expanded(child: _Hint('Could not load best frames.')),
          TextButton(
            onPressed: () => unawaited(_refresh()),
            child: const Text('Retry'),
          ),
        ],
      );
    }
    if (frames == null) {
      return const _Hint('Connect to a server to see your best frames.');
    }
    if (frames.isEmpty) {
      return const _Hint('No scored frames yet — your best frames appear here once captures are quality-scored.');
    }
    return Column(
      children: [
        for (final entry in frames.asMap().entries)
          _BestFrameTile(rank: entry.key + 1, frame: entry.value),
      ],
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
            'Couldn’t refresh — showing the last loaded frames.',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
                  color: AraColors.accentBusy,
                ),
          ),
        ],
      ),
    );
  }
}

class _BestFrameTile extends StatelessWidget {
  const _BestFrameTile({required this.rank, required this.frame});

  final int rank;
  final BestFrame frame;

  @override
  Widget build(BuildContext context) {
    final subtitle = [
      'Score ${frame.compositeScore.toStringAsFixed(2)}',
      ?frame.filterName,
      if (frame.capturedUtc != null) formatStatsDate(frame.capturedUtc),
    ].join(' · ');

    return ListTile(
      dense: true,
      leading: CircleAvatar(
        radius: 12,
        backgroundColor: AraColors.selectionBg,
        child: Text('$rank', style: Theme.of(context).textTheme.labelSmall),
      ),
      title: Text(
        frame.targetName.isEmpty ? '(unnamed target)' : frame.targetName,
        style: Theme.of(context).textTheme.bodyMedium,
      ),
      subtitle: Text(subtitle),
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
