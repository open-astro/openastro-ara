import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/stats/stats_targets_state.dart';
import '../../widgets/stats/charts/calendar_heatmap.dart';
import '../../widgets/stats/charts/focus_temp_scatter.dart';
import '../../widgets/stats/charts/frame_quality_chart.dart';
import '../../widgets/stats/achievements_section.dart';
import '../../widgets/stats/best_frames_section.dart';
import '../../widgets/stats/overview_section.dart';
import '../../widgets/stats/targets_section.dart';
import '../../widgets/stats/astrobin_export_dialog.dart';
import '../../widgets/stats/charts/guiding_rms_chart.dart';

/// Stats dashboard per playbook §50. The Overview, Targets, Best Frames, and
/// Achievements sections are wired to the live daemon (`/api/v1/stats/*`); the
/// chart visualizations (Focus & Temperature scatter, Guiding RMS trends, Frame
/// Quality composite, Calendar heatmap) render via fl_chart. CSV export +
/// per-target detail drill-down remain follow-ups.
class StatsDashboardScreen extends ConsumerWidget {
  const StatsDashboardScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
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
          const BestFramesSection(),
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
    // §50: the CSVs come from the server's catalog (scope=frames|sessions),
    // not from client-side demo data.
    final api = ref.read(statsExportApiProvider);
    if (api == null) {
      ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Connect to a server to export.')));
      return;
    }
    final String csv;
    try {
      csv = await api.fetchCsv(key);
    } on Exception catch (e) {
      if (!context.mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Export failed: $e')));
      return;
    }
    if (!context.mounted) return;
    // Data rows = lines minus the header. The server never embeds newlines in
    // fields (targets are CsvEscape'd but single-line), so a line count is
    // accurate here.
    final rowCount =
        csv.split('\n').where((l) => l.trim().isNotEmpty).length - 1;

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

