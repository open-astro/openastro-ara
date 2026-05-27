import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/frame.dart';
import '../../state/library/library_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/library/frame_thumbnail.dart';
import 'frame_viewer_screen.dart';

/// Image Library per playbook §40. Phase 12f.1 renders the by-session
/// grouping over in-memory demo data. 12f.2 wires the real backend +
/// stretch picker + filter/rating pills + bulk operations.
class ImageLibraryScreen extends ConsumerWidget {
  const ImageLibraryScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(librarySessionsProvider);
    final grouping = ref.watch(libraryGroupingProvider);
    final groups = _groupSessions(sessions, grouping);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Image Library'),
        bottom: const PreferredSize(
          preferredSize: Size.fromHeight(48),
          child: _LibraryHeaderBar(),
        ),
      ),
      body: ListView(
        children: [
          for (final g in groups) ...[
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
              child: Text(
                g.label,
                style: Theme.of(context).textTheme.titleSmall?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
            ),
            ...g.sessions.map((s) => _SessionCard(session: s)),
          ],
        ],
      ),
    );
  }

  /// Apply the active grouping to the flat session list. Each group also
  /// gets its sessions sorted newest-first (matches NINA's UX). Phase 12f.2
  /// adds a secondary-sort dropdown.
  List<_SessionGroup> _groupSessions(
    List<CaptureSession> sessions,
    LibraryGrouping grouping,
  ) {
    final sorted = [...sessions]..sort((a, b) => b.date.compareTo(a.date));
    switch (grouping) {
      case LibraryGrouping.bySession:
        return [_SessionGroup(label: 'All sessions', sessions: sorted)];
      case LibraryGrouping.byTarget:
        final byTarget = <String, List<CaptureSession>>{};
        for (final s in sorted) {
          byTarget.putIfAbsent(s.targetName, () => []).add(s);
        }
        return byTarget.entries
            .map((e) => _SessionGroup(label: e.key, sessions: e.value))
            .toList();
      case LibraryGrouping.byDate:
        final byMonth = <String, List<CaptureSession>>{};
        for (final s in sorted) {
          final key =
              '${s.date.year}-${s.date.month.toString().padLeft(2, '0')}';
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
  final List<CaptureSession> sessions;
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
          // Filter + rating + search pills wire up in 12f.2.
          const _ChipPlaceholder(icon: Icons.filter_list, label: 'All filters'),
          const SizedBox(width: 8),
          const _ChipPlaceholder(icon: Icons.star_border, label: 'Any rating'),
          const SizedBox(width: 8),
          const _ChipPlaceholder(icon: Icons.search, label: 'Search'),
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

class _SessionCard extends StatelessWidget {
  final CaptureSession session;
  const _SessionCard({required this.session});

  String _dateLabel() {
    final d = session.date;
    return '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
  }

  String _integrationLabel() {
    final mins = session.totalIntegration.inMinutes;
    return '${mins ~/ 60}h ${(mins % 60).toString().padLeft(2, '0')}min total';
  }

  String _filterCountLabel() => session.framesByFilter.entries
      .map((e) => '${e.key}:${e.value}')
      .join(' ');

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Text(
                  '${_dateLabel()} — ${session.targetName}',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(width: 8),
                Text(
                  '(${session.siteName})',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                ),
              ],
            ),
            const SizedBox(height: 4),
            Text(
              '${_integrationLabel()} · ${_filterCountLabel()} (${session.frames.length} frames)',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                TextButton.icon(
                  onPressed: null,
                  icon: const Icon(Icons.add_photo_alternate_outlined, size: 16),
                  label: const Text('Capture Matching Flats'),
                ),
                TextButton.icon(
                  onPressed: null,
                  icon: const Icon(Icons.replay, size: 16),
                  label: const Text('Resume Target'),
                ),
              ],
            ),
            const SizedBox(height: 8),
            SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: session.frames
                    .map((f) => FrameThumbnail(
                          frame: f,
                          onTap: () => Navigator.of(context).push(
                            MaterialPageRoute<void>(
                              builder: (_) => FrameViewerScreen(frame: f),
                            ),
                          ),
                        ))
                    .toList(),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
