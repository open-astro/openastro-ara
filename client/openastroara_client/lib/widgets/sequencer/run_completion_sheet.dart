import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/library/live_library.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/app_shell_state.dart';
import '../../state/library/live_library_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';

/// §Run-redesign S6 — a finished run is an event, not a log line. Shown once
/// on the running→terminal transition: duration, how the night ended, frames
/// per filter for the session overlapping the run, the failing instruction
/// when it failed, and a jump to the Library. Failure/abort wear error dress.
class RunCompletionSheet extends ConsumerWidget {
  const RunCompletionSheet({super.key, required this.run});

  final SequenceRunStateInfo run;

  static Future<void> show(BuildContext context, SequenceRunStateInfo run) =>
      showModalBottomSheet<void>(
        context: context,
        backgroundColor: AraColors.bgPanel,
        shape: const RoundedRectangleBorder(
            borderRadius: BorderRadius.vertical(top: Radius.circular(14))),
        builder: (_) => RunCompletionSheet(run: run),
      );

  String _fmtDuration(Duration d) {
    final h = d.inHours;
    final m = d.inMinutes % 60;
    if (h > 0) return '${h}h ${m.toString().padLeft(2, '0')}m';
    return '${m}m';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final state = run.state;
    final ok = state == SequenceRunState.completed;
    final color = ok ? AraColors.accentConnected : AraColors.accentError;
    final title = switch (state) {
      SequenceRunState.completed => 'Run complete',
      SequenceRunState.failed => 'Run failed',
      _ => 'Run stopped',
    };
    final duration = (run.startedUtc != null && run.completedUtc != null)
        ? run.completedUtc!.difference(run.startedUtc!)
        : null;

    // Frames per filter for the session overlapping this run (best-effort:
    // the newest session whose start is before the run's end).
    final sessions = ref.watch(liveLibrarySessionsProvider).value;
    final session = sessions?.isNotEmpty == true ? sessions!.first : null;
    final frames = session == null
        ? null
        : ref.watch(sessionFramesProvider(session.id)).value;
    final inRun = frames == null || run.startedUtc == null
        ? frames
        : [
            for (final f in frames)
              if (!f.capturedUtc.isBefore(run.startedUtc!)) f,
          ];
    final perFilter = <String, int>{};
    for (final f in inRun ?? const <LibraryFrameItem>[]) {
      final filter = f.filterName;
      final name = filter != null && filter.isNotEmpty ? filter : 'No filter';
      perFilter[name] = (perFilter[name] ?? 0) + 1;
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(AraSpace.s24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Icon(ok ? Icons.check_circle : Icons.error_outline,
                    color: color, size: 28),
                const SizedBox(width: AraSpace.s12),
                Text(title, style: AraText.title.copyWith(fontSize: 18)),
                const Spacer(),
                if (duration != null)
                  Text(_fmtDuration(duration), style: AraText.numeric),
              ],
            ),
            const SizedBox(height: AraSpace.s12),
            if (run.currentTargetName?.isNotEmpty == true)
              Text(run.currentTargetName!, style: AraText.body),
            if (!ok && run.currentInstructionDescription?.isNotEmpty == true)
              Padding(
                padding: const EdgeInsets.only(top: AraSpace.s4),
                child: Text(
                  'Stopped at: ${run.currentInstructionDescription}',
                  style: AraText.caption.copyWith(color: AraColors.accentError),
                ),
              ),
            Padding(
              padding: const EdgeInsets.only(top: AraSpace.s4),
              child: Text(
                '${run.instructionsCompleted}/${run.instructionsTotal} instructions completed',
                style: AraText.caption,
              ),
            ),
            if (perFilter.isNotEmpty) ...[
              const SizedBox(height: AraSpace.s16),
              Text('FRAMES THIS RUN', style: AraText.section),
              const SizedBox(height: AraSpace.s8),
              Wrap(
                spacing: AraSpace.s8,
                runSpacing: AraSpace.s4,
                children: [
                  for (final e in perFilter.entries)
                    Container(
                      padding: const EdgeInsets.symmetric(
                          horizontal: AraSpace.s8, vertical: 3),
                      decoration: BoxDecoration(
                        color: AraColors.bgInput,
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: Text('${e.key} × ${e.value}', style: AraText.caption),
                    ),
                ],
              ),
            ],
            const SizedBox(height: AraSpace.s24),
            Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                TextButton(
                  onPressed: () => Navigator.of(context).pop(),
                  child: const Text('Close'),
                ),
                const SizedBox(width: AraSpace.s8),
                FilledButton.icon(
                  icon: const Icon(Icons.photo_library_outlined, size: 16),
                  label: const Text('View in Library'),
                  onPressed: () {
                    Navigator.of(context).pop();
                    ref
                        .read(selectedTabIndexProvider.notifier)
                        .select(kLiveTabIndex);
                  },
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
