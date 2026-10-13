import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/mosaic.dart';
import '../../state/library/mosaics_state.dart';
import '../../theme/ara_colors.dart';

/// §47 / #1146 — the Library's "Mosaics" section: one row per project the
/// planetarium framed, with how many panels are done and a dot per panel
/// (filled = done, half = started, outline = owed). Progress is the daemon's
/// own count of light frames filed under each panel, so it is right after a
/// restart and after frames are deleted. Renders nothing when there is no
/// server or no project.
class MosaicsSection extends ConsumerWidget {
  const MosaicsSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final projects = ref.watch(mosaicProjectsProvider).asData?.value;
    if (projects == null || projects.isEmpty) return const SizedBox.shrink();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 12, 20, 4),
          child: Text(
            'Mosaics',
            style: Theme.of(context).textTheme.labelLarge
                ?.copyWith(color: AraColors.textSecondary, letterSpacing: 0.6),
          ),
        ),
        for (final p in projects) MosaicProjectRow(project: p),
        const SizedBox(height: 12),
      ],
    );
  }
}

/// One project: name, grid, "k/N panels done · f frames", the panel dots.
class MosaicProjectRow extends StatelessWidget {
  final MosaicProject project;
  const MosaicProjectRow({super.key, required this.project});

  @override
  Widget build(BuildContext context) {
    final m = project.mosaic;
    final pr = project.progress;
    final total = pr.totalPanels > 0 ? pr.totalPanels : m.totalPanels;
    final done = pr.completedPanels;
    final lights = pr.framesCapturedTotal;
    final perPanel = pr.framesPerPanelTarget;
    final summary = [
      m.gridLabel,
      '$done/$total panels done',
      if (lights > 0) '$lights light frame${lights == 1 ? '' : 's'}',
      if (perPanel > 0) '$perPanel per panel',
    ].join(' · ');
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 6, 20, 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  m.name,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.w600),
                ),
                const SizedBox(height: 2),
                Text(
                  summary,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: Theme.of(context).textTheme.bodySmall
                      ?.copyWith(color: AraColors.textSecondary),
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          MosaicPanelDots(project: project),
        ],
      ),
    );
  }
}

/// The panel grid as dots, row-major like the framing overlay.
class MosaicPanelDots extends StatelessWidget {
  final MosaicProject project;
  const MosaicPanelDots({super.key, required this.project});

  @override
  Widget build(BuildContext context) {
    // The daemon clamps a grid to 32 per axis; the dots follow the real width.
    final cols = project.mosaic.panelCountX.clamp(1, 32);
    final panels = project.panels;
    if (panels.isEmpty) return const SizedBox.shrink();
    final rows = <List<MosaicPanel>>[];
    for (var i = 0; i < panels.length; i += cols) {
      rows.add(panels.sublist(i, (i + cols).clamp(0, panels.length)));
    }
    return Tooltip(
      message: [
        for (final p in panels)
          'Panel ${p.panelIndex + 1}: ${switch (p.status) {
            MosaicPanelStatus.done => 'done',
            MosaicPanelStatus.inProgress => '${p.framesCaptured} so far',
            MosaicPanelStatus.pending => 'owed',
          }}',
      ].join('\n'),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final r in rows)
            Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                for (final p in r)
                  Padding(
                    padding: const EdgeInsets.all(1.5),
                    child: _PanelDot(status: p.status),
                  ),
              ],
            ),
        ],
      ),
    );
  }
}

class _PanelDot extends StatelessWidget {
  final MosaicPanelStatus status;
  const _PanelDot({required this.status});

  @override
  Widget build(BuildContext context) {
    final accent = Theme.of(context).colorScheme.primary;
    return Container(
      width: 10,
      height: 10,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: switch (status) {
          MosaicPanelStatus.done => accent,
          MosaicPanelStatus.inProgress => accent.withValues(alpha: 0.45),
          MosaicPanelStatus.pending => Colors.transparent,
        },
        border: Border.all(color: accent, width: 1.2),
      ),
    );
  }
}
