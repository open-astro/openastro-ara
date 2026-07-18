import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/run_eta.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../state/sequencer/run_spotlight_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'run_state_badge.dart';

/// §Run-redesign S5 — the live run dashboard: a band under the toolbar that
/// exists only while a run is active. One glance answers "is my night going
/// well?": sequence name + coloured state chip, the current instruction,
/// a determinate progress bar, and elapsed / est-remaining. The §58.12
/// needs-attention state renders the whole band in error dress.
class RunDashboardBand extends ConsumerStatefulWidget {
  const RunDashboardBand({super.key});

  @override
  ConsumerState<RunDashboardBand> createState() => _RunDashboardBandState();
}

class _RunDashboardBandState extends ConsumerState<RunDashboardBand> {
  @override
  void initState() {
    super.initState();
    // Tick the elapsed readout once a second while mounted. The band only
    // mounts during an active run, so the timer's lifetime is the run's.
    _ticker = Stream<void>.periodic(const Duration(seconds: 1));
  }

  late final Stream<void> _ticker;

  String _fmtDuration(Duration d) {
    final h = d.inHours;
    final m = d.inMinutes % 60;
    final s = d.inSeconds % 60;
    return h > 0
        ? '$h:${m.toString().padLeft(2, '0')}:${s.toString().padLeft(2, '0')}'
        : '$m:${s.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(sequenceRunStateProvider).value;
    final state = run?.state;
    if (run == null || state == null || !state.isActive) {
      return const SizedBox.shrink();
    }
    final editor = ref.watch(sequenceEditorProvider);
    final color = RunStateBadge.colorFor(state);
    final total = run.instructionsTotal;
    final completed = run.instructionsCompleted;
    final progress = total > 0 ? (completed / total).clamp(0.0, 1.0) : null;
    final needsAttention = state == SequenceRunState.pausedAwaitingUser;

    final staticEta = editor != null && editor.id == run.sequenceId
        ? estimateRunEta(editor.body).totalSeconds
        : 0.0;

    return StreamBuilder<void>(
      stream: _ticker,
      builder: (context, _) {
        final elapsed = run.startedUtc == null
            ? Duration.zero
            : DateTime.now().toUtc().difference(run.startedUtc!);
        final remainingS = estimateRemainingSeconds(
          staticTotalSeconds: staticEta,
          completed: completed,
          total: total,
          elapsed: elapsed,
        );
        return Container(
          key: const Key('run-dashboard-band'),
          padding: const EdgeInsets.fromLTRB(
              AraSpace.s16, AraSpace.s12, AraSpace.s16, AraSpace.s12),
          decoration: BoxDecoration(
            color: needsAttention
                ? AraColors.accentError.withValues(alpha: 0.08)
                : AraColors.bgPanel,
            border: Border(
              bottom: BorderSide(
                  color: needsAttention ? AraColors.accentError : AraColors.border),
            ),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            mainAxisSize: MainAxisSize.min,
            children: [
              Row(
                children: [
                  Expanded(
                    child: Row(
                      children: [
                        Flexible(
                          child: Text(
                            run.currentTargetName?.isNotEmpty == true
                                ? run.currentTargetName!
                                : 'Sequence run',
                            style: AraText.title,
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        const SizedBox(width: AraSpace.s8),
                        RunStateBadge(state),
                      ],
                    ),
                  ),
                  if (run.startedUtc != null)
                    Text(_fmtDuration(elapsed), style: AraText.numeric),
                  if (remainingS > 0) ...[
                    Text('  ·  ', style: AraText.caption),
                    Text(
                      '~${_fmtDuration(Duration(seconds: remainingS.round()))} left',
                      style: AraText.numeric,
                    ),
                  ],
                ],
              ),
              const SizedBox(height: AraSpace.s8),
              ClipRRect(
                borderRadius: BorderRadius.circular(3),
                child: LinearProgressIndicator(
                  value: progress,
                  minHeight: 5,
                  backgroundColor: AraColors.bgInput,
                  valueColor: AlwaysStoppedAnimation(color),
                ),
              ),
              const SizedBox(height: AraSpace.s8),
              Row(
                children: [
                  Expanded(
                    child: InkWell(
                      // Tapping the current-instruction line jumps the tree to
                      // the spotlighted node (the tree autoscrolls on change;
                      // reselecting the spotlight path re-fires attention).
                      onTap: () {
                        final path =
                            ref.read(runSpotlightProvider)?.currentPath;
                        if (path != null) {
                          ref
                              .read(sequenceEditorProvider.notifier)
                              .select(path);
                        }
                      },
                      child: Text(
                        needsAttention
                            ? 'The rig needs you — ${run.currentInstructionDescription ?? 'check the mount/guider and resume'}'
                            : (run.currentInstructionDescription?.isNotEmpty ==
                                    true
                                ? '▶ ${run.currentInstructionDescription}'
                                : 'Running…'),
                        style: AraText.caption.copyWith(
                            color: needsAttention
                                ? AraColors.accentError
                                : AraColors.textSecondary),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                  if (total > 0)
                    Text('$completed/$total', style: AraText.numeric),
                ],
              ),
            ],
          ),
        );
      },
    );
  }
}
