import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart' show SequenceClient;
import '../../state/planning/sky_model_calibration_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/sequencer/target_plan_dialog.dart'
    show planOptionsAvailable, suggestedSubSeconds;
import 'simple_preview_panel.dart'
    show
        SimplePresetTile,
        SimpleShutterButton,
        exposurePresets,
        formatExposure,
        kSimpleText;
import 'simple_shell.dart' show SimplePanel;
import 'simple_target_panel.dart' show simpleTargetStatus;

/// The first plan for a filter set: LRGB when the set has any of it, else the
/// narrowband filters, else every filter; one row with no filter name when
/// there is no choice to make (OSC). Tonight's frames split evenly. Pure —
/// unit-tested.
// Subs start at the profile's default exposure; the sheet offers the
// suggested ones (simpleRunSuggestion).
// shortcut: frames don't fit the dark window; reuse target_plan_dialog's
// _steps split when the sheet should be smarter.
List<FilterPlanStep> simpleRunDefaultPlan(
  List<PlanningFilter> filters,
  double exposureSeconds,
) {
  if (!planOptionsAvailable(filters)) {
    return [
      FilterPlanStep(
        filterName: '',
        exposureSeconds: exposureSeconds,
        frameCount: 60,
      ),
    ];
  }
  List<PlanningFilter> of(List<FilterKind> kinds) => [
    for (final k in kinds) ...filters.where((f) => f.kind == k).take(1),
  ];
  var picked = of([FilterKind.l, FilterKind.r, FilterKind.g, FilterKind.b]);
  if (picked.isEmpty) {
    picked = of([FilterKind.ha, FilterKind.oiii, FilterKind.sii]);
  }
  if (picked.isEmpty) picked = filters;
  return [
    for (final f in picked)
      FilterPlanStep(
        filterName: f.name,
        exposureSeconds: exposureSeconds,
        frameCount: math.max(12, 60 ~/ picked.length),
      ),
  ];
}

/// Frames done per plan row, the loops running in plan order. Pure —
/// unit-tested.
// shortcut: counts the run's filed frames against the plan in order, so a
// skipped or failed frame shifts the split; read per-loop counts from the
// run tree if that matters.
List<int> simpleRunProgress(List<FilterPlanStep> steps, int captured) {
  var left = captured;
  return [
    for (final s in steps)
      () {
        final done = math.min(left, s.frameCount);
        left -= done;
        return done;
      }(),
  ];
}

/// The plan's one-line hint: the suggested sub length of every row that
/// differs from it, rows sharing a length together ("L 120 s · GB 180 s",
/// "Ha/OIII 300 s"); null when every row already matches. Pure —
/// unit-tested.
String? simpleRunSuggestion(List<FilterPlanStep> steps, List<double> suggested) {
  final byLength = <double, List<String>>{};
  for (var i = 0; i < steps.length; i++) {
    if (suggested[i] == steps[i].exposureSeconds) continue;
    (byLength[suggested[i]] ??= []).add(steps[i].filterName);
  }
  if (byLength.isEmpty) return null;
  String len(double s) => formatExposure(Duration(milliseconds: (s * 1000).round()));
  return byLength.entries.map((e) {
    final names = e.value.where((n) => n.isNotEmpty).toList();
    final joined = names.join(names.every((n) => n.length == 1) ? '' : '/');
    return joined.isEmpty ? len(e.key) : '$joined ${len(e.key)}';
  }).join(' · ');
}

int _total(List<FilterPlanStep> steps) =>
    steps.fold(0, (n, s) => n + s.frameCount);

/// "6 h 20 min", "45 min". Pure — unit-tested.
String simpleRunDuration(double seconds) {
  final min = (seconds / 60).round();
  return min >= 60 ? '${min ~/ 60} h ${min % 60} min' : '$min min';
}

String _hhmm(DateTime t) =>
    '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}';

/// The plan the sheet edits: one row per filter.
class SimpleRunPlanNotifier extends Notifier<List<FilterPlanStep>> {
  @override
  List<FilterPlanStep> build() => simpleRunDefaultPlan(
    ref.watch(filterSetProvider).filters,
    ref.watch(imagingDefaultsProvider).defaultExposure.inMilliseconds / 1000,
  );

  void _set(int i, {double? exposureSeconds, int? frameCount}) => state = [
    for (var j = 0; j < state.length; j++)
      j == i
          ? FilterPlanStep(
              filterName: state[j].filterName,
              exposureSeconds: exposureSeconds ?? state[j].exposureSeconds,
              frameCount: frameCount ?? state[j].frameCount,
            )
          : state[j],
  ];

  void setExposure(int i, double seconds) => _set(i, exposureSeconds: seconds);
  void setFrames(int i, int count) => _set(i, frameCount: count);

  // The last row stays: a run needs something to take.
  void remove(int i) {
    if (state.length > 1) state = [...state]..removeAt(i);
  }

  void add(String filterName) => state = [
    ...state,
    FilterPlanStep(
      filterName: filterName,
      exposureSeconds: state.last.exposureSeconds,
      frameCount: state.last.frameCount,
    ),
  ];
}

final simpleRunPlanProvider =
    NotifierProvider<SimpleRunPlanNotifier, List<FilterPlanStep>>(
      SimpleRunPlanNotifier.new,
    );

/// The run Simple Mode started (its plan, to split the progress per filter),
/// whether the shutter is still creating it, why it failed, and the red
/// square's question.
class SimpleRun {
  const SimpleRun({
    this.sequenceId,
    this.targetName,
    this.steps = const [],
    this.starting = false,
    this.error,
    this.asking = false,
    this.stopAfter,
  });

  final String? sequenceId;
  final String? targetName;
  final List<FilterPlanStep> steps;
  final bool starting;
  final String? error;

  /// The red square was tapped: the progress panel asks before stopping.
  final bool asking;

  /// "Stop after this frame": the frame count to stop past.
  final int? stopAfter;

  SimpleRun copyWith({bool? asking, int? stopAfter, String? error}) =>
      SimpleRun(
        sequenceId: sequenceId,
        targetName: targetName,
        steps: steps,
        asking: asking ?? this.asking,
        stopAfter: stopAfter ?? this.stopAfter,
        error: error,
      );
}

class SimpleRunNotifier extends Notifier<SimpleRun> {
  @override
  SimpleRun build() {
    // The daemon's stop cancels the frame in hand, so "after this frame"
    // waits here for the next filed frame and stops then.
    // shortcut: only while this app runs; a daemon-side stop-after-frame
    // would survive the iPad sleeping.
    ref.listen(sequenceRunStateProvider, (_, next) {
      final after = state.stopAfter, info = next.value;
      if (after != null && info != null && info.framesCaptured > after) {
        stopNow();
      }
    });
    return const SimpleRun();
  }

  /// The shutter: create the run from the plan on the target picked in
  /// Target, then start it. [ref] is the shutter's (the create reads the
  /// widget tree).
  Future<void> start(WidgetRef ref) async {
    final sel = ref.read(planetariumSelectionProvider);
    final steps = ref.read(simpleRunPlanProvider);
    if (sel == null || state.starting) return;
    state = const SimpleRun(starting: true);
    final plan = steps.first.filterName.isEmpty ? null : steps;
    try {
      final r = await createImagingRun(
        ref,
        raDeg: sel.raJ2000Hours * 15,
        decDeg: sel.decJ2000Deg,
        targetName: sel.name,
        jumpToRun: false,
        appendToOpen: false,
        filterPlan: plan,
        exposureSeconds: steps.first.exposureSeconds,
        frameCount: steps.first.frameCount,
      );
      if (r == null || r.draft) {
        state = const SimpleRun(
          error: "Couldn't reach the rig. Check the connection and try again.",
        );
        return;
      }
      final api = this.ref.read(sequenceApiProvider);
      if (api == null) return;
      await api.start(r.sequenceId);
      state = SimpleRun(
        sequenceId: r.sequenceId,
        targetName: sel.name,
        steps: steps,
      );
      await this.ref.read(sequenceRunStateProvider.notifier).refresh();
    } catch (e) {
      state = SimpleRun(error: _why(e, 'start imaging'));
    }
  }

  void ask() => state = state.copyWith(asking: true);
  void keepGoing() => state = state.copyWith(asking: false);

  void stopAfterThisFrame() {
    final info = ref.read(sequenceRunStateProvider).value;
    state = state.copyWith(asking: false, stopAfter: info?.framesCaptured ?? 0);
  }

  Future<void> stopNow() => _op((api, id) => api.stop(id), 'stop the run');
  Future<void> pause() => _op((api, id) => api.pause(id), 'pause');
  Future<void> resume() => _op((api, id) => api.resume(id), 'resume');

  /// Night done's New plan: back to the sheet.
  void newPlan() => state = const SimpleRun();

  Future<void> _op(
    Future<String> Function(SequenceClient api, String id) op,
    String action,
  ) async {
    final api = ref.read(sequenceApiProvider);
    final id = ref.read(selectedSequenceIdProvider);
    if (api == null || id == null) return;
    state = SimpleRun(
      sequenceId: state.sequenceId,
      targetName: state.targetName,
      steps: state.steps,
    );
    try {
      await op(api, id);
    } catch (e) {
      state = state.copyWith(error: _why(e, action));
    }
    await ref.read(sequenceRunStateProvider.notifier).refresh();
  }

  // The daemon's own words when it sent them.
  static String _why(Object e, String action) {
    if (e is DioException) {
      final data = e.response?.data;
      final detail = data is Map ? data['detail'] : null;
      if (detail is String && detail.trim().isNotEmpty) return detail.trim();
    }
    return friendlyError(e, action: action);
  }
}

final simpleRunProvider = NotifierProvider<SimpleRunNotifier, SimpleRun>(
  SimpleRunNotifier.new,
);

bool _paused(SequenceRunState? s) =>
    s == SequenceRunState.paused || s == SequenceRunState.pausedAwaitingUser;

/// Whether the run Simple Mode started has ended (Night done shows).
bool simpleRunDone(SimpleRun run, SequenceRunStateInfo? info) =>
    run.sequenceId != null &&
    info?.sequenceId == run.sequenceId &&
    !(info?.state?.isActive ?? true);

/// The rail chip in Run: "Plan", "23 / 100" while imaging, "Paused",
/// "Done". Pure — unit-tested.
String simpleRunSummary(SimpleRun run, SequenceRunStateInfo? info) {
  if (simpleRunDone(run, info)) return 'Done';
  if (info?.state?.isActive != true) return 'Plan';
  if (_paused(info!.state)) return 'Paused';
  final total = info.sequenceId == run.sequenceId ? _total(run.steps) : 0;
  return total > 0
      ? '${info.framesCaptured} / $total'
      : '${info.framesCaptured} frames';
}

/// The rail's shutter in Run: starts imaging the plan; while a run images,
/// its red square asks first (in the progress panel) whether to stop.
class SimpleRunShutter extends ConsumerWidget {
  const SimpleRunShutter({super.key, required this.onPanel});

  /// Opens (true) or closes the mode's panel: the sheet closes once the run
  /// starts and the progress panel opens to ask.
  final ValueChanged<bool> onPanel;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final info = ref.watch(sequenceRunStateProvider).value;
    final run = ref.watch(simpleRunProvider);
    final sel = ref.watch(planetariumSelectionProvider);
    final active = info?.state?.isActive ?? false;
    return SimpleShutterButton(
      running: active || run.starting,
      label: active ? 'Stop the run' : 'Start imaging',
      onPressed: active
          ? () {
              ref.read(simpleRunProvider.notifier).ask();
              onPanel(true);
            }
          : sel == null || run.starting
          ? null
          : () async {
              await ref.read(simpleRunProvider.notifier).start(ref);
              if (ref.read(simpleRunProvider).error == null) onPanel(false);
            },
    );
  }
}

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);
TextStyle get _head =>
    kSimpleText.copyWith(fontSize: 16, fontWeight: FontWeight.w600);

const _kFramePresets = [5, 10, 12, 15, 20, 30, 40, 60, 90, 120, 180, 240, 300];

/// Run's sheet, centred over the image: the plan for the target picked in
/// Target (a row per filter: sub length and frames), or why there's none;
/// once the run ends, Night done. The shutter starts imaging.
class SimpleRunSheet extends ConsumerStatefulWidget {
  const SimpleRunSheet({
    super.key,
    required this.onOpenTarget,
    required this.onLibrary,
  });

  final VoidCallback onOpenTarget;
  final VoidCallback onLibrary;

  @override
  ConsumerState<SimpleRunSheet> createState() => _SimpleRunSheetState();
}

class _SimpleRunSheetState extends ConsumerState<SimpleRunSheet> {
  @override
  void initState() {
    super.initState();
    // The rows start from the profile's filters and default exposure.
    hydratePlanningSettings(ProviderScope.containerOf(context, listen: false));
  }

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    return SimplePanel(
      padding: const EdgeInsets.all(18),
      child: SizedBox(
        width: 400,
        child: simpleRunDone(run, info)
            ? _NightDone(run: run, info: info!, onLibrary: widget.onLibrary)
            : _plan(run),
      ),
    );
  }

  Widget _plan(SimpleRun run) {
    final sel = ref.watch(planetariumSelectionProvider);
    if (sel == null) {
      return Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s8,
        children: [
          Text('No target yet', style: _head),
          Text(
            'Run images the target you pick in Target. Pick one there, go '
            'to it, then come back.',
            style: _dim,
          ),
          FilledButton(
            key: const Key('simple-run-open-target'),
            onPressed: widget.onOpenTarget,
            child: const Text('Open Target'),
          ),
        ],
      );
    }
    final steps = ref.watch(simpleRunPlanProvider);
    final plan = ref.read(simpleRunPlanProvider.notifier);
    final filters = ref.watch(filterSetProvider).filters;
    final unused = [
      for (final f in filters)
        if (steps.every((s) => s.filterName != f.name)) f.name,
    ];
    final seconds = steps.fold(
      0.0,
      (t, s) => t + s.exposureSeconds * s.frameCount,
    );
    final k =
        ref.watch(skyModelCalibrationProvider).value?.appliedK ?? const {};
    final suggested = [
      for (final s in steps)
        suggestedSubSeconds(
          ref,
          // The unnamed row (no filter choice) is the set's one filter.
          filterName: s.filterName.isEmpty
              ? filters.firstOrNull?.name ?? ''
              : s.filterName,
          raDeg: sel.raJ2000Hours * 15,
          decDeg: sel.decJ2000Deg,
          defaultExposure:
              ref.watch(imagingDefaultsProvider).defaultExposure.inMilliseconds /
              1000,
          skyCalibrationK: k,
        ),
    ];
    final hint = simpleRunSuggestion(steps, suggested);
    Duration dur(double s) => Duration(milliseconds: (s * 1000).round());
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text("Tonight's plan · ${sel.name}", style: _head),
        Text(
          '${simpleTargetStatus(sel)}. Autofocus, dither and flats follow '
          'your profile.',
          style: _dim,
        ),
        for (var i = 0; i < steps.length; i++)
          Row(
            spacing: AraSpace.s8,
            children: [
              SizedBox(
                width: 40,
                child: Text(
                  steps[i].filterName.isEmpty ? '—' : steps[i].filterName,
                  style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              Expanded(
                child: SimplePresetTile<Duration>(
                  key: Key('simple-run-exposure-$i'),
                  label: 'Exposure',
                  value: Duration(
                    milliseconds: (steps[i].exposureSeconds * 1000).round(),
                  ),
                  // The suggestion is listed, and marked, even off the presets.
                  options: {
                    ...exposurePresets(minSec: 1, maxSec: 0),
                    dur(suggested[i]),
                  }.toList()..sort(),
                  format: (d) => d == dur(suggested[i])
                      ? '${formatExposure(d)} · suggested'
                      : formatExposure(d),
                  onSelected: (d) =>
                      plan.setExposure(i, d.inMilliseconds / 1000),
                ),
              ),
              Expanded(
                child: SimplePresetTile<int>(
                  key: Key('simple-run-frames-$i'),
                  label: 'Frames',
                  value: steps[i].frameCount,
                  options: _kFramePresets,
                  format: (n) => '× $n',
                  onSelected: (n) => plan.setFrames(i, n),
                ),
              ),
              SizedBox(
                width: 44,
                child: steps.length > 1
                    ? IconButton(
                        tooltip: 'Remove ${steps[i].filterName}',
                        icon: const Icon(Icons.close, size: 18),
                        onPressed: () => plan.remove(i),
                      )
                    : null,
              ),
            ],
          ),
        if (steps.first.filterName.isNotEmpty && unused.isNotEmpty)
          Align(
            alignment: Alignment.centerLeft,
            child: PopupMenuButton<String>(
              tooltip: 'Add filter',
              onSelected: plan.add,
              itemBuilder: (_) => [
                for (final f in unused)
                  PopupMenuItem(
                    value: f,
                    height: 44,
                    child: Text(f, style: kSimpleText),
                  ),
              ],
              child: SizedBox(
                height: 44,
                child: Center(
                  child: Text(
                    'Add filter',
                    style: kSimpleText.copyWith(color: AraColors.accentInfo),
                  ),
                ),
              ),
            ),
          ),
        if (hint != null)
          Row(
            children: [
              Expanded(child: Text('Suggested: $hint', style: _dim)),
              TextButton(
                key: const Key('simple-run-use-suggested'),
                onPressed: () {
                  for (var i = 0; i < steps.length; i++) {
                    plan.setExposure(i, suggested[i]);
                  }
                },
                child: const Text('Use all'),
              ),
            ],
          ),
        Row(
          children: [
            Expanded(child: Text('${_total(steps)} frames', style: kSimpleText)),
            Text(simpleRunDuration(seconds), style: _dim),
          ],
        ),
        if (run.error != null)
          Text(
            run.error!,
            style: kSimpleText.copyWith(color: AraColors.accentError),
            maxLines: 4,
            overflow: TextOverflow.ellipsis,
          ),
        Container(
          height: 48,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            color: const Color(0x59404040),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Text(
            run.starting ? 'Creating the run…' : 'Tap the shutter to start imaging',
            style: _dim,
          ),
        ),
      ],
    );
  }
}

class _NightDone extends ConsumerWidget {
  const _NightDone({
    required this.run,
    required this.info,
    required this.onLibrary,
  });

  final SimpleRun run;
  final SequenceRunStateInfo info;
  final VoidCallback onLibrary;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final done = simpleRunProgress(run.steps, info.framesCaptured);
    final start = info.startedUtc?.toLocal(), end = info.completedUtc?.toLocal();
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text('Night done', style: _head),
        Text(
          [
            run.targetName ?? info.currentTargetName ?? '',
            if (start != null && end != null) '${_hhmm(start)} – ${_hhmm(end)}',
            if (info.state == SequenceRunState.stopped) 'stopped',
            if (info.state == SequenceRunState.failed) 'failed',
          ].where((s) => s.isNotEmpty).join(' · '),
          style: _dim,
        ),
        for (var i = 0; i < run.steps.length; i++)
          Row(
            children: [
              Expanded(
                child: Text(
                  run.steps[i].filterName.isEmpty
                      ? 'Frames'
                      : run.steps[i].filterName,
                  style: _dim,
                ),
              ),
              Text(
                '${done[i]} × ${formatExposure(Duration(milliseconds: (run.steps[i].exposureSeconds * 1000).round()))}',
                style: kSimpleText,
              ),
            ],
          ),
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: OutlinedButton(
                onPressed: onLibrary,
                child: const Text('Image Library'),
              ),
            ),
            Expanded(
              child: FilledButton(
                key: const Key('simple-run-new-plan'),
                onPressed: ref.read(simpleRunProvider.notifier).newPlan,
                child: const Text('New plan'),
              ),
            ),
          ],
        ),
      ],
    );
  }
}

/// Run's panel beside the rail while imaging: frames done of the plan, when
/// it ends, a bar per filter, Pause or Resume; after the red square, the
/// question whether to stop.
class SimpleRunPanel extends ConsumerWidget {
  const SimpleRunPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final runner = ref.read(simpleRunProvider.notifier);
    final ours = info?.sequenceId == run.sequenceId;
    final steps = ours ? run.steps : const <FilterPlanStep>[];
    final done = simpleRunProgress(steps, info?.framesCaptured ?? 0);
    final remaining = info?.estimatedRemainingSeconds;
    final paused = _paused(info?.state);
    return SimplePanel(
      padding: const EdgeInsets.all(AraSpace.s16),
      child: SizedBox(
        width: 248,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Text(
              'IMAGING ${(run.targetName ?? info?.currentTargetName ?? '').toUpperCase()}',
              style: AraText.caption,
            ),
            Text(
              simpleRunSummary(run, info),
              style: const TextStyle(
                fontSize: 30,
                fontWeight: FontWeight.w300,
                color: AraColors.textPrimary,
                fontFeatures: [FontFeature.tabularFigures()],
              ),
            ),
            if (remaining != null)
              Text(
                'Ends ${_hhmm(DateTime.now().add(Duration(seconds: remaining.round())))}'
                ' · ${simpleRunDuration(remaining)} left',
                style: _dim,
              ),
            for (var i = 0; i < steps.length; i++)
              Row(
                spacing: AraSpace.s8,
                children: [
                  SizedBox(
                    width: 24,
                    child: Text(
                      steps[i].filterName.isEmpty ? '—' : steps[i].filterName,
                      style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                      overflow: TextOverflow.clip,
                    ),
                  ),
                  Expanded(
                    child: LinearProgressIndicator(
                      value: done[i] / steps[i].frameCount,
                      minHeight: 6,
                      borderRadius: BorderRadius.circular(3),
                      color: done[i] == steps[i].frameCount
                          ? AraColors.accentConnected
                          : AraColors.selectionBg,
                      backgroundColor: const Color(0xCC404040),
                    ),
                  ),
                  SizedBox(
                    width: 64,
                    child: Text(
                      '${done[i]} / ${steps[i].frameCount}',
                      style: _dim,
                      textAlign: TextAlign.right,
                    ),
                  ),
                ],
              ),
            if (run.error != null)
              Text(
                run.error!,
                style: kSimpleText.copyWith(color: AraColors.accentError),
                maxLines: 4,
                overflow: TextOverflow.ellipsis,
              ),
            if (run.stopAfter != null)
              Text('Stopping after this frame…', style: _dim)
            else if (run.asking)
              Container(
                padding: const EdgeInsets.all(12),
                decoration: BoxDecoration(
                  color: const Color(0x993A2C0A),
                  border: Border.all(color: AraColors.accentBusy),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  spacing: AraSpace.s8,
                  children: [
                    Text(
                      'Stop the run?',
                      style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                    ),
                    Text(
                      '${info?.framesCaptured ?? 0} frames are saved. The mount '
                      'keeps tracking.',
                      style: _dim.copyWith(fontSize: 12),
                    ),
                    FilledButton(
                      key: const Key('simple-run-stop-after'),
                      onPressed: runner.stopAfterThisFrame,
                      child: const Text('Stop after this frame'),
                    ),
                    Row(
                      spacing: AraSpace.s8,
                      children: [
                        Expanded(
                          child: OutlinedButton(
                            key: const Key('simple-run-stop-now'),
                            style: OutlinedButton.styleFrom(
                              side: const BorderSide(
                                color: AraColors.accentError,
                              ),
                            ),
                            onPressed: runner.stopNow,
                            child: const Text('Stop now'),
                          ),
                        ),
                        Expanded(
                          child: OutlinedButton(
                            key: const Key('simple-run-keep-going'),
                            onPressed: runner.keepGoing,
                            child: const Text('Keep going'),
                          ),
                        ),
                      ],
                    ),
                  ],
                ),
              )
            else ...[
              FilledButton.tonal(
                key: const Key('simple-run-pause'),
                onPressed: paused ? runner.resume : runner.pause,
                child: Text(paused ? 'Resume' : 'Pause'),
              ),
              Text(
                'The red square stops the run.',
                style: _dim.copyWith(fontSize: 12),
              ),
            ],
          ],
        ),
      ),
    );
  }
}
