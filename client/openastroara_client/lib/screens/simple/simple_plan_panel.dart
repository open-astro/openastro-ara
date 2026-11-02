import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart' show CustomSemanticsAction;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart' show SequenceClient;
import '../../state/equipment/camera_state.dart';
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
import '../../widgets/emergency_stop_button.dart' show kEmergencyStopHold;
import 'simple_preview_panel.dart'
    show SimplePresetTile, exposurePresets, formatExposure, kSimpleText;
import 'simple_shell.dart' show SimplePanel, kSimpleInset, kSimpleRailWidth;

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

/// The rail chip in Plan: the plan's total imaging time, the created plan's
/// once there is one, else the sheet's rows; hours over minutes, as P.A.'s
/// chip takes two lines in 60 px. Pure — unit-tested.
String simpleRunChip(SimpleRun run, List<FilterPlanStep> rows) => simpleRunDuration(
  (run.steps.isEmpty ? rows : run.steps).fold(
    0.0,
    (t, s) => t + s.exposureSeconds * s.frameCount,
  ),
).replaceFirst(' h ', ' h\n');

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

/// Where the selection stands, in words: "62° up · max 74°", "Below your
/// horizon · rises 22:40", "Never rises here". Pure — unit-tested.
String simpleTargetStatus(PlanetariumSelection s) {
  if (s.neverRises) return 'Never rises here';
  if (s.belowHorizon) {
    final rise = s.rise;
    return rise == null
        ? 'Below your horizon'
        : 'Below your horizon · rises ${_hhmm(rise)}';
  }
  return '${(s.altDeg ?? 0).round()}° up · max ${s.maxAltDeg.round()}°';
}

/// The plan Simple Mode created on the rig (its rows, to split the progress
/// per filter), whether it has started, a call in flight, a pause asked for
/// but not reached yet, and why the last call failed.
class SimpleRun {
  const SimpleRun({
    this.sequenceId,
    this.targetName,
    this.steps = const [],
    this.started = false,
    this.busy = false,
    this.pausing = false,
    this.error,
  });

  final String? sequenceId;
  final String? targetName;
  final List<FilterPlanStep> steps;
  final bool started;
  final bool busy;

  /// Pause asked for: the daemon finishes the frame in hand first.
  final bool pausing;
  final String? error;

  SimpleRun copyWith({bool? started, bool? busy, bool? pausing, String? error}) =>
      SimpleRun(
        sequenceId: sequenceId,
        targetName: targetName,
        steps: steps,
        started: started ?? this.started,
        busy: busy ?? this.busy,
        pausing: pausing ?? this.pausing,
        error: error,
      );
}

/// What Plan's shutter, tracker and sheet show.
enum SimpleRunPhase { none, ready, imaging, pausing, paused, done }

bool _paused(SequenceRunState? s) =>
    s == SequenceRunState.paused || s == SequenceRunState.pausedAwaitingUser;

/// Pure — unit-tested. A run in progress wins (Simple Mode's or one adopted
/// from the daemon); a plan this app started ends once the daemon reports it
/// finished, stopped or failed (not merely idle, which a just-started run
/// still reads).
SimpleRunPhase simpleRunPhase(SimpleRun run, SequenceRunStateInfo? info) {
  if (info?.state?.isActive ?? false) {
    if (_paused(info!.state)) return SimpleRunPhase.paused;
    return run.pausing ? SimpleRunPhase.pausing : SimpleRunPhase.imaging;
  }
  if (run.sequenceId == null) return SimpleRunPhase.none;
  if (!run.started) return SimpleRunPhase.ready;
  final ended =
      info?.sequenceId == run.sequenceId &&
      const {
        SequenceRunState.completed,
        SequenceRunState.stopped,
        SequenceRunState.failed,
      }.contains(info?.state);
  return ended ? SimpleRunPhase.done : SimpleRunPhase.ready;
}

class SimpleRunNotifier extends Notifier<SimpleRun> {
  @override
  SimpleRun build() {
    // The pause lands once the frame in hand is filed.
    ref.listen(sequenceRunStateProvider, (_, next) {
      if (state.pausing && _paused(next.value?.state)) {
        state = state.copyWith(pausing: false);
      }
    });
    return const SimpleRun();
  }

  /// Create plan: the run for the picked target, on the rig but not started.
  /// Saving again replaces the plan (the old one is deleted). [ref] is the
  /// sheet's (the create reads the widget tree). True once created.
  Future<bool> create(WidgetRef ref) async {
    final sel = ref.read(planetariumSelectionProvider);
    final steps = ref.read(simpleRunPlanProvider);
    if (sel == null || state.busy) return false;
    final old = state.started ? null : state.sequenceId;
    state = state.copyWith(busy: true);
    try {
      final r = await createImagingRun(
        ref,
        raDeg: sel.raJ2000Hours * 15,
        decDeg: sel.decJ2000Deg,
        targetName: sel.name,
        jumpToRun: false,
        appendToOpen: false,
        filterPlan: steps.first.filterName.isEmpty ? null : steps,
        exposureSeconds: steps.first.exposureSeconds,
        frameCount: steps.first.frameCount,
      );
      if (r == null || r.draft) {
        state = state.copyWith(
          busy: false,
          error: "Couldn't reach the rig. Check the connection and try again.",
        );
        return false;
      }
      state = SimpleRun(
        sequenceId: r.sequenceId,
        targetName: sel.name,
        steps: steps,
      );
      if (old != null) {
        // shortcut: a failed delete leaves the old plan in the Sequence list;
        // update the plan in place if that clutters.
        try {
          await this.ref.read(sequenceApiProvider)?.deleteSequence(old);
        } catch (_) {}
      }
      return true;
    } catch (e) {
      state = state.copyWith(busy: false, error: _why(e, 'create the plan'));
      return false;
    }
  }

  /// The shutter's actions.
  Future<void> start() => _op(
    (api, id) => api.start(id),
    'start imaging',
    before: state.copyWith(started: true),
  );
  Future<void> pause() =>
      _op((api, id) => api.pause(id), 'pause', before: state.copyWith(pausing: true));
  Future<void> resume() => _op((api, id) => api.resume(id), 'resume');
  Future<void> stop() => _op((api, id) => api.stop(id), 'stop the run');

  /// Night done's New plan: back to the sheet.
  void newPlan() => state = const SimpleRun();

  Future<void> _op(
    Future<String> Function(SequenceClient api, String id) op,
    String action, {
    SimpleRun? before,
  }) async {
    final api = ref.read(sequenceApiProvider);
    final info = ref.read(sequenceRunStateProvider).value;
    // A run in progress (perhaps adopted from the daemon), else our plan.
    final id = (info?.state?.isActive ?? false)
        ? info!.sequenceId
        : state.sequenceId;
    if (api == null || id == null || id.isEmpty) return;
    final undo = state;
    state = (before ?? state).copyWith();
    try {
      await op(api, id);
    } catch (e) {
      state = undo.copyWith(error: _why(e, action));
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

/// The row the run is on (the first not yet full), by the plan order.
int _current(List<FilterPlanStep> steps, List<int> done) {
  for (var i = 0; i < steps.length; i++) {
    if (done[i] < steps[i].frameCount) return i;
  }
  return steps.length - 1;
}

String _sub(FilterPlanStep s) {
  final len = formatExposure(
    Duration(milliseconds: (s.exposureSeconds * 1000).round()),
  );
  return s.filterName.isEmpty ? len : '${s.filterName} · $len';
}

/// The tracker above the rail, for the plan's whole life: the target, frames
/// done of the plan, the filter and sub length in hand, and the time (total
/// before the start, left while imaging). Green ready, blue imaging, amber
/// paused, green done. Nothing without a plan. Tap: the sheet or the
/// progress panel.
class SimpleRunTracker extends ConsumerWidget {
  const SimpleRunTracker({super.key, required this.onTap});

  final VoidCallback onTap;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final phase = simpleRunPhase(run, info);
    if (phase == SimpleRunPhase.none) return const SizedBox.shrink();
    final ours = run.sequenceId != null && info?.sequenceId == run.sequenceId;
    final steps = run.steps;
    final captured = ours || !run.started ? (info?.framesCaptured ?? 0) : 0;
    final frames = phase == SimpleRunPhase.ready ? 0 : captured;
    final done = simpleRunProgress(steps, frames);
    final total = _total(steps);
    final known = steps.isNotEmpty && (ours || phase == SimpleRunPhase.ready);
    final left = known
        ? [
            for (var i = 0; i < steps.length; i++)
              (steps[i].frameCount - done[i]) * steps[i].exposureSeconds,
          ].fold(0.0, (a, b) => a + b)
        : null;
    final color = switch (phase) {
      SimpleRunPhase.ready || SimpleRunPhase.done => AraColors.accentConnected,
      SimpleRunPhase.paused => AraColors.accentBusy,
      _ => AraColors.selectionBg,
    };
    final last = switch (phase) {
      SimpleRunPhase.pausing => 'Pausing…',
      SimpleRunPhase.paused => 'Paused',
      SimpleRunPhase.done => 'Done',
      _ => (info?.estimatedRemainingSeconds ?? left) == null
          ? ''
          : simpleRunDuration(info?.estimatedRemainingSeconds ?? left!),
    };
    final small = kSimpleText.copyWith(
      fontSize: 11.5,
      color: AraColors.textSecondary,
    );
    return Semantics(
      button: true,
      label: 'Plan: ${run.targetName ?? info?.currentTargetName ?? ''}',
      child: GestureDetector(
        key: const Key('simple-run-tracker'),
        onTap: onTap,
        child: Container(
          width: kSimpleRailWidth,
          height: 100,
          clipBehavior: Clip.antiAlias,
          decoration: BoxDecoration(
            color: AraColors.overlayScrim,
            border: Border.all(color: color),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Stack(
            children: [
              // A long name or time shrinks to fit, clear of the border.
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6),
                child: Center(
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    spacing: 2,
                    children: [
                      for (final (text, style) in [
                        (
                          run.targetName ?? info?.currentTargetName ?? '',
                          kSimpleText.copyWith(fontWeight: FontWeight.w600),
                        ),
                        (known ? '$frames / $total' : '$captured', kSimpleText),
                        if (known) (_sub(steps[_current(steps, done)]), small),
                        (
                          last,
                          phase == SimpleRunPhase.paused
                              ? small.copyWith(color: AraColors.accentBusy)
                              : small,
                        ),
                      ])
                        FittedBox(
                          fit: BoxFit.scaleDown,
                          child: Text(text, style: style, maxLines: 1),
                        ),
                    ],
                  ),
                ),
              ),
              if (known && total > 0 && phase != SimpleRunPhase.ready)
                Align(
                  alignment: Alignment.bottomLeft,
                  child: FractionallySizedBox(
                    widthFactor: frames / total,
                    child: Container(height: 3, color: color),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// How long the shutter is held to stop a run (the Emergency Stop's hold).
const kSimpleRunStopHold = kEmergencyStopHold;

/// The rail's shutter in Plan. Plan ready: a play triangle in a green ring;
/// a tap starts it. Imaging: pause bars, with a blue ring filling as the
/// frame exposes; a tap pauses (after the frame in hand). Paused: a play
/// triangle in an amber ring; a tap resumes. While imaging or paused, a
/// 2 s hold stops the run (a red ring fills; letting go early cancels).
/// Screen readers get "Stop the run" as an action; keyboards ⌘. / Ctrl+.
class SimpleRunShutter extends ConsumerStatefulWidget {
  const SimpleRunShutter({super.key});

  @override
  ConsumerState<SimpleRunShutter> createState() => _SimpleRunShutterState();
}

class _SimpleRunShutterState extends ConsumerState<SimpleRunShutter>
    with SingleTickerProviderStateMixin {
  late final AnimationController _hold =
      AnimationController(vsync: this, duration: kSimpleRunStopHold)
        ..addListener(() => setState(() {}))
        ..addStatusListener((s) {
          if (s == AnimationStatus.completed) {
            _held = true;
            _hold.value = 0;
            ref.read(simpleRunProvider.notifier).stop();
          }
        });

  /// The hold reached the end: the release is not a tap.
  bool _held = false;
  bool _down = false;

  @override
  void dispose() {
    _hold.dispose();
    super.dispose();
  }

  bool _holdable(SimpleRunPhase p) =>
      p == SimpleRunPhase.imaging ||
      p == SimpleRunPhase.pausing ||
      p == SimpleRunPhase.paused;

  VoidCallback? _tap(SimpleRunPhase p) {
    final run = ref.read(simpleRunProvider.notifier);
    return switch (p) {
      SimpleRunPhase.ready => run.start,
      SimpleRunPhase.imaging => run.pause,
      SimpleRunPhase.paused => run.resume,
      _ => null,
    };
  }

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final phase = simpleRunPhase(run, ref.watch(sequenceRunStateProvider).value);
    final camera = ref.watch(cameraStatusProvider).value;
    final exposing = camera?.runtimeState == 'exposing';
    final holdable = _holdable(phase);
    final tap = run.busy ? null : _tap(phase);
    final ring = switch (phase) {
      SimpleRunPhase.ready => AraColors.accentConnected,
      SimpleRunPhase.paused => AraColors.accentBusy,
      SimpleRunPhase.none => AraColors.textDisabled,
      _ => AraColors.textPrimary.withValues(alpha: 0.25),
    };
    final pauseBars =
        phase == SimpleRunPhase.imaging || phase == SimpleRunPhase.pausing;
    return Semantics(
      button: true,
      label: switch (phase) {
        SimpleRunPhase.ready => 'Start imaging',
        SimpleRunPhase.imaging => 'Pause after this frame',
        SimpleRunPhase.pausing => 'Pausing',
        SimpleRunPhase.paused => 'Resume',
        _ => 'Create a plan first',
      },
      onTap: tap,
      customSemanticsActions: holdable
          ? {
              const CustomSemanticsAction(label: 'Stop the run'): () =>
                  ref.read(simpleRunProvider.notifier).stop(),
            }
          : null,
      child: Listener(
        onPointerDown: (_) {
          _down = true;
          _held = false;
          if (holdable) _hold.forward(from: 0);
        },
        onPointerUp: (_) {
          if (!_down) return;
          _down = false;
          final quick = _hold.value < 0.15;
          _hold.value = 0;
          // A quick release is a tap; a longer one short of 2 s cancels.
          if (!_held && (quick || !holdable)) tap?.call();
        },
        onPointerCancel: (_) {
          _down = false;
          _hold.value = 0;
        },
        child: Stack(
          clipBehavior: Clip.none,
          alignment: Alignment.center,
          children: [
            Container(
              key: const Key('simple-shutter'),
              width: 60,
              height: 60,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                border: Border.all(color: ring, width: 3),
              ),
              alignment: Alignment.center,
              child: Icon(
                pauseBars ? Icons.pause : Icons.play_arrow,
                size: 40,
                color: phase == SimpleRunPhase.none
                    ? AraColors.textDisabled
                    : AraColors.textPrimary,
              ),
            ),
            // The frame exposing, or the hold to stop over it.
            if (_hold.value > 0 || (pauseBars && exposing))
              SizedBox(
                width: 60,
                height: 60,
                child: TweenAnimationBuilder<double>(
                  tween: Tween(
                    end: _hold.value > 0
                        ? _hold.value
                        : (camera?.exposureProgressPct ?? 0) / 100,
                  ),
                  duration: _hold.value > 0
                      ? Duration.zero
                      : const Duration(milliseconds: 900),
                  builder: (_, v, _) => CircularProgressIndicator(
                    value: v,
                    strokeWidth: 3,
                    color: _hold.value > 0
                        ? AraColors.accentError
                        : AraColors.selectionBg,
                  ),
                ),
              ),
            if (_hold.value > 0)
              Positioned(
                right: 60 + kSimpleInset + 6,
                child: SimplePanel(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 12,
                    vertical: 8,
                  ),
                  child: Text(
                    'Hold to stop',
                    style: kSimpleText,
                    maxLines: 1,
                    softWrap: false,
                  ),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

TextStyle get _dim => kSimpleText.copyWith(color: AraColors.textSecondary);
TextStyle get _head =>
    kSimpleText.copyWith(fontSize: 16, fontWeight: FontWeight.w600);

const _kFramePresets = [5, 10, 12, 15, 20, 30, 40, 60, 90, 120, 180, 240, 300];

/// Plan's sheet, centred over the image: the target (a search, or Pick in
/// Sky), a row per filter (sub length and frames), then Create plan (Save
/// plan once one exists); once the run ends, Night done. The shutter starts
/// the plan.
class SimpleRunSheet extends ConsumerStatefulWidget {
  const SimpleRunSheet({
    super.key,
    required this.onShowSky,
    required this.onPickInSky,
    required this.onCreated,
    required this.onLibrary,
  });

  /// A search runs in the planetarium, so it shows behind the sheet.
  final VoidCallback onShowSky;

  /// Closes the sheet for the sky; Sky's card brings it back.
  final VoidCallback onPickInSky;
  final VoidCallback onCreated;
  final VoidCallback onLibrary;

  @override
  ConsumerState<SimpleRunSheet> createState() => _SimpleRunSheetState();
}

class _SimpleRunSheetState extends ConsumerState<SimpleRunSheet> {
  final _query = TextEditingController();

  @override
  void initState() {
    super.initState();
    // The rows start from the profile's filters and default exposure.
    hydratePlanningSettings(ProviderScope.containerOf(context, listen: false));
  }

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  void _search() {
    final q = _query.text.trim();
    if (q.isEmpty) return;
    widget.onShowSky();
    ref.read(planetariumSearchProvider.notifier).search(q);
  }

  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    return SimplePanel(
      padding: const EdgeInsets.all(18),
      child: SizedBox(
        width: 400,
        // A short window scrolls the sheet rather than cutting it.
        child: SingleChildScrollView(
          child: simpleRunPhase(run, info) == SimpleRunPhase.done
              ? _NightDone(run: run, info: info!, onLibrary: widget.onLibrary)
              : _plan(run),
        ),
      ),
    );
  }

  Widget _plan(SimpleRun run) {
    final sel = ref.watch(planetariumSelectionProvider);
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
        sel == null
            ? s.exposureSeconds
            : suggestedSubSeconds(
                ref,
                // The unnamed row (no filter choice) is the set's one filter.
                filterName: s.filterName.isEmpty
                    ? filters.firstOrNull?.name ?? ''
                    : s.filterName,
                raDeg: sel.raJ2000Hours * 15,
                decDeg: sel.decJ2000Deg,
                defaultExposure:
                    ref
                        .watch(imagingDefaultsProvider)
                        .defaultExposure
                        .inMilliseconds /
                    1000,
                skyCalibrationK: k,
              ),
    ];
    final hint = simpleRunSuggestion(steps, suggested);
    Duration dur(double s) => Duration(milliseconds: (s * 1000).round());
    // The filter column fits the longest name ("L-eXtreme"); the tiles share
    // what's left.
    final nameStyle = kSimpleText.copyWith(fontWeight: FontWeight.w600);
    double widthOf(String name) {
      final painter = TextPainter(
        text: TextSpan(
          text: name,
          style: DefaultTextStyle.of(context).style.merge(nameStyle),
        ),
        textDirection: TextDirection.ltr,
        textScaler: MediaQuery.textScalerOf(context),
      )..layout();
      final w = painter.width.ceilToDouble();
      painter.dispose();
      return w;
    }

    final nameWidth = steps.map((s) => widthOf(s.filterName)).fold(32.0, math.max);
    final saved = run.sequenceId != null && !run.started;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text("Tonight's plan", style: _head),
        TextField(
          key: const Key('simple-run-search'),
          controller: _query,
          style: kSimpleText,
          textInputAction: TextInputAction.search,
          onSubmitted: (_) => _search(),
          decoration: InputDecoration(
            hintText: 'Search objects',
            prefixIcon: const Icon(Icons.search, size: 20),
            suffixIcon: IconButton(
              tooltip: 'Search',
              icon: const Icon(Icons.arrow_forward, size: 20),
              onPressed: _search,
            ),
          ),
        ),
        Row(
          children: [
            Expanded(
              child: sel == null
                  ? Text('Search, or pick in the sky.', style: _dim)
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          sel.name,
                          style: kSimpleText.copyWith(
                            fontWeight: FontWeight.w600,
                          ),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        Text(
                          simpleTargetStatus(sel),
                          style: sel.belowHorizon
                              ? kSimpleText.copyWith(
                                  color: AraColors.accentBusy,
                                )
                              : _dim,
                        ),
                      ],
                    ),
            ),
            TextButton(
              key: const Key('simple-run-pick-in-sky'),
              onPressed: widget.onPickInSky,
              child: const Text('Pick in Sky'),
            ),
          ],
        ),
        const Divider(height: 1),
        for (var i = 0; i < steps.length; i++)
          Row(
            spacing: AraSpace.s8,
            children: [
              SizedBox(
                width: nameWidth,
                child: Text(
                  steps[i].filterName.isEmpty ? '—' : steps[i].filterName,
                  style: nameStyle,
                  maxLines: 1,
                ),
              ),
              Expanded(
                child: SimplePresetTile<Duration>(
                  key: Key('simple-run-exposure-$i'),
                  label: 'Exposure',
                  value: dur(steps[i].exposureSeconds),
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
        FilledButton(
          key: const Key('simple-run-create'),
          onPressed: sel == null || sel.belowHorizon || run.busy
              ? null
              : () async {
                  final ok = await ref
                      .read(simpleRunProvider.notifier)
                      .create(ref);
                  if (ok) widget.onCreated();
                },
          child: Text(
            run.busy
                ? 'Creating…'
                : saved
                ? 'Save plan'
                : 'Create plan',
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

/// Plan's panel beside the rail while imaging: frames done of the plan, when
/// it ends, and a bar per filter. The shutter pauses and, held, stops.
class SimpleRunPanel extends ConsumerWidget {
  const SimpleRunPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = info?.sequenceId == run.sequenceId;
    final steps = ours ? run.steps : const <FilterPlanStep>[];
    final captured = info?.framesCaptured ?? 0;
    final done = simpleRunProgress(steps, captured);
    final remaining = info?.estimatedRemainingSeconds;
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
              steps.isEmpty ? '$captured frames' : '$captured / ${_total(steps)}',
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
            Text(
              'Tap the shutter to pause after this frame; hold it to stop.',
              style: _dim.copyWith(fontSize: 12),
            ),
          ],
        ),
      ),
    );
  }
}
