import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/settings/autofocus_settings_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../state/sky_atlas/session_plan_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/mosaic_geometry.dart';
import '../../util/session_planner.dart';
import '../../util/tonight_sky_local.dart' show opticsFovArcmin;
import 'target_preview.dart';

/// §36.8 "What-if run" — plan an imaging session: the user gives the window
/// they can actually shoot (say 22:00–01:00) and how many targets they want,
/// and the planner allocates the window to the best object(s) from tonight's
/// ranked list, with Glover-optimal sub counts per slice. Replaces the old
/// what-if optics dialog (trying a different rig is what launchpad profiles
/// are for; planning tonight's run is what nobody else does for you).
///
/// Each planned target is actionable, not just a card: swap it for another
/// candidate that fits the same slot, show it on the planetarium (the dialog
/// closes so the atlas is visible; the plan is kept in [sessionPlanProvider]
/// and comes back on reopen), or add it to a run sized to its slice. "Add all"
/// builds one multi-target sequence in slot order.
class SessionPlanDialog extends ConsumerStatefulWidget {
  const SessionPlanDialog({super.key});

  @override
  ConsumerState<SessionPlanDialog> createState() => _SessionPlanDialogState();
}

class _SessionPlanDialogState extends ConsumerState<SessionPlanDialog> {
  bool _planning = false;
  bool _adding = false;

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
    final s = ref.read(sessionPlanProvider);
    final start = _nextLocal(s.start);
    final end = _nextLocal(s.end, after: start);
    setState(() => _planning = true);
    // Rank around the WINDOW's midpoint, not "now": the base list centres its
    // ±12 h dark-window scan on the current instant, so a plan made in the
    // afternoon would intersect tonight's window with LAST night's windows
    // and find nothing shootable. Rounded to 5 min for a stable family key.
    final midMs =
        (start.millisecondsSinceEpoch + end.millisecondsSinceEpoch) ~/ 2;
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
      setState(() => _planning = false);
      ref.read(sessionPlanProvider.notifier).setPlan(
            const SessionPlan(targets: [], plannedHours: 0, notes: [
              'Could not rank the sky for that window — try again.'
            ]),
            ranked: const [],
            overheads: const SessionOverheads(),
          );
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
    setState(() => _planning = false);
    ref.read(sessionPlanProvider.notifier).setPlan(
          planImagingSession(
            ranked: ranked,
            windowStartUtc: start.toUtc(),
            windowEndUtc: end.toUtc(),
            targetCount: s.targetCount,
            overheads: overheads,
          ),
          ranked: ranked,
          overheads: overheads,
        );
  }

  Future<void> _pick(bool isStart) async {
    final s = ref.read(sessionPlanProvider);
    final picked = await showTimePicker(
      context: context,
      initialTime: isStart ? s.start : s.end,
    );
    if (picked == null || !mounted) return;
    final n = ref.read(sessionPlanProvider.notifier);
    if (isStart) {
      n.setStart(picked);
    } else {
      n.setEnd(picked);
    }
  }

  /// Frame the target on the planetarium. The dialog is modal, so it closes
  /// — the plan lives in the provider and is still there on reopen.
  void _showOnAtlas(SessionPlanTarget t) {
    final o = t.object;
    final aim = t.aim; // the object, or where the user dragged the frame
    ref.read(selectedTonightObjectProvider.notifier).select(o.id);
    ref.read(planetariumCommandProvider.notifier).send({
      'type': 'goto',
      'ra': aim.raDeg,
      'dec': aim.decDeg,
      'name': o.name,
      'frame': true,
      // The slot's rotation + grid land on the framing box — ALWAYS sent,
      // 0° / 1×1 included: the page only ever assigns these, so a previous
      // slot's 90° 2×2 would otherwise stay on the box for this one and
      // Create Run there would build a 4-panel run (review #1105).
      'rot': t.positionAngleDeg ?? 0,
      'cols': t.mosaic.cols,
      'rows': t.mosaic.rows,
      'overlap': t.mosaic.overlapPct,
      // The point of "show" is to SEE it: switch the DSS2 photo layer on so
      // the framed field is the real sky, not a hint circle on a star map.
      'dss': true,
    });
    Navigator.of(context).pop();
  }

  /// Add one slice to a run, sized to ITS hours (not the whole dark window).
  /// The first add creates a sequence and selects it; later adds append to
  /// it, so adding the plan's targets in order builds one night plan.
  Future<bool> _addOne(SessionPlanTarget t, ScaffoldMessengerState messenger,
      {bool quiet = false}) async {
    final o = t.object;
    // A mosaic becomes one target block per panel, on the same grid the
    // preview drew (mosaic_geometry ports the overlay's math). Each panel
    // gets its share of the slot so the run finishes inside it.
    final fov = opticsFovArcmin(ref.read(opticsSettingsProvider));
    final aim = t.aim;
    final panels = t.mosaic.isMosaic && fov != null
        ? mosaicPanelCentres(
            raDeg: aim.raDeg,
            decDeg: aim.decDeg,
            fovArcmin: fov,
            g: t.mosaic,
            rotationDeg: t.positionAngleDeg ?? 0,
          )
        : const <({double raDeg, double decDeg})>[];
    ImagingRunResult? result;
    try {
      result = await createImagingRun(
        ref,
        raDeg: aim.raDeg,
        decDeg: aim.decDeg,
        targetName: o.name,
        remainingDarkHours:
            panels.isEmpty ? t.hours : t.hours / panels.length,
        mosaicPanels: panels,
        // Same rule as the framing overlay: a dialled angle upgrades the
        // slew to Center and Rotate; not set (or an untouched 0) stays a
        // plain slew so the run never demands a plate solver by accident.
        // (A mosaic always carries the angle, 0° included — its panels only
        // tile when each slew is plate-solved to the drawn grid; the run
        // builder applies that rule when panels are present.)
        positionAngleDeg: panels.isNotEmpty
            ? (t.positionAngleDeg ?? 0)
            : (t.positionAngleDeg ?? 0) != 0
                ? t.positionAngleDeg
                : null,
        jumpToRun: false,
      );
    } catch (e, st) {
      debugPrint('[session-plan] create-run failed: $e\n$st');
      showImagingRunFeedback(messenger, targetName: o.name, failed: true);
      return false;
    }
    if (result?.cancelled ?? false) return false;
    if (!quiet) {
      showImagingRunFeedback(messenger, targetName: o.name, result: result);
    }
    return true;
  }

  /// The per-card add. Same busy latch as [_addAll]: a create awaits the
  /// plan chooser and the daemon (or disk), so a double-tap on the button
  /// would otherwise start two runs for one target.
  Future<void> _addSingle(SessionPlanTarget t) async {
    if (_adding) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _adding = true);
    try {
      await _addOne(t, messenger);
    } finally {
      if (mounted) setState(() => _adding = false);
    }
  }

  Future<void> _addAll(SessionPlan plan) async {
    if (_adding) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _adding = true);
    var added = 0;
    try {
      for (final t in plan.targets) {
        if (!await _addOne(t, messenger, quiet: true)) break;
        added++;
      }
    } finally {
      if (mounted) setState(() => _adding = false);
    }
    if (added > 0) {
      messenger.showSnackBar(SnackBar(
        content: Text(added == plan.targets.length
            ? 'Added all ${plan.targets.length} planned targets to the run.'
            : 'Added $added of ${plan.targets.length} planned targets.'),
      ));
    }
  }

  String _fmtLocal(DateTime utc) {
    final l = utc.toLocal();
    return '${l.hour.toString().padLeft(2, '0')}:'
        '${l.minute.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final s = ref.watch(sessionPlanProvider);
    final plan = s.plan;
    final frameFov = opticsFovArcmin(ref.watch(opticsSettingsProvider));

    return AlertDialog(
      backgroundColor: AraColors.bgPanel,
      title: const Text('Plan tonight\'s session'),
      content: SizedBox(
        width: 560,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Tell me when you can image and I\'ll allocate the window to '
                'the best target(s) from tonight\'s list. Sub counts use the '
                'optimal-exposure criterion and charge your real overheads — '
                'slew, plate solve, focus, dither settles from your OpenAstro Guider '
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
                      label: Text('From ${s.start.format(context)}'),
                      onPressed: () => _pick(true),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: OutlinedButton.icon(
                      icon: const Icon(Icons.schedule, size: 16),
                      label: Text('To ${s.end.format(context)}'),
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
                selected: {s.targetCount},
                onSelectionChanged: (sel) => ref
                    .read(sessionPlanProvider.notifier)
                    .setTargetCount(sel.first),
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
                    label: Text(_planning
                        ? 'Planning…'
                        : plan == null
                            ? 'Plan it'
                            : 'Plan it again'),
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
                      for (var i = 0; i < plan.targets.length; i++)
                        _PlanTargetCard(
                          target: plan.targets[i],
                          fmtLocal: _fmtLocal,
                          alternatives: swapCandidates(
                            ranked: s.ranked,
                            plan: plan,
                            slice: plan.targets[i],
                          ),
                          busy: _adding,
                          frameFovArcmin: frameFov,
                          onRotate: (deg) => ref
                              .read(sessionPlanProvider.notifier)
                              .setRotation(i, deg),
                          overheads: s.overheads,
                          onMosaic: (g) => ref
                              .read(sessionPlanProvider.notifier)
                              .setMosaic(i, g),
                          onAim: (off) => ref
                              .read(sessionPlanProvider.notifier)
                              .setAim(i, off),
                          onSwap: (o) => ref
                              .read(sessionPlanProvider.notifier)
                              .swap(i, o),
                          onShow: () => _showOnAtlas(plan.targets[i]),
                          onAdd: () => _addSingle(plan.targets[i]),
                        ),
                      if (plan.notes.isNotEmpty)
                        Padding(
                          padding: const EdgeInsets.only(top: 4),
                          child: Text(
                            plan.notes.join(' '),
                            style: theme.textTheme.bodySmall?.copyWith(
                                color: AraColors.textSecondary),
                          ),
                        ),
                      if (plan.targets.length > 1) ...[
                        const SizedBox(height: 12),
                        FilledButton.tonalIcon(
                          icon: _adding
                              ? const SizedBox(
                                  width: 14,
                                  height: 14,
                                  child: CircularProgressIndicator(
                                      strokeWidth: 2))
                              : const Icon(Icons.playlist_add, size: 16),
                          label: Text(_adding
                              ? 'Adding…'
                              : 'Add all ${plan.targets.length} to a run'),
                          onPressed: _adding ? null : () => _addAll(plan),
                        ),
                      ],
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
  final List<TonightSkyObject> alternatives;
  final bool busy;
  final (double, double)? frameFovArcmin;
  final ValueChanged<double?> onRotate;
  final SessionOverheads overheads;
  final ValueChanged<MosaicGrid> onMosaic;
  final ValueChanged<(double, double)> onAim;
  final ValueChanged<TonightSkyObject> onSwap;
  final VoidCallback onShow;
  final VoidCallback onAdd;
  const _PlanTargetCard({
    required this.target,
    required this.fmtLocal,
    required this.alternatives,
    required this.busy,
    required this.frameFovArcmin,
    required this.onRotate,
    required this.overheads,
    required this.onMosaic,
    required this.onAim,
    required this.onSwap,
    required this.onShow,
    required this.onAdd,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final o = target.object;
    final m = target.mosaic;
    final perPanel = target.subsPerPanel(overheads);
    final subs = perPanel != null && target.subSeconds != null
        ? m.isMosaic
            ? '≈ $perPanel subs × ${target.subSeconds!.round()} s per panel '
                '(${m.panelCount} panels)'
            : '≈ $perPanel subs × ${target.subSeconds!.round()} s'
        : null;
    // SHO planning helper: with a narrowband recommendation, the slice is
    // typically split across the three lines.
    final sho = o.filterAdvice == TonightFilterAdvice.narrowband &&
            perPanel != null
        ? ' (SHO ≈ ${(perPanel / 3).floor()} each)'
        : '';
    final suggested =
        frameFovArcmin == null ? null : suggestMosaic(o.sizeMajArcmin, frameFovArcmin!);
    return Card(
      color: AraColors.bgPanelAlt,
      margin: const EdgeInsets.only(bottom: 8),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 4, 4),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // What it looks like: a full-width DSS2 cutout with the camera
            // frame drawn on it at the dialled rotation. Tap to enlarge.
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: TargetPreview(
                object: o,
                frameFovArcmin: frameFovArcmin,
                rotationDeg: target.positionAngleDeg ?? 0,
                mosaic: m,
                aimOffsetArcmin: target.aimOffsetArcmin,
                onAim: onAim,
              ),
            ),
            const SizedBox(height: 8),
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
            if (target.isAimed)
              Row(
                children: [
                  Expanded(
                    child: Text(
                      'Aimed ${_fmtOffset(target.aimOffsetArcmin)} from the '
                      'catalogue centre',
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.primary),
                    ),
                  ),
                  TextButton(
                    style: TextButton.styleFrom(
                        visualDensity: VisualDensity.compact,
                        foregroundColor: AraColors.textSecondary),
                    onPressed: busy ? null : () => onAim((0.0, 0.0)),
                    child: const Text('Recentre'),
                  ),
                ],
              ),
            // Camera rotation for this slot — the box on the preview turns
            // with it so the layout is judged on the real field. Only drawn
            // when the optical train is configured enough to know the FOV.
            if (frameFovArcmin != null)
              Row(
                children: [
                  Tooltip(
                    message: 'Camera rotation for this target',
                    child: Icon(Icons.rotate_right,
                        size: 16,
                        color: target.positionAngleDeg == null
                            ? AraColors.textSecondary
                            : theme.colorScheme.primary),
                  ),
                  Expanded(
                    child: SliderTheme(
                      data: SliderTheme.of(context).copyWith(
                        trackHeight: 3,
                        activeTrackColor: theme.colorScheme.primary,
                        inactiveTrackColor: AraColors.border,
                        thumbShape:
                            const RoundSliderThumbShape(enabledThumbRadius: 6),
                        overlayShape:
                            const RoundSliderOverlayShape(overlayRadius: 12),
                      ),
                      child: Slider(
                        min: 0,
                        max: 359,
                        divisions: 359,
                        value: (target.positionAngleDeg ?? 0).clamp(0, 359),
                        label: '${(target.positionAngleDeg ?? 0).round()}°',
                        onChanged: busy ? null : onRotate,
                      ),
                    ),
                  ),
                  SizedBox(
                    width: 36,
                    child: Text(
                      '${(target.positionAngleDeg ?? 0).round()}°',
                      textAlign: TextAlign.right,
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                  IconButton(
                    iconSize: 16,
                    visualDensity: VisualDensity.compact,
                    tooltip: 'Reset rotation',
                    icon: const Icon(Icons.restart_alt),
                    onPressed: busy || target.positionAngleDeg == null
                        ? null
                        : () => onRotate(null),
                  ),
                ],
              ),
            // Mosaic grid for this slot: columns × rows with overlap, the
            // same bounds as the planetarium's Frame panel. "Suggest" picks
            // the smallest grid that covers an object that overflows one
            // frame; the preview redraws the grid and the field widens to
            // hold it.
            if (frameFovArcmin != null)
              Wrap(
                crossAxisAlignment: WrapCrossAlignment.center,
                spacing: 4,
                runSpacing: 2,
                children: [
                  Tooltip(
                    message: 'Mosaic grid for this target',
                    child: Icon(Icons.grid_on,
                        size: 16,
                        color: m.isMosaic
                            ? theme.colorScheme.primary
                            : AraColors.textSecondary),
                  ),
                  const SizedBox(width: 6),
                  _Stepper(
                    label: 'cols',
                    value: m.cols,
                    min: 1,
                    max: 8,
                    enabled: !busy,
                    onChanged: (v) => onMosaic((cols: v, rows: m.rows, overlapPct: m.overlapPct)),
                  ),
                  const SizedBox(width: 6),
                  _Stepper(
                    label: 'rows',
                    value: m.rows,
                    min: 1,
                    max: 8,
                    enabled: !busy,
                    onChanged: (v) => onMosaic((cols: m.cols, rows: v, overlapPct: m.overlapPct)),
                  ),
                  if (m.isMosaic) ...[
                    const SizedBox(width: 6),
                    _Stepper(
                      label: 'overlap %',
                      value: m.overlapPct,
                      min: 0,
                      max: 50,
                      step: 5,
                      enabled: !busy,
                      onChanged: (v) => onMosaic((cols: m.cols, rows: m.rows, overlapPct: v)),
                    ),
                  ],
                  if (suggested != null && suggested != m)
                    TextButton(
                      style: TextButton.styleFrom(
                          visualDensity: VisualDensity.compact),
                      onPressed: busy ? null : () => onMosaic(suggested),
                      child: Text(
                          'Suggest ${suggested.cols}×${suggested.rows}'),
                    )
                  else if (m.isMosaic)
                    TextButton(
                      style: TextButton.styleFrom(
                          visualDensity: VisualDensity.compact,
                          foregroundColor: AraColors.textSecondary),
                      onPressed: busy ? null : () => onMosaic(singleFrame),
                      child: const Text('Single frame'),
                    ),
                ],
              ),
            const SizedBox(height: 2),
            Row(
              children: [
                // Swap: the ranked alternatives that fit this slot, best
                // first, capped so the menu stays a menu.
                PopupMenuButton<TonightSkyObject>(
                  tooltip: alternatives.isEmpty
                      ? 'Nothing else on tonight\'s list fits this slot'
                      : 'Swap for another target that fits this slot',
                  enabled: alternatives.isNotEmpty && !busy,
                  onSelected: onSwap,
                  itemBuilder: (_) => [
                    for (final a in alternatives.take(12))
                      PopupMenuItem(
                        value: a,
                        child: Text(
                          '${a.name}'
                          '${a.score != null ? '  ·  ${a.score!.round()}' : ''}',
                        ),
                      ),
                  ],
                  child: Padding(
                    padding: const EdgeInsets.symmetric(
                        horizontal: 8, vertical: 6),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(Icons.swap_horiz,
                            size: 16,
                            color: alternatives.isEmpty
                                ? AraColors.textSecondary
                                : theme.colorScheme.primary),
                        const SizedBox(width: 4),
                        Text('Swap',
                            style: theme.textTheme.labelLarge?.copyWith(
                                color: alternatives.isEmpty
                                    ? AraColors.textSecondary
                                    : theme.colorScheme.primary)),
                      ],
                    ),
                  ),
                ),
                IconButton(
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  tooltip: 'Show on the planetarium',
                  icon: const Icon(Icons.my_location),
                  onPressed: busy ? null : onShow,
                ),
                IconButton(
                  iconSize: 18,
                  visualDensity: VisualDensity.compact,
                  tooltip: 'Add to a run (${target.hours.toStringAsFixed(1)} h)',
                  icon: const Icon(Icons.playlist_add),
                  onPressed: busy ? null : onAdd,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// A compact − value + stepper for the mosaic controls.
class _Stepper extends StatelessWidget {
  final String label;
  final int value;
  final int min;
  final int max;
  final int step;
  final bool enabled;
  final ValueChanged<int> onChanged;
  const _Stepper({
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    this.step = 1,
    required this.enabled,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(label,
            style: theme.textTheme.bodySmall
                ?.copyWith(color: AraColors.textSecondary)),
        IconButton(
          iconSize: 14,
          padding: EdgeInsets.zero,
          constraints: const BoxConstraints.tightFor(width: 26, height: 26),
          tooltip: 'Fewer $label',
          icon: const Icon(Icons.remove),
          onPressed: enabled && value - step >= min
              ? () => onChanged(value - step)
              : null,
        ),
        Text('$value', style: theme.textTheme.bodySmall),
        IconButton(
          iconSize: 14,
          padding: EdgeInsets.zero,
          constraints: const BoxConstraints.tightFor(width: 26, height: 26),
          tooltip: 'More $label',
          icon: const Icon(Icons.add),
          onPressed: enabled && value + step <= max
              ? () => onChanged(value + step)
              : null,
        ),
      ],
    );
  }
}

/// "12′ N · 30′ W" — the aim offset in the words an imager uses.
String _fmtOffset((double, double) off) {
  final parts = <String>[];
  if (off.$2.abs() >= 0.5) {
    parts.add('${off.$2.abs().round()}′ ${off.$2 > 0 ? 'N' : 'S'}');
  }
  if (off.$1.abs() >= 0.5) {
    parts.add('${off.$1.abs().round()}′ ${off.$1 > 0 ? 'E' : 'W'}');
  }
  return parts.isEmpty ? 'under 1′' : parts.join(' · ');
}
