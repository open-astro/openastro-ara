import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
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
  bool _planning = false;

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

  Future<void> _makePlan() async {
    final start = _nextLocal(_start);
    final end = _nextLocal(_end, after: start);
    setState(() {
      _planning = true;
      _plan = null;
    });
    // Rank around the WINDOW's midpoint, not "now": the base list centres its
    // ±12 h dark-window scan on the current instant, so a plan made in the
    // afternoon would intersect tonight's window with LAST night's windows
    // and find nothing shootable. Rounded to 5 min for a stable family key.
    final midMs = (start.millisecondsSinceEpoch +
            end.millisecondsSinceEpoch) ~/ 2;
    final mid = DateTime.fromMillisecondsSinceEpoch(
        midMs - midMs % (5 * 60 * 1000),
        isUtc: false);
    final List<TonightSkyObject> ranked;
    // Hold a subscription for the await: a bare read of an autoDispose family
    // doesn't keep it alive, so the provider was disposed mid-computation and
    // the future errored ("could not rank" on every plan).
    final keepAlive =
        ref.listenManual(tonightSkyAtProvider(mid.toUtc()), (_, _) {});
    try {
      ranked = await ref.read(tonightSkyAtProvider(mid.toUtc()).future);
    } catch (e) {
      debugPrint('[session-plan] ranking failed: $e');
      if (!mounted) return;
      setState(() {
        _planning = false;
        _plan = const SessionPlan(targets: [], plannedHours: 0, notes: [
          'Could not rank the sky for that window — try again.'
        ]);
      });
      return;
    } finally {
      keepAlive.close();
    }
    if (!mounted) return;
    // Charge the night's REAL overheads from the user's own settings, so the
    // sub counts describe a session with dithering, guiding settles, plate
    // solving and autofocus in it — not an idealized shutter-open number.
    final phd2 = ref.read(phd2SettingsProvider);
    final af = ref.read(autofocusSettingsProvider);
    final overheads = SessionOverheads(
      ditherEnabled: phd2.ditherEnabled,
      ditherEveryNFrames: phd2.ditherEveryNFrames,
      ditherSettleSec: phd2.settleTimeSec.toDouble(),
      autofocusEveryHours: af.everyNHours.toDouble(),
    );
    setState(() {
      _planning = false;
      _plan = planImagingSession(
        ranked: ranked,
        windowStartUtc: start.toUtc(),
        windowEndUtc: end.toUtc(),
        targetCount: _targetCount,
        overheads: overheads,
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
                'the best target(s) from tonight\'s list. Sub counts use the '
                'optimal-exposure criterion and charge your real overheads — '
                'slew, plate solve, focus, dither settles from your PHD2 '
                'settings, and periodic autofocus.',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AraColors.textSecondary),
              ),
              const SizedBox(height: 16),
              Text('Window',
                  style: theme.textTheme.labelSmall
                      ?.copyWith(color: AraColors.textSecondary)),
              const SizedBox(height: 6),
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
              Text('Targets',
                  style: theme.textTheme.labelSmall
                      ?.copyWith(color: AraColors.textSecondary)),
              const SizedBox(height: 6),
              SegmentedButton<int>(
                segments: const [
                  ButtonSegment(value: 1, label: Text('1')),
                  ButtonSegment(value: 2, label: Text('2')),
                  ButtonSegment(value: 3, label: Text('3')),
                  ButtonSegment(value: 4, label: Text('4')),
                ],
                selected: {_targetCount},
                onSelectionChanged: (s) => setState(() {
                  _targetCount = s.first;
                  _plan = null;
                }),
              ),
              const SizedBox(height: 16),
              Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FilledButton.icon(
                    icon: _planning
                        ? const SizedBox(
                            width: 14,
                            height: 14,
                            child:
                                CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.auto_awesome, size: 16),
                    label: Text(_planning ? 'Planning…' : 'Plan it'),
                    onPressed: _planning ? null : _makePlan,
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
                        _PlanTargetCard(target: t, fmtLocal: _fmtLocal),
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
