import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/session_planner.dart';

/// §36.8 "What-if run" — plan an imaging session: the user gives the window
/// they can actually shoot (say 22:00–01:00) and how many targets they want,
/// and the planner allocates the window to the best object(s) from tonight's
/// ranked list, with Glover-optimal sub counts per slice. Replaces the old
/// what-if optics dialog (trying a different rig is what launchpad profiles
/// are for; planning tonight's run is what nobody else does for you).
class SessionPlanDialog extends ConsumerStatefulWidget {
  const SessionPlanDialog({super.key});

  @override
  ConsumerState<SessionPlanDialog> createState() => _SessionPlanDialogState();
}

class _SessionPlanDialogState extends ConsumerState<SessionPlanDialog> {
  TimeOfDay _start = const TimeOfDay(hour: 22, minute: 0);
  TimeOfDay _end = const TimeOfDay(hour: 1, minute: 0);
  int _targetCount = 1;
  SessionPlan? _plan;

  /// The next occurrence of [t] from now, local — an end time "earlier" than
  /// the start rolls to the next day (22:00 → 01:00 spans midnight).
  DateTime _nextLocal(TimeOfDay t, {DateTime? after}) {
    final now = DateTime.now();
    var candidate = DateTime(now.year, now.month, now.day, t.hour, t.minute);
    final floor = after ?? now;
    while (!candidate.isAfter(floor)) {
      candidate = candidate.add(const Duration(days: 1));
    }
    return candidate;
  }

  void _makePlan(List<TonightSkyObject> ranked) {
    final start = _nextLocal(_start);
    final end = _nextLocal(_end, after: start);
    setState(() {
      _plan = planImagingSession(
        ranked: ranked,
        windowStartUtc: start.toUtc(),
        windowEndUtc: end.toUtc(),
        targetCount: _targetCount,
      );
    });
  }

  Future<void> _pick(bool isStart) async {
    final picked = await showTimePicker(
      context: context,
      initialTime: isStart ? _start : _end,
    );
    if (picked == null || !mounted) return;
    setState(() {
      if (isStart) {
        _start = picked;
      } else {
        _end = picked;
      }
      _plan = null; // window changed — the old plan no longer applies
    });
  }

  String _fmtLocal(DateTime utc) {
    final l = utc.toLocal();
    return '${l.hour.toString().padLeft(2, '0')}:'
        '${l.minute.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final async = ref.watch(tonightSkyProvider);
    final plan = _plan;

    return AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: const Text('Plan tonight\'s session'),
      content: SizedBox(
        width: 420,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Tell me when you can image and I\'ll allocate the window to '
                'the best target(s) from tonight\'s list — with sub counts '
                'from the optimal-exposure criterion so you finish an image, '
                'not just start one.',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
              const SizedBox(height: 16),
              Row(
                children: [
                  Expanded(
                    child: OutlinedButton.icon(
                      icon: const Icon(Icons.schedule, size: 16),
                      label: Text('From ${_start.format(context)}'),
                      onPressed: () => _pick(true),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: OutlinedButton.icon(
                      icon: const Icon(Icons.schedule, size: 16),
                      label: Text('To ${_end.format(context)}'),
                      onPressed: () => _pick(false),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              SegmentedButton<int>(
                segments: const [
                  ButtonSegment(value: 1, label: Text('1 target')),
                  ButtonSegment(value: 2, label: Text('2 targets')),
                ],
                selected: {_targetCount},
                onSelectionChanged: (s) => setState(() {
                  _targetCount = s.first;
                  _plan = null;
                }),
              ),
              const SizedBox(height: 16),
              async.when(
                loading: () =>
                    const Center(child: CircularProgressIndicator()),
                error: (e, _) => Text(
                  'Tonight\'s list isn\'t available — refresh the panel first.',
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: AraColors.accentError),
                ),
                data: (ranked) => Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    FilledButton.icon(
                      icon: const Icon(Icons.auto_awesome, size: 16),
                      label: const Text('Plan it'),
                      onPressed:
                          ranked.isEmpty ? null : () => _makePlan(ranked),
                    ),
                    if (plan != null) ...[
                      const SizedBox(height: 16),
                      if (plan.targets.isEmpty)
                        Text(
                          plan.notes.join(' '),
                          style: theme.textTheme.bodySmall?.copyWith(
                              color: AraColors.textSecondary),
                        )
                      else ...[
                        for (final t in plan.targets)
                          _PlanTargetCard(
                              target: t, fmtLocal: _fmtLocal),
                        if (plan.notes.isNotEmpty)
                          Padding(
                            padding: const EdgeInsets.only(top: 4),
                            child: Text(
                              plan.notes.join(' '),
                              style: theme.textTheme.bodySmall?.copyWith(
                                  color: AraColors.textSecondary),
                            ),
                          ),
                      ],
                    ],
                  ],
                ),
              ),
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}

class _PlanTargetCard extends StatelessWidget {
  final SessionPlanTarget target;
  final String Function(DateTime) fmtLocal;
  const _PlanTargetCard({required this.target, required this.fmtLocal});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final o = target.object;
    final subs = target.subCount != null && target.subSeconds != null
        ? '≈ ${target.subCount} subs × ${target.subSeconds!.round()} s'
        : null;
    // SHO planning helper: with a narrowband recommendation, the slice is
    // typically split across the three lines.
    final sho = o.filterAdvice == TonightFilterAdvice.narrowband &&
            target.subCount != null
        ? ' (SHO ≈ ${(target.subCount! / 3).floor()} each)'
        : '';
    return Card(
      color: AraColors.bgPanelAlt,
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(o.name, style: theme.textTheme.bodyMedium),
            const SizedBox(height: 4),
            Text(
              '${fmtLocal(target.startUtc)}–${fmtLocal(target.endUtc)} · '
              '${target.hours.toStringAsFixed(1)} h'
              '${o.score != null ? ' · score ${o.score!.round()}' : ''}',
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
            if (subs != null) ...[
              const SizedBox(height: 4),
              Text('$subs$sho', style: theme.textTheme.bodySmall),
            ],
          ],
        ),
      ),
    );
  }
}
