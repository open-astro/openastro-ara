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
class BestFramesSection extends ConsumerWidget {
  const BestFramesSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(bestFramesProvider);
    final frames = async.asData?.value;

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
              onPressed: async.isLoading
                  ? null
                  : () => unawaited(
                      ref.read(bestFramesProvider.notifier).refresh()),
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
        _body(ref, async, frames),
      ],
    );
  }

  Widget _body(
    WidgetRef ref,
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
            onPressed: () =>
                unawaited(ref.read(bestFramesProvider.notifier).refresh()),
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
