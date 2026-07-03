import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../state/library/library_selection.dart';
import '../../state/library/library_state.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/library/bulk_action_bar.dart';
import '../../widgets/library/frame_thumbnail.dart';
import '../calibration/calibration_screen.dart';
import 'live_frame_viewer_screen.dart';

/// Image Library per playbook §40 — 12f.2: live over `/api/v1/sessions` +
/// `/api/v1/frames` (sessions, frame strips, capture-time thumbnails), with
/// the §39.5 [Capture Matching Flats] flow wired on every session card.
/// Bulk operations (12f.3b) and Resume Target remain stubbed.
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
          // 64 = dropdown's 48px intrinsic + 16px vertical padding.
          preferredSize: Size.fromHeight(64),
          child: _LibraryHeaderBar(),
        ),
      ),
      body: sessions.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text('Could not load the library: $e',
                  textAlign: TextAlign.center),
              const SizedBox(height: 8),
              OutlinedButton(
                onPressed: () =>
                    ref.read(liveLibrarySessionsProvider.notifier).refresh(),
                child: const Text('Retry'),
              ),
            ],
          ),
        ),
        data: (list) {
          if (list == null) {
            return const Center(
                child: Text('Connect to a server to browse its library.'));
          }
          if (list.isEmpty) {
            return const Center(
                child: Text('No sessions yet — captured frames will appear here.'));
          }
          final filter = ref.watch(libraryFilterProvider);
          final visible = list.where(filter.matchesSession).toList();
          if (visible.isEmpty) {
            return Center(
              child: Column(mainAxisSize: MainAxisSize.min, children: [
                Text('No sessions match "${filter.query}".'),
                const SizedBox(height: 8),
                OutlinedButton(
                  onPressed: () =>
                      ref.read(libraryFilterProvider.notifier).clear(),
                  child: const Text('Clear filters'),
                ),
              ]),
            );
          }
          final groups = _groupSessions(visible, grouping);
          return Column(
            children: [
              Expanded(
                child: RefreshIndicator(
                  onRefresh: () =>
                      ref.read(liveLibrarySessionsProvider.notifier).refresh(),
                  child: ListView(
                    children: [
                      for (final g in groups) ...[
                        Padding(
                          padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
                          child: Text(
                            g.label,
                            style: Theme.of(context)
                                .textTheme
                                .titleSmall
                                ?.copyWith(color: AraColors.textSecondary),
                          ),
                        ),
                          ...g.sessions.map((s) => _SessionCard(session: s)),
                      ],
                      // Cursor paging: append the next server page on demand.
                      if (ref
                          .read(liveLibrarySessionsProvider.notifier)
                          .hasMore)
                        Center(
                          child: Padding(
                            padding: const EdgeInsets.all(12),
                            child: _LoadMoreButton(onLoadMore: () => ref
                                .read(liveLibrarySessionsProvider.notifier)
                                .loadMore()),
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
          final key = '${d.year}-${d.month.toString().padLeft(2, '0')}';
          byMonth.putIfAbsent(key, () => []).add(s);
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

class _LibraryHeaderBar extends ConsumerWidget {
  const _LibraryHeaderBar();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final grouping = ref.watch(libraryGroupingProvider);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          DropdownButton<LibraryGrouping>(
            value: grouping,
            onChanged: (g) {
              if (g != null) ref.read(libraryGroupingProvider.notifier).set(g);
            },
            items: const [
              DropdownMenuItem(
                  value: LibraryGrouping.bySession, child: Text('By Session')),
              DropdownMenuItem(
                  value: LibraryGrouping.byTarget, child: Text('By Target')),
              DropdownMenuItem(
                  value: LibraryGrouping.byDate, child: Text('By Date')),
            ],
          ),
          const SizedBox(width: 16),
          // 12f.3 filter/rating/search pills. Wrapped in a
          // SingleChildScrollView so the header stays usable on narrow widths.
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Consumer(builder: (context, ref, _) {
                final filter = ref.watch(libraryFilterProvider);
                final sessions =
                    ref.watch(liveLibrarySessionsProvider).value ?? const [];
                final filterNames = {
                  for (final s in sessions) ...s.filtersUsed
                }.toList()
                  ..sort();
                return Row(children: [
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
                      ref.read(libraryFilterProvider.notifier).setFilterName(
                          choice == 'All filters' ? null : choice);
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
                      ref.read(libraryFilterProvider.notifier).setMinRating(
                          choice == 'Any rating'
                              ? 0
                              : int.parse(choice.substring(0, 1)));
                    },
                  ),
                  const SizedBox(width: 8),
                  _FilterPill(
                    icon: Icons.search,
                    label: filter.query.isEmpty ? 'Search' : '"${filter.query}"',
                    active: filter.query.isNotEmpty,
                    onTap: () async {
                      final query = await showDialog<String>(
                        context: context,
                        builder: (_) => _SearchDialog(initial: filter.query),
                      );
                      if (query == null) return;
                      ref.read(libraryFilterProvider.notifier).setQuery(query);
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
                ]);
              }),
            ),
          ),
        ],
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

class _SearchDialog extends StatefulWidget {
  final String initial;
  const _SearchDialog({required this.initial});

  @override
  State<_SearchDialog> createState() => _SearchDialogState();
}

class _SearchDialogState extends State<_SearchDialog> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.initial);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Search targets'),
      content: TextField(
        controller: _controller,
        autofocus: true,
        decoration: const InputDecoration(labelText: 'Target name contains…'),
        onSubmitted: (v) => Navigator.of(context).pop(v.trim()),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.of(context).pop(_controller.text.trim()),
          child: const Text('Search'),
        ),
      ],
    );
  }
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
    final color = active ? AraColors.selectionBg : AraColors.textSecondary;
    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(16),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: AraColors.bgInput,
          border: Border.all(color: active ? AraColors.selectionBg : AraColors.border),
          borderRadius: BorderRadius.circular(16),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 6),
            Text(label,
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: color)),
          ],
        ),
      ),
    );
  }
}

class _SessionCard extends ConsumerWidget {
  final LibrarySession session;
  const _SessionCard({required this.session});

  String _dateLabel() {
    final d = session.sessionStartUtc.toLocal();
    return '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final filters = session.filtersUsed.join(' · ');
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '${_dateLabel()} — ${session.targetName}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 4),
            Text(
              [
                '${session.lightFrames} lights',
                if (session.calibrationFrames > 0)
                  '${session.calibrationFrames} calibration',
                if (filters.isNotEmpty) filters,
              ].join(' · '),
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                TextButton.icon(
                  // §39.5 — live since 12f.2: cards carry real session ids.
                  onPressed: () => showDialog<void>(
                    context: context,
                    builder: (_) => MatchingFlatsDialog(
                      sessionId: session.id,
                      targetName: session.targetName,
                      filterNames: session.filtersUsed,
                    ),
                  ),
                  icon: const Icon(Icons.add_photo_alternate_outlined, size: 16),
                  label: const Text('Capture Matching Flats'),
                ),
                Consumer(builder: (context, ref, _) {
                  final api = ref.watch(libraryApiProvider);
                  return TextButton.icon(
                    // §40.6 — the server resumes the session's recorded
                    // sequence (or synthesizes a per-filter plan from its
                    // lights) and we land on it in the Run tab.
                    onPressed: api == null
                        ? null
                        : () async {
                            try {
                              final id = await api.resumeTarget(session.id);
                              if (!context.mounted) return;
                              ScaffoldMessenger.of(context).showSnackBar(
                                  const SnackBar(
                                      content: Text(
                                          'Resume sequence saved — review the slew/center steps before running.')));
                              openGeneratedSequence(context, ref, id);
                            } on Exception catch (e) {
                              if (!context.mounted) return;
                              ScaffoldMessenger.of(context).showSnackBar(
                                  SnackBar(
                                      content: Text('Resume failed: $e')));
                            }
                          },
                    icon: const Icon(Icons.replay, size: 16),
                    label: const Text('Resume Target'),
                  );
                }),
              ],
            ),
            const SizedBox(height: 8),
            _FrameStrip(sessionId: session.id),
          ],
        ),
      ),
    );
  }
}

/// Lazily-loaded frame strip — in selection mode, tap toggles selection; out
/// of selection mode, tap opens the §40.5 frame viewer. Long-press always
/// enters selection mode (add-only).
class _FrameStrip extends ConsumerWidget {
  final String sessionId;
  const _FrameStrip({required this.sessionId});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final frames = ref.watch(sessionFramesProvider(sessionId));
    final api = ref.watch(libraryApiProvider);
    return frames.when(
      loading: () => const SizedBox(
        height: 72,
        child: Center(
            child: SizedBox(
                width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))),
      ),
      error: (e, _) => SizedBox(
        height: 40,
        child: Text('Frames unavailable: $e',
            style: Theme.of(context)
                .textTheme
                .bodySmall
                ?.copyWith(color: AraColors.textSecondary)),
      ),
      data: (all) {
        if (all.isEmpty) {
          return Text('No frames recorded for this session.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary));
        }
        final filter = ref.watch(libraryFilterProvider);
        final list = all.where(filter.matchesFrame).toList();
        if (list.isEmpty) {
          return Text('No frames match the active filters.',
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary));
        }
        final selection = ref.watch(librarySelectionProvider);
        final inSelectionMode = selection.isNotEmpty;
        return SingleChildScrollView(
          scrollDirection: Axis.horizontal,
          child: Row(
            children: [
              for (final f in list)
                FrameThumbnail(
                  filter: f.filterName ?? f.frameType.toUpperCase(),
                  hfr: f.hfr,
                  rating: f.rating,
                  imageUrl: api?.thumbnailUrl(f.id),
                  selected: selection.contains(f.id),
                  selectionMode: inSelectionMode,
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
                ),
            ],
          ),
        );
      },
    );
  }
}

/// Load-more with a local in-flight spinner; the notifier's own guard makes
/// double-taps a no-op even if this widget's state lags (r1).
class _LoadMoreButton extends StatefulWidget {
  final Future<void> Function() onLoadMore;
  const _LoadMoreButton({required this.onLoadMore});

  @override
  State<_LoadMoreButton> createState() => _LoadMoreButtonState();
}

class _LoadMoreButtonState extends State<_LoadMoreButton> {
  bool _busy = false;

  Future<void> _tap() async {
    setState(() => _busy = true);
    try {
      await widget.onLoadMore();
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      onPressed: _busy ? null : _tap,
      icon: _busy
          ? const SizedBox(
              width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2))
          : const Icon(Icons.expand_more, size: 16),
      label: const Text('Load more sessions'),
    );
  }
}
