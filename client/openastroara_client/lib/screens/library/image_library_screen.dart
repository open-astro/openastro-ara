import 'dart:async';
import 'dart:io';
import '../../util/friendly_error.dart';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../state/faults/fault_feed_state.dart';
import '../../state/library/library_selection.dart';
import '../../state/library/library_state.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';
import '../../state/backup/backup_stream_state.dart';
import '../../widgets/imaging/fault_panel.dart' show FaultHistoryTile;
import '../../widgets/library/bulk_action_bar.dart';
import '../../widgets/library/frame_thumbnail.dart';
import '../../widgets/library/load_more_button.dart';
import '../calibration/calibration_screen.dart';
import 'live_frame_viewer_screen.dart';

const _monthNames = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec',
];

String _friendlyDate(DateTime utc) {
  final d = utc.toLocal();
  return '${_monthNames[d.month - 1]} ${d.day}, ${d.year}';
}

/// Image Library per playbook §40 — 12f.2: live over `/api/v1/sessions` +
/// `/api/v1/frames` (sessions, frame grids, capture-time thumbnails), with
/// the §39.5 [Capture Matching Flats] flow on every session's overflow menu.
/// Photos-style layout: session sections with a lazy thumbnail grid, so
/// off-screen tiles never fetch their thumbnails.
class ImageLibraryScreen extends ConsumerWidget {
  const ImageLibraryScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(liveLibrarySessionsProvider);
    final grouping = ref.watch(libraryGroupingProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Image Library'),
        bottom: const PreferredSize(
          preferredSize: Size.fromHeight(52),
          child: _LibraryToolbar(),
        ),
      ),
      body: sessions.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => _CenteredNotice(
          message: friendlyError(e, action: 'load your library'),
          action: OutlinedButton(
            onPressed: () =>
                ref.read(liveLibrarySessionsProvider.notifier).refresh(),
            child: const Text('Retry'),
          ),
        ),
        data: (list) {
          if (list == null) {
            return const _CenteredNotice(
              message: 'Connect to a server to browse its library.',
            );
          }
          if (list.isEmpty) {
            return const _CenteredNotice(
              message: 'No sessions yet — captured frames will appear here.',
            );
          }
          final filter = ref.watch(libraryFilterProvider);
          final visible = list.where(filter.matchesSession).toList();
          final hasMorePages = ref
              .read(liveLibrarySessionsProvider.notifier)
              .hasMore;
          if (visible.isEmpty) {
            return _CenteredNotice(
              message: 'No sessions match "${filter.query}".',
              detail: hasMorePages
                  ? 'More sessions exist on the server — load them to widen the search.'
                  : null,
              action: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  OutlinedButton(
                    onPressed: () =>
                        ref.read(libraryFilterProvider.notifier).clear(),
                    child: const Text('Clear filters'),
                  ),
                  // A match may live in an unfetched page (r1) — keep paging
                  // reachable without forcing the user to drop their filter.
                  if (hasMorePages) ...[
                    const SizedBox(width: 8),
                    LoadMoreButton(
                      onLoadMore: () => ref
                          .read(liveLibrarySessionsProvider.notifier)
                          .loadMore(),
                    ),
                  ],
                ],
              ),
            );
          }
          final groups = _groupSessions(visible, grouping);
          return Column(
            children: [
              Expanded(
                child: RefreshIndicator(
                  onRefresh: () =>
                      ref.read(liveLibrarySessionsProvider.notifier).refresh(),
                  child: CustomScrollView(
                    physics: const AlwaysScrollableScrollPhysics(),
                    slivers: [
                      const SliverToBoxAdapter(child: SizedBox(height: 8)),
                      for (final g in groups) ...[
                        // A lone "All sessions" banner is noise — group
                        // labels only earn their row when grouping is on.
                        if (grouping != LibraryGrouping.bySession)
                          SliverToBoxAdapter(child: _GroupLabel(g.label)),
                        for (final s in g.sessions) ...[
                          SliverToBoxAdapter(child: _SessionHeader(session: s)),
                          _SessionFramesGrid(sessionId: s.id),
                          const SliverToBoxAdapter(child: SizedBox(height: 24)),
                        ],
                      ],
                      if (hasMorePages)
                        SliverToBoxAdapter(
                          child: Center(
                            child: Padding(
                              padding: const EdgeInsets.all(12),
                              child: LoadMoreButton(
                                onLoadMore: () => ref
                                    .read(liveLibrarySessionsProvider.notifier)
                                    .loadMore(),
                              ),
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ),
              // Slides into view when selection is non-empty (§40.8).
              const LibraryBulkActionBar(),
            ],
          );
        },
      ),
    );
  }

  /// Apply the active grouping. Each group's sessions sort newest-first
  /// (matches NINA's UX).
  List<_SessionGroup> _groupSessions(
    List<LibrarySession> sessions,
    LibraryGrouping grouping,
  ) {
    final sorted = [...sessions]
      ..sort((a, b) => b.sessionStartUtc.compareTo(a.sessionStartUtc));
    switch (grouping) {
      case LibraryGrouping.bySession:
        return [_SessionGroup(label: 'All sessions', sessions: sorted)];
      case LibraryGrouping.byTarget:
        final byTarget = <String, List<LibrarySession>>{};
        for (final s in sorted) {
          byTarget.putIfAbsent(s.targetName, () => []).add(s);
        }
        return byTarget.entries
            .map((e) => _SessionGroup(label: e.key, sessions: e.value))
            .toList();
      case LibraryGrouping.byDate:
        final byMonth = <String, List<LibrarySession>>{};
        for (final s in sorted) {
          final d = s.sessionStartUtc.toLocal();
          byMonth
              .putIfAbsent('${_monthNames[d.month - 1]} ${d.year}', () => [])
              .add(s);
        }
        return byMonth.entries
            .map((e) => _SessionGroup(label: e.key, sessions: e.value))
            .toList();
    }
  }
}

class _SessionGroup {
  final String label;
  final List<LibrarySession> sessions;
  const _SessionGroup({required this.label, required this.sessions});
}

class _CenteredNotice extends StatelessWidget {
  final String message;
  final String? detail;
  final Widget? action;
  const _CenteredNotice({required this.message, this.detail, this.action});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center),
          if (detail != null) ...[
            const SizedBox(height: 4),
            Text(
              detail!,
              textAlign: TextAlign.center,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
          ],
          if (action != null) ...[const SizedBox(height: 12), action!],
        ],
      ),
    );
  }
}

class _GroupLabel extends StatelessWidget {
  final String label;
  const _GroupLabel(this.label);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 4),
      child: Text(
        label,
        style: Theme.of(
          context,
        ).textTheme.titleSmall?.copyWith(color: AraColors.textSecondary),
      ),
    );
  }
}

/// Quiet toolbar under the app bar: grouping popup, filter/rating pills, and
/// an inline search field — no dialogs for typing a search.
class _LibraryToolbar extends ConsumerWidget {
  const _LibraryToolbar();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final grouping = ref.watch(libraryGroupingProvider);
    final filter = ref.watch(libraryFilterProvider);
    final sessions = ref.watch(liveLibrarySessionsProvider).value ?? const [];
    final filterNames = {for (final s in sessions) ...s.filtersUsed}.toList()
      ..sort();
    const groupingLabels = {
      LibraryGrouping.bySession: 'All Sessions',
      LibraryGrouping.byTarget: 'By Target',
      LibraryGrouping.byDate: 'By Month',
    };
    return Container(
      height: 52,
      padding: const EdgeInsets.symmetric(horizontal: 12),
      alignment: Alignment.centerLeft,
      child: Row(
        children: [
          // The pill cluster scrolls when the window narrows; search and
          // refresh keep their footing on the right.
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  PopupMenuButton<LibraryGrouping>(
                    tooltip: 'Group sessions',
                    onSelected: (g) =>
                        ref.read(libraryGroupingProvider.notifier).set(g),
                    itemBuilder: (context) => [
                      for (final e in groupingLabels.entries)
                        CheckedPopupMenuItem(
                          value: e.key,
                          checked: grouping == e.key,
                          child: Text(e.value),
                        ),
                    ],
                    child: Padding(
                      padding: const EdgeInsets.symmetric(
                        horizontal: 8,
                        vertical: 6,
                      ),
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            groupingLabels[grouping]!,
                            style: Theme.of(context).textTheme.titleSmall
                                ?.copyWith(fontWeight: FontWeight.w600),
                          ),
                          const Icon(
                            Icons.expand_more,
                            size: 18,
                            color: AraColors.textSecondary,
                          ),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  _FilterPill(
                    icon: Icons.filter_list,
                    label: filter.filterName ?? 'All filters',
                    active: filter.filterName != null,
                    onTap: () async {
                      final choice = await _pickFromMenu(context, [
                        'All filters',
                        ...filterNames,
                      ]);
                      if (choice == null) return;
                      if (!context.mounted) return;
                      ref
                          .read(libraryFilterProvider.notifier)
                          .setFilterName(
                            choice == 'All filters' ? null : choice,
                          );
                    },
                  ),
                  const SizedBox(width: 8),
                  _FilterPill(
                    icon: Icons.star_border,
                    label: filter.minRating == 0
                        ? 'Any rating'
                        : '${filter.minRating}+ stars',
                    active: filter.minRating > 0,
                    onTap: () async {
                      final choice = await _pickFromMenu(context, [
                        'Any rating',
                        for (var i = 1; i <= 5; i++) '$i+ stars',
                      ]);
                      if (choice == null) return;
                      if (!context.mounted) return;
                      ref
                          .read(libraryFilterProvider.notifier)
                          .setMinRating(
                            choice == 'Any rating'
                                ? 0
                                : int.parse(choice.substring(0, 1)),
                          );
                    },
                  ),
                  if (filter.isActive) ...[
                    const SizedBox(width: 8),
                    _FilterPill(
                      icon: Icons.clear,
                      label: 'Clear',
                      active: false,
                      onTap: () =>
                          ref.read(libraryFilterProvider.notifier).clear(),
                    ),
                  ],
                ],
              ),
            ),
          ),
          const SizedBox(width: 12),
          const _SearchField(),
          IconButton(
            tooltip: 'Refresh',
            icon: const Icon(Icons.refresh, size: 20),
            color: AraColors.textSecondary,
            onPressed: () =>
                ref.read(liveLibrarySessionsProvider.notifier).refresh(),
          ),
        ],
      ),
    );
  }
}

/// Inline debounced search over target names.
class _SearchField extends ConsumerStatefulWidget {
  const _SearchField();

  @override
  ConsumerState<_SearchField> createState() => _SearchFieldState();
}

class _SearchFieldState extends ConsumerState<_SearchField> {
  final _controller = TextEditingController();
  Timer? _debounce;

  @override
  void dispose() {
    _debounce?.cancel();
    _controller.dispose();
    super.dispose();
  }

  void _onChanged(String v) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), () {
      if (mounted) {
        ref.read(libraryFilterProvider.notifier).setQuery(v.trim());
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    // An external clear (the Clear pill) must empty the visible field too.
    ref.listen(libraryFilterProvider, (_, next) {
      if (next.query.isEmpty && _controller.text.isNotEmpty) {
        _controller.clear();
      }
    });
    return SizedBox(
      width: 220,
      height: 32,
      child: TextField(
        controller: _controller,
        onChanged: _onChanged,
        style: Theme.of(context).textTheme.bodySmall,
        decoration: InputDecoration(
          isDense: true,
          contentPadding: EdgeInsets.zero,
          hintText: 'Search targets',
          hintStyle: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AraColors.textDisabled),
          prefixIcon: const Icon(
            Icons.search,
            size: 16,
            color: AraColors.textSecondary,
          ),
          filled: true,
          fillColor: AraColors.bgInput,
          border: OutlineInputBorder(
            borderRadius: BorderRadius.circular(8),
            borderSide: BorderSide.none,
          ),
        ),
      ),
    );
  }
}

/// Bottom-sheet style single-choice menu used by the filter/rating pills.
Future<String?> _pickFromMenu(BuildContext context, List<String> options) {
  return showDialog<String>(
    context: context,
    builder: (context) => SimpleDialog(
      children: [
        for (final option in options)
          SimpleDialogOption(
            onPressed: () => Navigator.of(context).pop(option),
            child: Text(option),
          ),
      ],
    ),
  );
}

class _FilterPill extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool active;
  final VoidCallback onTap;
  const _FilterPill({
    required this.icon,
    required this.label,
    required this.active,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final color = active ? AraColors.selectionFg : AraColors.textSecondary;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: active ? AraColors.selectionBg : AraColors.bgInput,
          borderRadius: BorderRadius.circular(16),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 6),
            Text(
              label,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: color),
            ),
          ],
        ),
      ),
    );
  }
}

/// Section header for one session: title + metadata line on the left, faults
/// badge and an overflow menu (Capture Matching Flats / Resume Target) on the
/// right — actions live behind "⋯" instead of a row of link buttons.
class _SessionHeader extends ConsumerWidget {
  final LibrarySession session;
  const _SessionHeader({required this.session});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final filters = session.filtersUsed.join(' · ');
    final api = ref.watch(libraryApiProvider);
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 12, 20, 10),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  session.targetName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(
                    context,
                  ).textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 2),
                Text(
                  [
                    _friendlyDate(session.sessionStartUtc),
                    '${session.lightFrames} lights',
                    if (session.calibrationFrames > 0)
                      '${session.calibrationFrames} calibration',
                    if (filters.isNotEmpty) filters,
                  ].join(' · '),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
              ],
            ),
          ),
          _SessionFaultsBadge(
            sessionId: session.id,
            targetName: session.targetName,
          ),
          PopupMenuButton<String>(
            tooltip: 'Session actions',
            icon: const Icon(
              Icons.more_horiz,
              size: 20,
              color: AraColors.textSecondary,
            ),
            onSelected: (action) => _runAction(context, ref, action),
            itemBuilder: (context) => [
              const PopupMenuItem(
                value: 'flats',
                child: ListTile(
                  dense: true,
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(Icons.add_photo_alternate_outlined, size: 18),
                  title: Text('Capture Matching Flats'),
                ),
              ),
              PopupMenuItem(
                value: 'resume',
                enabled: api != null,
                child: const ListTile(
                  dense: true,
                  contentPadding: EdgeInsets.zero,
                  leading: Icon(Icons.replay, size: 18),
                  title: Text('Resume Target'),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Future<void> _runAction(
    BuildContext context,
    WidgetRef ref,
    String action,
  ) async {
    switch (action) {
      case 'flats':
        // §39.5 — live since 12f.2: cards carry real session ids.
        await showDialog<void>(
          context: context,
          builder: (_) => MatchingFlatsDialog(
            sessionId: session.id,
            targetName: session.targetName,
            filterNames: session.filtersUsed,
          ),
        );
      case 'resume':
        // §40.6 — the server resumes the session's recorded sequence (or
        // synthesizes a per-filter plan from its lights) and we land on it
        // in the Run tab.
        final api = ref.read(libraryApiProvider);
        if (api == null) return;
        try {
          final id = await api.resumeTarget(session.id);
          if (!context.mounted) return;
          ScaffoldMessenger.of(context).showSnackBar(
            const SnackBar(
              content: Text(
                'Resume sequence saved — review the slew/center steps before running.',
              ),
            ),
          );
          openGeneratedSequence(context, ref, id);
        } on Exception catch (e) {
          if (!context.mounted) return;
          ScaffoldMessenger.of(
            context,
          ).showSnackBar(SnackBar(content: Text(friendlyError(e, action: 'resume the sequence'))));
        }
    }
  }
}

/// §42.6 per-session fault badge — hidden while the session has no recorded
/// faults; otherwise an amber count that opens the session's fault timeline.
/// Lazily fetched per card (like the frame grid): the sessions endpoint
/// carries no fault count, so each visible card asks the §42.5 log directly.
class _SessionFaultsBadge extends ConsumerWidget {
  final String sessionId;
  final String targetName;
  const _SessionFaultsBadge({
    required this.sessionId,
    required this.targetName,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final faults = ref.watch(sessionFaultsProvider(sessionId));
    final rows = faults.asData?.value;
    // Loading/error/none all render nothing — the badge is an alert, not a
    // status field, and a fault-log hiccup must not clutter every card.
    if (rows == null || rows.isEmpty) return const SizedBox.shrink();
    return TextButton.icon(
      onPressed: () => showDialog<void>(
        context: context,
        builder: (_) => AlertDialog(
          title: Text('Faults — $targetName'),
          content: SizedBox(
            width: 480,
            child: ListView.builder(
              shrinkWrap: true,
              itemCount: rows.length,
              itemBuilder: (context, i) => FaultHistoryTile(row: rows[i]),
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: const Text('Close'),
            ),
          ],
        ),
      ),
      icon: const Icon(Icons.warning_amber_outlined, size: 16),
      style: TextButton.styleFrom(foregroundColor: AraColors.accentBusy),
      label: Text('${rows.length} fault${rows.length == 1 ? '' : 's'}'),
    );
  }
}

/// Lazily-loaded Photos-style thumbnail grid for one session. Rendered as a
/// sliver so off-screen tiles are never built — with hundreds of frames per
/// session, eagerly building every tile fires hundreds of concurrent
/// thumbnail fetches at the rig and none of them finish. In selection mode,
/// tap toggles selection; out of selection mode, tap opens the §40.5 frame
/// viewer. Long-press (or the hover circle) always enters selection mode.
class _SessionFramesGrid extends ConsumerWidget {
  final String sessionId;
  const _SessionFramesGrid({required this.sessionId});

  static const _padding = EdgeInsets.symmetric(horizontal: 20);

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final frames = ref.watch(sessionFramesProvider(sessionId));
    final api = ref.watch(libraryApiProvider);
    return frames.when(
      loading: () => const SliverToBoxAdapter(
        child: SizedBox(
          height: 72,
          child: Center(
            child: SizedBox(
              width: 16,
              height: 16,
              child: CircularProgressIndicator(strokeWidth: 2),
            ),
          ),
        ),
      ),
      error: (e, _) => SliverToBoxAdapter(
        child: Padding(
          padding: _padding,
          child: Text(
            'Frames unavailable: $e',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
        ),
      ),
      data: (all) {
        if (all.isEmpty) {
          return SliverToBoxAdapter(
            child: Padding(
              padding: _padding,
              child: Text(
                'No frames recorded for this session.',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
              ),
            ),
          );
        }
        final filter = ref.watch(libraryFilterProvider);
        final list = all.where(filter.matchesFrame).toList();
        if (list.isEmpty) {
          return SliverToBoxAdapter(
            child: Padding(
              padding: _padding,
              child: Text(
                'No frames match the active filters.',
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
              ),
            ),
          );
        }
        final selection = ref.watch(librarySelectionProvider);
        final inSelectionMode = selection.isNotEmpty;
        final backupConfigured = ref.watch(
          backupStreamProvider.select((s) => s.enabled),
        );
        return SliverPadding(
          padding: _padding,
          sliver: SliverGrid.builder(
            gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
              maxCrossAxisExtent: 132,
              mainAxisSpacing: 6,
              crossAxisSpacing: 6,
            ),
            itemCount: list.length,
            itemBuilder: (context, i) {
              final f = list[i];
              return FrameThumbnail(
                filter: f.filterName ?? f.frameType.toUpperCase(),
                hfr: f.hfr,
                rating: f.rating,
                imageUrl: api?.thumbnailUrl(f.id),
                selected: selection.contains(f.id),
                selectionMode: inSelectionMode,
                // §44 badge only when a backup stream is configured, and
                // "protected" only when mirrored to THIS desktop — sync is
                // per-target, another machine's mirror doesn't cover us.
                synced: frameSyncedForThisDesktop(
                  f,
                  backupConfigured: backupConfigured,
                  hostname: Platform.localHostname,
                ),
                onTap: () {
                  if (inSelectionMode) {
                    ref.read(librarySelectionProvider.notifier).toggle(f.id);
                  } else {
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => LiveFrameViewerScreen(frame: f),
                      ),
                    );
                  }
                },
                onLongPress: () {
                  // Long-press is add-only — never deselects.
                  if (!selection.contains(f.id)) {
                    ref.read(librarySelectionProvider.notifier).toggle(f.id);
                  }
                },
              );
            },
          ),
        );
      },
    );
  }
}
