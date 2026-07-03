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
          final groups = _groupSessions(list, grouping);
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
          // Filter + rating + search pills wire up in 12f.3. Wrapped in a
          // SingleChildScrollView so the header stays usable on narrow widths.
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(children: const [
                _ChipPlaceholder(icon: Icons.filter_list, label: 'All filters'),
                SizedBox(width: 8),
                _ChipPlaceholder(icon: Icons.star_border, label: 'Any rating'),
                SizedBox(width: 8),
                _ChipPlaceholder(icon: Icons.search, label: 'Search'),
              ]),
            ),
          ),
        ],
      ),
    );
  }
}

class _ChipPlaceholder extends StatelessWidget {
  final IconData icon;
  final String label;
  const _ChipPlaceholder({required this.icon, required this.label});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      decoration: BoxDecoration(
        color: AraColors.bgInput,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: AraColors.textSecondary),
          const SizedBox(width: 6),
          Text(label,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  )),
        ],
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
                TextButton.icon(
                  // Resume Target (§40.6) wires in a later slice.
                  onPressed: null,
                  icon: const Icon(Icons.replay, size: 16),
                  label: const Text('Resume Target'),
                ),
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
      data: (list) {
        if (list.isEmpty) {
          return Text('No frames recorded for this session.',
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
