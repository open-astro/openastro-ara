import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/fault_row.dart';
import '../../state/faults/fault_feed_state.dart';
import '../../state/faults/faults_state.dart';
import '../../theme/ara_colors.dart';
import '../status_indicator.dart';

/// §42.6 fault feed per playbook §42 — renders inline below the diagnostic
/// panel. Collapsed by default: the header rolls up the LIVE standing-fault
/// set (`activeFaultsProvider`, the same overlay the equipment chips blend);
/// expanding shows the persisted fault history (`GET /api/v1/faults`,
/// newest-first, WS-refreshed) with each fault's §42.3 reaction outcome.
class FaultPanel extends ConsumerStatefulWidget {
  const FaultPanel({super.key});

  @override
  ConsumerState<FaultPanel> createState() => _FaultPanelState();
}

class _FaultPanelState extends ConsumerState<FaultPanel> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final standing = ref.watch(activeFaultsProvider);
    final n = standing.byDeviceType.length;
    final level = n == 0
        ? StatusLevel.connected
        : standing.byDeviceType.values
            .map((f) => f.level)
            .reduce((a, b) => statusLevelRank(a) >= statusLevelRank(b) ? a : b);
    final label = n == 0
        ? 'Faults: none standing'
        : 'Faults: $n standing — ${level == StatusLevel.error ? 'critical' : 'advisory'}';
    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              child: Row(
                children: [
                  Icon(_expanded ? Icons.expand_more : Icons.chevron_right,
                      size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  StatusIndicator(level: level, label: label),
                  const Spacer(),
                  if (_expanded)
                    IconButton(
                      icon: const Icon(Icons.refresh, size: 16),
                      color: AraColors.textSecondary,
                      tooltip: 'Refresh fault history',
                      onPressed: () =>
                          ref.read(faultFeedProvider.notifier).refresh(),
                    ),
                ],
              ),
            ),
          ),
          if (_expanded)
            ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 160),
              child: _FaultHistoryList(),
            ),
        ],
      ),
    );
  }
}

class _FaultHistoryList extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final feed = ref.watch(faultFeedProvider);
    final notifier = ref.read(faultFeedProvider.notifier);
    return feed.when(
      loading: () => const Center(
          child: Padding(
              padding: EdgeInsets.all(12),
              child: SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2)))),
      error: (e, _) => Padding(
        padding: const EdgeInsets.all(12),
        child: Text('Fault history unavailable: $e',
            style: Theme.of(context)
                .textTheme
                .bodySmall
                ?.copyWith(color: AraColors.textSecondary)),
      ),
      data: (rows) {
        if (rows == null || rows.isEmpty) {
          return Padding(
            padding: const EdgeInsets.all(12),
            child: Text(
                rows == null ? 'No server connected' : 'No faults recorded',
                style: Theme.of(context)
                    .textTheme
                    .bodySmall
                    ?.copyWith(color: AraColors.textSecondary)),
          );
        }
        final hasMore = notifier.hasMore;
        return ListView.builder(
          shrinkWrap: true,
          itemCount: rows.length + (hasMore ? 1 : 0),
          itemBuilder: (context, i) {
            if (i == rows.length) {
              return ListTile(
                dense: true,
                title: Text('Load more…',
                    textAlign: TextAlign.center,
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textSecondary)),
                onTap: notifier.loadMore,
              );
            }
            return FaultHistoryTile(row: rows[i]);
          },
        );
      },
    );
  }
}

/// One fault-history row: severity/resolution dot, device + kind, details,
/// the §42.3 reaction outcome, and the detection time. Shared with the §42.6
/// per-session timeline dialog.
class FaultHistoryTile extends StatelessWidget {
  final FaultRow row;
  const FaultHistoryTile({super.key, required this.row});

  @override
  Widget build(BuildContext context) {
    final dot = row.resolved
        ? StatusLevel.connected
        : faultKindLevel(row.faultType);
    final device = row.equipmentName ?? row.equipmentType;
    final outcome = row.resolved
        ? 'recovered'
        : row.actionTaken ?? 'unresolved';
    return ListTile(
      dense: true,
      leading: Container(
        width: 8,
        height: 8,
        decoration: BoxDecoration(color: dot.color, shape: BoxShape.circle),
      ),
      title: Text(
        '$device — ${row.faultType.replaceAll('_', ' ')}'
        '${row.details == null ? '' : ' (${row.details})'}',
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: Theme.of(context).textTheme.bodySmall,
      ),
      subtitle: Text(
        outcome,
        style: Theme.of(context)
            .textTheme
            .labelSmall
            ?.copyWith(color: AraColors.textDisabled),
      ),
      trailing: Text(
        _formatTime(row.detectedUtc.toLocal()),
        style: Theme.of(context)
            .textTheme
            .labelSmall
            ?.copyWith(color: AraColors.textDisabled),
      ),
    );
  }

  static String _formatTime(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}:${t.second.toString().padLeft(2, '0')}';
}
