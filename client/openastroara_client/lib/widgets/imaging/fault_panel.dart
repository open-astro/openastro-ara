import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/fault_row.dart';
import '../../state/faults/fault_feed_state.dart';
import '../../state/faults/faults_state.dart';
import '../../theme/ara_colors.dart';
import '../status_indicator.dart';
import '../../util/friendly_error.dart';
import '../../l10n/l10n.dart';

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
                  // Expanded, not a Spacer: the label ellipsizes in the
                  // 320 px rail instead of overflowing (as DiagnosticPanel).
                  Expanded(
                    child: Align(
                      alignment: Alignment.centerLeft,
                      child: StatusIndicator(level: level, label: label),
                    ),
                  ),
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
        child: Text(friendlyError(context.l10n, e, action: 'load the fault history'),
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
        final groups = groupFaultRows(rows);
        final hasMore = notifier.hasMore;
        return ListView.builder(
          shrinkWrap: true,
          itemCount: groups.length + (hasMore ? 1 : 0),
          itemBuilder: (context, i) {
            if (i == groups.length) {
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
            return FaultGroupTile(group: groups[i]);
          },
        );
      },
    );
  }
}

/// #1767 — consecutive rows (newest first) for one device and fault type shown as one entry.
/// Presentation only: every row stays in the feed and in [rows].
class FaultGroup {
  final List<FaultRow> rows;
  const FaultGroup(this.rows);
  int get count => rows.length;
  DateTime get lastUtc => rows.first.detectedUtc;
  DateTime get firstUtc => rows.last.detectedUtc;
}

/// A fault row carries no episode id, so an episode is: same session, device and fault type,
/// each row within [faultEpisodeGap] of the next. A longer quiet gap starts a new episode.
const faultEpisodeGap = Duration(minutes: 10);

List<FaultGroup> groupFaultRows(List<FaultRow> rows) {
  String key(FaultRow r) =>
      '${r.sessionId}|${r.equipmentType}|${r.equipmentId ?? r.equipmentName}|${r.faultType}';
  final groups = <List<FaultRow>>[];
  for (final r in rows) {
    // Newest first: the previous row of the group is the later one.
    if (groups.isNotEmpty &&
        key(groups.last.last) == key(r) &&
        groups.last.last.detectedUtc.difference(r.detectedUtc) <=
            faultEpisodeGap) {
      groups.last.add(r);
    } else {
      groups.add([r]);
    }
  }
  return [for (final g in groups) FaultGroup(g)];
}

/// A group of one is a plain [FaultHistoryTile]; a longer one is a collapsed row with the
/// count and first / last time, its raw rows behind a tap.
class FaultGroupTile extends StatelessWidget {
  final FaultGroup group;
  const FaultGroupTile({super.key, required this.group});

  @override
  Widget build(BuildContext context) {
    if (group.count == 1) return FaultHistoryTile(row: group.rows.first);
    final r = group.rows.first;
    final l10n = context.l10n;
    final unresolved = group.rows.where((x) => !x.resolved).length;
    final span = l10n.faultGroupSpan(
      FaultHistoryTile.formatTime(group.firstUtc.toLocal()),
      FaultHistoryTile.formatTime(group.lastUtc.toLocal()),
    );
    // The outcome in words too: night mode's red filter makes the dot colours look alike.
    final outcome = unresolved == 0
        ? l10n.faultGroupRecovered
        : l10n.faultGroupUnresolved(unresolved);
    return ExpansionTile(
      // The oldest row is the group's: an open group stays open when the feed refreshes.
      key: PageStorageKey('fault-group-${group.rows.last.id}'),
      dense: true,
      tilePadding: const EdgeInsets.symmetric(horizontal: 16),
      shape: const Border(),
      collapsedShape: const Border(),
      leading: Container(
        width: 8,
        height: 8,
        decoration: BoxDecoration(
          color:
              (unresolved == 0
                      ? StatusLevel.connected
                      : faultKindLevel(r.faultType))
                  .color,
          shape: BoxShape.circle,
        ),
      ),
      title: Text(
        // Count first: the ellipsis in a narrow rail must not cut the number off.
        '${l10n.faultGroupCount(group.count)} · '
        '${r.equipmentName ?? r.equipmentType} — ${r.faultType.replaceAll('_', ' ')}',
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: Theme.of(context).textTheme.bodySmall,
      ),
      subtitle: Text(
        '$span · $outcome',
        style: Theme.of(context).textTheme.labelSmall
            ?.copyWith(color: AraColors.textSecondary),
      ),
      children: [for (final row in group.rows) FaultHistoryTile(row: row)],
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
    // A row resolved because the user disconnected the device mid-recovery
    // (#1307) is not "recovered": say what happened.
    final outcome = row.resolved
        ? (row.actionTaken?.startsWith('released') ?? false)
            ? 'released — disconnected by hand'
            : 'recovered'
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
            ?.copyWith(color: AraColors.textSecondary),
      ),
      trailing: Text(
        formatTime(row.detectedUtc.toLocal()),
        style: Theme.of(context)
            .textTheme
            .labelSmall
            ?.copyWith(color: AraColors.textSecondary),
      ),
    );
  }

  static String formatTime(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}:${t.second.toString().padLeft(2, '0')}';
}
