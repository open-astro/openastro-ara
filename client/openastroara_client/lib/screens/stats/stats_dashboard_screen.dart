import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/stats_csv_export.dart';
import '../../state/library/library_state.dart';
import '../../state/stats/stats_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/stats/charts/calendar_heatmap.dart';
import '../../widgets/stats/charts/focus_temp_scatter.dart';
import '../../widgets/stats/charts/frame_quality_chart.dart';
import '../../widgets/stats/achievements_section.dart';
import '../../widgets/stats/overview_section.dart';
import '../../widgets/stats/targets_section.dart';
import '../../widgets/stats/astrobin_export_dialog.dart';
import '../../widgets/stats/charts/guiding_rms_chart.dart';

/// Stats dashboard per playbook §50. Phase 12g.1 wires the Overview tiles +
/// Targets rollup + Best Frames list over `librarySessionsProvider` demo
/// data. 12g.2 adds chart visualizations (Focus & Temperature scatter,
/// Guiding RMS trends, Frame Quality composite, Calendar heatmap) via
/// fl_chart. 12g.3 wires `/api/v1/stats/*` + per-target detail drill-down.
class StatsDashboardScreen extends ConsumerWidget {
  const StatsDashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final bestFrames = ref.watch(bestFramesProvider);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Stats'),
        actions: [
          Builder(
            builder: (context) => PopupMenuButton<String>(
              tooltip: 'Export CSV',
              onSelected: (key) => _exportCsv(context, ref, key),
              itemBuilder: (_) => const [
                PopupMenuItem(
                  value: 'sessions',
                  child: Text('Sessions summary'),
                ),
                PopupMenuItem(
                  value: 'frames',
                  child: Text('Per-frame details'),
                ),
                PopupMenuItem(
                  value: 'astrobin',
                  child: Text('AstroBin (per target)…'),
                ),
              ],
              child: const Padding(
                padding: EdgeInsets.symmetric(horizontal: 12),
                child: Row(children: [
                  Icon(Icons.file_download, size: 16),
                  SizedBox(width: 6),
                  Text('Export CSV'),
                  SizedBox(width: 4),
                  Icon(Icons.arrow_drop_down, size: 16),
                ]),
              ),
            ),
          ),
          const SizedBox(width: 8),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          const OverviewSection(),
          const SizedBox(height: 24),
          const TargetsSection(),
          const SizedBox(height: 24),
          _SectionTitle(title: 'Best Frames (by HFR)', context: context),
          ...bestFrames.asMap().entries.map(
                (entry) => ListTile(
                  dense: true,
                  leading: CircleAvatar(
                    radius: 12,
                    backgroundColor: AraColors.selectionBg,
                    child: Text('${entry.key + 1}',
                        style: Theme.of(context).textTheme.labelSmall),
                  ),
                  title: Text(entry.value.filename,
                      style: Theme.of(context).textTheme.bodySmall),
                  subtitle: Text(
                    'HFR ${entry.value.hfr.toStringAsFixed(2)} · '
                    '${entry.value.starCount} stars · '
                    '${entry.value.filter}',
                  ),
                  trailing: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      for (var i = 0; i < entry.value.rating; i++)
                        const Icon(Icons.star,
                            size: 12, color: AraColors.accentBusy),
                    ],
                  ),
                ),
              ),
          const SizedBox(height: 24),
          const AchievementsSection(),
          const SizedBox(height: 24),
          _SectionTitle(title: 'Visualizations', context: context),
          const FocusTempScatterChart(),
          const GuidingRmsChart(),
          const FrameQualityChart(),
          const CalendarHeatmap(),
        ],
      ),
    );
  }

  Future<void> _exportCsv(
    BuildContext context,
    WidgetRef ref,
    String key,
  ) async {
    // AstroBin export is per-target and server-backed — open the picker dialog
    // rather than copying a whole-catalog CSV to the clipboard.
    if (key == 'astrobin') {
      await showDialog<void>(
        context: context,
        builder: (_) => const AstrobinExportDialog(),
      );
      return;
    }
    final sessions = ref.read(librarySessionsProvider);
    // Count rows from the source data, not by splitting the CSV — quoted
    // fields with embedded newlines (rare but legal per RFC-4180) would
    // otherwise inflate the count.
    final rowCount = switch (key) {
      'sessions' => sessions.length,
      'frames' => sessions.fold<int>(0, (sum, s) => sum + s.frames.length),
      _ => 0,
    };
    final csv = switch (key) {
      'sessions' => exportSessionsCsv(sessions),
      'frames' => exportFramesCsv(sessions),
      _ => '',
    };
    if (csv.isEmpty) return;

    try {
      await Clipboard.setData(ClipboardData(text: csv));
    } on PlatformException {
      if (!context.mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Failed to copy CSV to clipboard.')),
      );
      return;
    }
    if (!context.mounted) return;
    final label =
        key == 'sessions' ? 'Sessions summary' : 'Per-frame details';
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          '$label CSV copied to clipboard '
          '($rowCount rows). Real file save lands in 12g.4.',
        ),
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  final String title;
  final BuildContext context;
  const _SectionTitle({required this.title, required this.context});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Text(title, style: Theme.of(context).textTheme.titleMedium),
    );
  }
}

