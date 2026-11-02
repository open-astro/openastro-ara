import 'dart:async';
import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart' show CustomSemanticsAction;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/imaging_run_body.dart';
import '../../models/sequence/nina_dom.dart' show childrenOf, withChildren;
import '../../models/sequence/sequence_summary.dart';
import '../../services/sequence_api.dart' show SequenceClient;
import '../../state/equipment/camera_state.dart';
import '../../state/planning/sky_model_calibration_state.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/filter_set_state.dart';
import '../../state/setup/setup_readiness.dart';
import '../../state/simple_mode_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../services/dso_catalog_service.dart' show designationKey;
import '../../services/tonight_sky_api.dart';
import '../../state/sky_atlas/planetarium_selection_state.dart';
import '../../state/sky_atlas/tonight_sky_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/friendly_error.dart';
import '../../widgets/sequencer/target_plan_dialog.dart'
    show planFrameCount, planOptionsAvailable, suggestedSubSeconds;
import '../../state/ws/ws_providers.dart';
import '../../widgets/emergency_stop_button.dart' show kEmergencyStopHold;
import '../../widgets/stop_mount_overlay.dart' show SlewWsEvents;
import '../../widgets/sequencer/run_completion_sheet.dart'
    show runFramesPerFilter;
import 'simple_preview_panel.dart'
    show SimplePresetTile, exposurePresets, formatExposure, kSimpleText;
import 'simple_shell.dart'
    show
        SimplePanel,
        kSimpleInset,
        kSimpleRailWidth,
        kSimpleTileFill,
        leaveSimpleMode;

/// The first plan for a filter set: LRGB when the set has any of it, else the
/// narrowband filters, else every filter; one row with no filter name when
/// there is no choice to make (OSC). The rows split [remainingDarkHours]
/// (60 frames without a window) evenly. Pure — unit-tested.
// Subs start at the profile's default exposure; the sheet offers the
// suggested ones (simpleRunSuggestion).
List<FilterPlanStep> simpleRunDefaultPlan(
  List<PlanningFilter> filters,
  double exposureSeconds, {
  double? remainingDarkHours,
}) {
  if (!planOptionsAvailable(filters)) {
    return [
      FilterPlanStep(
        filterName: '',
        exposureSeconds: exposureSeconds,
        frameCount: planFrameCount(
          exposureSeconds,
          remainingDarkHours: remainingDarkHours,
          filterCount: 1,
        ),
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
        frameCount: planFrameCount(
          exposureSeconds,
          remainingDarkHours: remainingDarkHours,
          filterCount: picked.length,
        ),
      ),
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

/// Tonight's dark hours left for the picked target, from the ranking; null
/// when the ranking doesn't have it.
// shortcut: matched by name, so a search pick named unlike the ranking ("M 31")
// gets the 60-frame fallback; match by position if that bites.
final simpleDarkHoursProvider = Provider<double?>((ref) {
  final name = ref.watch(planetariumSelectionProvider.select((s) => s?.name));
  final hours = (ref.watch(tonightSkyProvider).value ?? const [])
      .where((o) => o.name == name)
      .firstOrNull
      ?.remainingHours;
  return hours != null && hours > 0 ? hours : null;
});

/// The plan the sheet edits: one row per filter. A new target's window refits
/// every row's frames; a new sub length refits its row's.
class SimpleRunPlanNotifier extends Notifier<List<FilterPlanStep>> {
  double? _darkHours;

  @override
  List<FilterPlanStep> build() {
    ref.listen(simpleDarkHoursProvider, (prev, next) {
      if (prev == next) return;
      _darkHours = next;
      state = [for (final s in state) _fit(s, s.exposureSeconds)];
    });
    _darkHours = ref.read(simpleDarkHoursProvider);
    return simpleRunDefaultPlan(
      ref.watch(filterSetProvider).filters,
      ref.watch(imagingDefaultsProvider).defaultExposure.inMilliseconds / 1000,
      remainingDarkHours: _darkHours,
    );
  }

  FilterPlanStep _fit(FilterPlanStep s, double exposureSeconds) => FilterPlanStep(
    filterName: s.filterName,
    exposureSeconds: exposureSeconds,
    frameCount: planFrameCount(
      exposureSeconds,
      remainingDarkHours: _darkHours,
      filterCount: state.length,
    ),
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

  // Without a window the count the user set stays.
  void setExposure(int i, double seconds) => _darkHours == null
      ? _set(i, exposureSeconds: seconds)
      : state = [
          for (var j = 0; j < state.length; j++)
            j == i ? _fit(state[j], seconds) : state[j],
        ];
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

/// A suggestion's second line: its type, then its altitude now or when it
/// rises. Pure — unit-tested.
String simpleSuggestionLine(TonightSkyObject o) {
  final rise = o.windowStartUtc?.toLocal();
  final where = o.altitudeDeg > 0
      ? '${o.altitudeDeg.round()}° up'
      : rise == null
      ? 'below the horizon'
      : 'rises ${_hhmm(rise)}';
  return [if (o.type.isNotEmpty) o.type, where].join(' · ');
}

/// A suggestion as the planetarium's selection, so the plan takes it as if
/// tapped in the sky. Pure — unit-tested.
// shortcut: the ranker's position is J2000 and stands in for JNOW too; only
// the plan (J2000) reads it, the planetarium isn't pointed at it.
PlanetariumSelection simpleSelectionOf(TonightSkyObject o) => PlanetariumSelection(
  name: o.name,
  kind: o.type,
  raHours: o.raDeg / 15,
  decDeg: o.decDeg,
  raJ2000Hours: o.raDeg / 15,
  decJ2000Deg: o.decDeg,
  altDeg: o.altitudeDeg,
  maxAltDeg: o.maxAltitudeDeg,
  rise: o.windowStartUtc?.toLocal(),
  designations: [o.id, ...o.aliases],
);

const _tagOrder = ['M', 'NGC', 'IC', 'SH2', 'B', 'LBN'];
final _tagKey = RegExp(r'^(M|NGC|IC|SH2|B|LBN)-?(\d+[A-Z]?)$');

/// A designation as the plan square writes it ("M 42", "Sh2-129") and its
/// rank in [_tagOrder]; null for any other catalog.
(int, String)? _catalogTag(String designation) {
  final m = _tagKey.firstMatch(designationKey(designation));
  if (m == null) return null;
  final p = m[1]!;
  return (_tagOrder.indexOf(p), p == 'SH2' ? 'Sh2-${m[2]}' : '$p ${m[2]}');
}

/// The plan square's name for a target: a curated region's own tag (its name
/// ends "(IC 434)"), else its best designation, M > NGC > IC > Sh2 > B > LBN;
/// null when it has none (the square shows the name). Pure — unit-tested.
String? simpleTargetTag(String name, List<String> designations) {
  final own = RegExp(r'\(([^()]+)\)\s*$').firstMatch(name)?[1];
  if (own != null) {
    final first = own.split(' / ').first.trim();
    return _catalogTag(first)?.$2 ?? first;
  }
  final tags = [for (final d in designations) ?_catalogTag(d)]
    ..sort((a, b) => a.$1.compareTo(b.$1));
  return tags.firstOrNull?.$2;
}

/// One target in Simple Mode's queue: its name, the plan square's tag, its
/// rows (to split the progress per filter), its position (to queue it again)
/// and, queued below the horizon, when it rises.
class SimpleTarget {
  const SimpleTarget({
    required this.name,
    this.tag,
    this.steps = const [],
    this.raDeg = 0,
    this.decDeg = 0,
    this.rise,
  });

  final String name;

  /// The catalog tag for the plan square (null: its name).
  final String? tag;
  final List<FilterPlanStep> steps;

  /// J2000.
  final double raDeg;
  final double decDeg;

  /// When it rises, if it was below the horizon when queued.
  // shortcut: the client holds the start; the daemon waiting for the rise
  // itself needs ALP-4857.
  final DateTime? rise;

  /// Still below the horizon at [now].
  bool waiting(DateTime now) => rise != null && now.isBefore(rise!);
}

/// Frames done per row of each target, the targets and their rows running in
/// order. Pure — unit-tested.
// shortcut: counts the run's filed frames against the plan in order, so a
// skipped or failed frame shifts the split; read per-loop counts from the
// run tree if that matters.
List<List<int>> simpleTargetProgress(List<SimpleTarget> targets, int captured) {
  var left = captured;
  return [
    for (final t in targets)
      [
        for (final s in t.steps)
          () {
            final done = math.min(left, s.frameCount);
            left -= done;
            return done;
          }(),
      ],
  ];
}

/// The target the run is on: the first not yet full, else the last. Pure —
/// unit-tested.
int simpleCurrentTarget(List<SimpleTarget> targets, int captured) {
  final done = simpleTargetProgress(targets, captured);
  for (var i = 0; i < targets.length; i++) {
    if (_total(targets[i].steps) > done[i].fold(0, (a, b) => a + b)) return i;
  }
  return math.max(0, targets.length - 1);
}

/// Simple Mode's queue on the rig: one sequence holding its targets in order,
/// whether it has started, a call in flight, a pause asked for but not
/// reached yet, and why the last call failed.
class SimpleRun {
  const SimpleRun({
    this.sequenceId,
    this.targets = const [],
    this.started = false,
    this.busy = false,
    this.pausing = false,
    this.error,
    this.noStorage = false,
  });

  final String? sequenceId;
  final List<SimpleTarget> targets;
  final bool started;
  final bool busy;

  /// Pause asked for: the daemon finishes the frame in hand first.
  final bool pausing;
  final String? error;

  /// The daemon refused for want of a storage disk: [error] offers Storage.
  final bool noStorage;

  /// Every target's rows, in order.
  List<FilterPlanStep> get steps => [for (final t in targets) ...t.steps];

  /// The first target, which the run starts on.
  String? get targetName => targets.firstOrNull?.name;

  /// The first target is still below the horizon at [now]: no start yet.
  bool waiting(DateTime now) => targets.firstOrNull?.waiting(now) ?? false;

  SimpleRun copyWith({
    List<SimpleTarget>? targets,
    bool? started,
    bool? busy,
    bool? pausing,
    String? error,
    bool noStorage = false,
  }) => SimpleRun(
        sequenceId: sequenceId,
        targets: targets ?? this.targets,
        started: started ?? this.started,
        busy: busy ?? this.busy,
        pausing: pausing ?? this.pausing,
        error: error,
        noStorage: noStorage,
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

  /// Add to queue: the picked target joins the end of the queue (the one
  /// sequence; live when it is imaging), or makes it. [first] puts it at the
  /// front instead (Start now). [ref] is the caller's widget's (the create
  /// reads the widget tree). True once on the rig.
  Future<bool> create(WidgetRef ref, {bool first = false}) async {
    final sel = ref.read(planetariumSelectionProvider);
    final steps = ref.read(simpleRunPlanProvider);
    if (sel == null || state.busy) return false;
    final queue = state.sequenceId;
    state = state.copyWith(busy: true);
    try {
      // The append goes to the selected sequence.
      if (queue != null) {
        this.ref.read(selectedSequenceIdProvider.notifier).select(queue);
      }
      final r = await createImagingRun(
        ref,
        raDeg: sel.raJ2000Hours * 15,
        decDeg: sel.decJ2000Deg,
        targetName: sel.name,
        jumpToRun: false,
        appendToOpen: queue != null,
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
      if (queue != null && r.sequenceId != queue) {
        // The daemon refused the append (the run is winding down) and a
        // second sequence was made: don't leave it behind.
        try {
          await this.ref.read(sequenceApiProvider)?.deleteSequence(r.sequenceId);
        } catch (_) {}
        state = state.copyWith(
          busy: false,
          error: "The run is ending; this target can't join it.",
        );
        return false;
      }
      final target = SimpleTarget(
        name: sel.name,
        tag: simpleTargetTag(sel.name, sel.designations),
        steps: steps,
        raDeg: sel.raJ2000Hours * 15,
        decDeg: sel.decJ2000Deg,
        rise: sel.belowHorizon ? sel.rise : null,
      );
      if (queue == null) {
        state = SimpleRun(sequenceId: r.sequenceId, targets: [target]);
      } else if (first && state.targets.isNotEmpty) {
        await _moveFirst(queue, target.name, state.targets.first.name);
        state = state.copyWith(targets: [target, ...state.targets], busy: false);
      } else {
        state = state.copyWith(targets: [...state.targets, target], busy: false);
      }
      return true;
    } catch (e) {
      state = state.copyWith(busy: false, error: _why(e, 'create the plan'));
      return false;
    }
  }

  // Start now on a queue that hasn't started: the new block goes before the
  // first target's.
  Future<void> _moveFirst(String id, String name, String before) async {
    final api = ref.read(sequenceApiProvider);
    if (api == null) return;
    final body = (await api.getSequenceDetail(id)).body;
    final from = indexOfTargetBlock(body, name);
    final to = indexOfTargetBlock(body, before);
    if (from < 0 || to < 0 || from < to) return;
    final children = childrenOf(body);
    children.insert(to, children.removeAt(from));
    await api.updateSequence(id, body: withChildren(body, children));
  }

  /// Remove target [i] from the queue (one not started yet): live from a
  /// running sequence, else from the stored body.
  Future<void> remove(int i) async {
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    if (api == null || id == null || i >= state.targets.length) return;
    final name = state.targets[i].name;
    try {
      final body = (await api.getSequenceDetail(id)).body;
      final active =
          ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false;
      if (active) {
        final at = indexOfTargetBlock(body, name);
        if (at >= 0) await api.removeRunItem(id, [at]);
      } else if (removeTargetFromRunBody(body, name) case final next?) {
        await api.updateSequence(id, body: next);
      }
      final left = [...state.targets]..removeAt(i);
      if (left.isEmpty && !state.started) {
        // Nothing left to run: the empty sequence goes too.
        await api.deleteSequence(id);
        state = const SimpleRun();
      } else {
        state = state.copyWith(targets: left);
      }
    } catch (e) {
      state = state.copyWith(error: _why(e, 'remove it from the queue'));
    }
  }

  /// Create and start (or ▶ with a target picked and no plan yet); with a
  /// queue, Start now: the target goes first.
  Future<bool> createAndStart(WidgetRef ref) async {
    if (!await create(ref, first: state.sequenceId != null)) return false;
    await start();
    return state.error == null;
  }

  /// The shutter's actions.
  Future<void> start() => _op(
    (api, id) => api.start(id),
    'start imaging',
    before: state.copyWith(started: true),
  );
  Future<void> pause() =>
      _op((api, id) => api.pause(id), 'pause', before: state.copyWith(pausing: true));
  // The shutter's resume re-centers (pointing may have drifted while
  // paused); the progress panel offers the other two.
  Future<void> resume({bool recenter = true, bool refocus = false}) => _op(
    (api, id) => api.resume(id, recenter: recenter, refocus: refocus),
    'resume',
  );
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
      state = undo.copyWith(
        error: _why(e, action),
        noStorage:
            e is DioException &&
            e.response?.data is Map &&
            e.response!.data['title'] == 'store_unavailable',
      );
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

/// A panic stop (this client's, another's, or the daemon's) paused the run:
/// true from the slew's abort until the run leaves the pause. Run state has
/// no pause reason, so the abort event is the only tell.
class SimpleMountStoppedNotifier extends Notifier<bool> {
  @override
  bool build() {
    ref.listen(wsEventsProvider, (_, next) {
      if (next.value?.type == SlewWsEvents.aborted &&
          (ref.read(sequenceRunStateProvider).value?.state?.isActive ??
              false)) {
        state = true;
      }
    });
    ref.listen(sequenceRunStateProvider, (prev, next) {
      if (_paused(prev?.value?.state) && !_paused(next.value?.state)) {
        state = false;
      }
    });
    return false;
  }
}

final simpleMountStoppedProvider =
    NotifierProvider<SimpleMountStoppedNotifier, bool>(
      SimpleMountStoppedNotifier.new,
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

/// The plan squares under the strip, for the queue's whole life, right to
/// left in queue order: each target's tag, frames done of its total, the
/// filter and sub length in hand, and the time (total before it starts, left
/// while imaging). The target in hand is green ready, blue imaging, amber
/// paused; the others green. Nothing without a plan. A tap on any square:
/// the sheet (where the queue is managed) or the progress panel.
class SimpleRunTracker extends ConsumerStatefulWidget {
  const SimpleRunTracker({super.key, required this.onTap});

  final VoidCallback onTap;

  @override
  ConsumerState<SimpleRunTracker> createState() => _SimpleRunTrackerState();
}

class _SimpleRunTrackerState extends ConsumerState<SimpleRunTracker> {
  @override
  Widget build(BuildContext context) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final phase = simpleRunPhase(run, info);
    if (phase == SimpleRunPhase.none) return const SizedBox.shrink();
    final ours = run.sequenceId != null && info?.sequenceId == run.sequenceId;
    final captured = ours || !run.started ? (info?.framesCaptured ?? 0) : 0;
    final known = run.targets.isNotEmpty && (ours || phase == SimpleRunPhase.ready);
    final active = switch (phase) {
      SimpleRunPhase.ready || SimpleRunPhase.done => AraColors.accentConnected,
      SimpleRunPhase.paused => AraColors.accentBusy,
      _ => AraColors.selectionBg,
    };
    String? stateWord() => switch (phase) {
      SimpleRunPhase.pausing => 'Pausing…',
      SimpleRunPhase.paused => 'Paused',
      SimpleRunPhase.done => 'Done',
      _ => null,
    };
    if (!known) {
      // A run picked up from the daemon: what it reports.
      return _PlanSquare(
        key: const Key('simple-run-tracker'),
        name: info?.currentTargetName ?? '',
        frames: '$captured',
        last: stateWord() ??
            (info?.estimatedRemainingSeconds == null
                ? ''
                : simpleRunDuration(info!.estimatedRemainingSeconds!)),
        color: active,
        onTap: widget.onTap,
      );
    }
    final frames = phase == SimpleRunPhase.ready ? 0 : captured;
    final progress = simpleTargetProgress(run.targets, frames);
    final cur = simpleCurrentTarget(run.targets, frames);
    final now = DateTime.now();
    Widget square(int i) {
      final t = run.targets[i];
      final done = progress[i];
      final got = done.fold(0, (a, b) => a + b);
      final total = _total(t.steps);
      final finished = phase == SimpleRunPhase.done || i < cur;
      final inHand = i == cur && !finished;
      final left = [
        for (var r = 0; r < t.steps.length; r++)
          (t.steps[r].frameCount - done[r]) * t.steps[r].exposureSeconds,
      ].fold(0.0, (a, b) => a + b);
      final last = finished
          ? 'Done'
          : t.waiting(now) && (!inHand || phase == SimpleRunPhase.ready)
          ? 'rises ${_hhmm(t.rise!.toLocal())}'
          : inHand
          ? stateWord() ??
                simpleRunDuration(
                  run.targets.length == 1
                      ? info?.estimatedRemainingSeconds ?? left
                      : left,
                )
          : simpleRunDuration(left);
      return _PlanSquare(
        name: t.tag ?? t.name,
        label: t.name,
        frames: '$got / $total',
        sub: t.steps.isEmpty ? null : _sub(t.steps[_current(t.steps, done)]),
        last: last,
        paused: inHand && phase == SimpleRunPhase.paused,
        color: inHand ? active : AraColors.accentConnected,
        fraction: inHand && phase != SimpleRunPhase.ready && total > 0
            ? got / total
            : null,
        onTap: widget.onTap,
      );
    }

    return Row(
      key: const Key('simple-run-tracker'),
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: 6,
      children: [
        // The queue runs right to left: the first target sits in the corner.
        for (var i = run.targets.length - 1; i >= 0; i--) square(i),
      ],
    );
  }
}

/// One plan square: the target's tag, frames, filter · sub and time, a bar
/// filling while it images.
class _PlanSquare extends StatelessWidget {
  const _PlanSquare({
    super.key,
    required this.name,
    this.label,
    required this.frames,
    this.sub,
    required this.last,
    required this.color,
    this.paused = false,
    this.fraction,
    required this.onTap,
  });

  final String name;

  /// The whole name, for screen readers ([name] may be the tag).
  final String? label;
  final String frames;
  final String? sub;
  final String last;
  final Color color;
  final bool paused;
  final double? fraction;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final small = kSimpleText.copyWith(
      fontSize: 11.5,
      color: AraColors.textSecondary,
    );
    return Semantics(
      button: true,
      label: 'Plan: ${label ?? name}',
      child: GestureDetector(
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
                        (name, kSimpleText.copyWith(fontWeight: FontWeight.w600)),
                        (frames, kSimpleText),
                        if (sub != null) (sub!, small),
                        (
                          last,
                          paused
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
              if (fraction != null)
                Align(
                  alignment: Alignment.bottomLeft,
                  child: FractionallySizedBox(
                    widthFactor: fraction,
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

  Timer? _riseTimer;

  @override
  void dispose() {
    _hold.dispose();
    _riseTimer?.cancel();
    super.dispose();
  }

  bool _pickable() {
    final sel = ref.read(planetariumSelectionProvider);
    return sel != null && !sel.belowHorizon;
  }

  bool _holdable(SimpleRunPhase p) =>
      p == SimpleRunPhase.imaging ||
      p == SimpleRunPhase.pausing ||
      p == SimpleRunPhase.paused;

  VoidCallback? _tap(SimpleRunPhase p) {
    final run = ref.read(simpleRunProvider.notifier);
    return switch (p) {
      SimpleRunPhase.none when _pickable() => () => run.createAndStart(ref),
      SimpleRunPhase.ready
          when !ref.read(simpleRunProvider).waiting(DateTime.now()) =>
        run.start,
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
    ref.watch(planetariumSelectionProvider);
    // A picked target with no plan yet starts like a ready plan; a plan
    // queued below the horizon waits for the rise.
    final waiting = phase == SimpleRunPhase.ready && run.waiting(DateTime.now());
    if (waiting) {
      _riseTimer ??= Timer(run.targets.first.rise!.difference(DateTime.now()), () {
        _riseTimer = null;
        if (mounted) setState(() {});
      });
    }
    final idle =
        (phase == SimpleRunPhase.none && !_pickable()) || waiting;
    final tap = run.busy ? null : _tap(phase);
    final ring = switch (phase) {
      _ when idle => AraColors.textDisabled,
      SimpleRunPhase.ready || SimpleRunPhase.none => AraColors.accentConnected,
      SimpleRunPhase.paused => AraColors.accentBusy,
      _ => AraColors.textPrimary.withValues(alpha: 0.25),
    };
    final pauseBars =
        phase == SimpleRunPhase.imaging || phase == SimpleRunPhase.pausing;
    return Semantics(
      button: true,
      label: switch (phase) {
        _ when waiting => 'Rises ${_hhmm(run.targets.first.rise!.toLocal())}',
        _ when idle => 'Pick a target first',
        SimpleRunPhase.ready || SimpleRunPhase.none => 'Start imaging',
        SimpleRunPhase.imaging => 'Pause after this frame',
        SimpleRunPhase.pausing => 'Pausing',
        SimpleRunPhase.paused => 'Resume',
        _ => 'Start imaging',
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
                color: idle ? AraColors.textDisabled : AraColors.textPrimary,
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
/// The sheet's list column (QUEUE, tonight's best) on a wide window.
const double _kListsWidth = 240;

/// The window width from which the lists sit beside the plan.
const double _kTwoColumnWidth = 960;

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

  /// "M42 added · 3 in the queue", until the next pick.
  String? _added;

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
      // A short window scrolls the sheet rather than cutting it.
      child: SingleChildScrollView(
        child: simpleRunPhase(run, info) == SimpleRunPhase.done
            ? SizedBox(
                width: 400,
                child: _NightDone(
                  run: run,
                  info: info!,
                  onLibrary: widget.onLibrary,
                ),
              )
            : _plan(run),
      ),
    );
  }

  Widget _plan(SimpleRun run) {
    ref.listen(planetariumSelectionProvider, (_, next) {
      if (next != null && _added != null) setState(() => _added = null);
    });
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
    // Below the horizon it can be queued, not started; while imaging the
    // sheet only adds (the shutter has the run).
    final imaging =
        ref.watch(sequenceRunStateProvider).value?.state?.isActive ?? false;
    final off = sel == null || sel.belowHorizon || run.busy || imaging;
    final queueOff = sel == null || sel.neverRises || run.busy;
    final best = (ref.watch(tonightSkyProvider).value ?? const [])
        .where((o) => o.maxAltitudeDeg > 0)
        .take(4)
        .toList();
    // A wide window puts the lists in a column beside the plan, so the sheet
    // stays short enough to clear the status pill.
    final wide =
        (run.targets.isNotEmpty || best.isNotEmpty) &&
        MediaQuery.sizeOf(context).width >= _kTwoColumnWidth;
    final lists = <Widget>[
        if (run.targets.isNotEmpty) _QueueList(run: run),
        // Tonight's best, from the full UI's ranking: one tap picks a target.
        if (best.isNotEmpty) ...[
          Text("TONIGHT'S BEST", style: AraText.caption),
          Wrap(
            spacing: AraSpace.s8,
            runSpacing: AraSpace.s8,
            children: [
              for (final o in best)
                SizedBox(
                  width: wide ? _kListsWidth : (400 - AraSpace.s8) / 2,
                  height: 52,
                  child: Material(
                    color: sel?.name == o.name
                        ? AraColors.selectionBg
                        : kSimpleTileFill,
                    borderRadius: BorderRadius.circular(8),
                    child: InkWell(
                      key: Key('simple-run-suggestion-${o.name}'),
                      borderRadius: BorderRadius.circular(8),
                      onTap: () => ref
                          .read(planetariumSelectionProvider.notifier)
                          .set(simpleSelectionOf(o)),
                      child: Padding(
                        padding: const EdgeInsets.symmetric(horizontal: 12),
                        child: Column(
                          mainAxisAlignment: MainAxisAlignment.center,
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              o.name,
                              style: kSimpleText.copyWith(
                                fontWeight: FontWeight.w600,
                              ),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                            Text(
                              simpleSuggestionLine(o),
                              style: _dim.copyWith(fontSize: 12),
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
            ],
          ),
        ],
    ];
    final main = Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text("Tonight's plan", style: _head),
        if (!wide) ...lists,
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
        // The full UI asks before Run; here it only warns (a permanent pier
        // or a daytime test runs fine unaligned).
        if (ref.watch(setupPolarAlignStateProvider) != SetupStepState.done &&
            !(ref.watch(simplePermanentPierProvider).value ?? false))
          Text(
            'Not polar aligned tonight',
            style: kSimpleText.copyWith(color: AraColors.accentBusy),
          ),
        if (_added != null)
          Text(
            _added!,
            style: kSimpleText.copyWith(color: AraColors.accentConnected),
          ),
        if (run.error != null) _RunError(run),
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: OutlinedButton(
                key: const Key('simple-run-queue'),
                onPressed: queueOff
                    ? null
                    : () async {
                        final name = sel.name;
                        final ok = await ref
                            .read(simpleRunProvider.notifier)
                            .create(ref);
                        if (!ok || !mounted) return;
                        // The sheet stays up for the next pick.
                        final n = ref.read(simpleRunProvider).targets.length;
                        ref.read(planetariumSelectionProvider.notifier).set(null);
                        setState(() => _added = '$name added · $n in the queue');
                      },
                child: const Text('Add to queue'),
              ),
            ),
            Expanded(
              child: FilledButton(
                key: const Key('simple-run-start'),
                style: FilledButton.styleFrom(
                  backgroundColor: AraColors.accentConnected,
                ),
                onPressed: off
                    ? null
                    : () async {
                        final ok = await ref
                            .read(simpleRunProvider.notifier)
                            .createAndStart(ref);
                        if (ok) widget.onCreated();
                      },
                child: Text(
                  run.busy
                      ? 'Starting…'
                      : run.sequenceId != null
                      ? 'Start now'
                      : 'Create and start',
                ),
              ),
            ),
          ],
        ),
      ],
    );
    if (!wide) return SizedBox(width: 400, child: main);
    return Row(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s16,
      children: [
        SizedBox(
          width: _kListsWidth,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            spacing: AraSpace.s8,
            children: lists,
          ),
        ),
        SizedBox(width: 400, child: main),
      ],
    );
  }
}

/// The sheet's QUEUE: each target with its tag, frames done of its total and
/// time left, and × for one that hasn't started.
class _QueueList extends ConsumerWidget {
  const _QueueList({required this.run});
  final SimpleRun run;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = run.started && info?.sequenceId == run.sequenceId;
    final captured = ours ? info?.framesCaptured ?? 0 : 0;
    final progress = simpleTargetProgress(run.targets, captured);
    final cur = simpleCurrentTarget(run.targets, captured);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text('QUEUE', style: AraText.caption),
        for (var i = 0; i < run.targets.length; i++)
          () {
            final t = run.targets[i];
            final got = progress[i].fold(0, (a, b) => a + b);
            final left = [
              for (var r = 0; r < t.steps.length; r++)
                (t.steps[r].frameCount - progress[i][r]) *
                    t.steps[r].exposureSeconds,
            ].fold(0.0, (a, b) => a + b);
            return Row(
              spacing: AraSpace.s8,
              children: [
                // Two lines, to fit the narrow list column.
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        t.tag ?? t.name,
                        style: kSimpleText.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                      Text(
                        '$got / ${_total(t.steps)} · ${simpleRunDuration(left)}',
                        style: _dim.copyWith(fontSize: 12),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ],
                  ),
                ),
                SizedBox(
                  width: 44,
                  height: 44,
                  child: !run.started || i > cur
                      ? IconButton(
                          key: Key('simple-run-remove-$i'),
                          tooltip: 'Remove ${t.name} from the queue',
                          icon: const Icon(Icons.close, size: 18),
                          onPressed: run.busy
                              ? null
                              : () => ref
                                    .read(simpleRunProvider.notifier)
                                    .remove(i),
                        )
                      : null,
                ),
              ],
            );
          }(),
      ],
    );
  }
}

/// Why the last call failed; a missing storage disk offers the full UI's
/// Storage page, where the drive is set up.
class _RunError extends ConsumerWidget {
  const _RunError(this.run);
  final SimpleRun run;

  @override
  Widget build(BuildContext context, WidgetRef ref) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      Text(
        run.error!,
        style: kSimpleText.copyWith(color: AraColors.errorText),
        maxLines: 4,
        overflow: TextOverflow.ellipsis,
      ),
      if (run.noStorage)
        TextButton(
          onPressed: () {
            ref
                .read(selectedSettingsPanelProvider.notifier)
                .select('session.storage');
            leaveSimpleMode(ref, kOptionsTabIndex);
          },
          child: const Text('Open Storage'),
        ),
    ],
  );
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
    // One target: the library's count when it answers (its newest session),
    // else the plan-order estimate, which a queue always uses.
    final multi = run.targets.length > 1;
    final filed = multi ? null : runFramesPerFilter(ref, info);
    final est = simpleTargetProgress(run.targets, info.framesCaptured);
    int done(int t, int r) {
      final s = run.targets[t].steps[r];
      if (filed == null) return est[t][r];
      return s.filterName.isEmpty
          ? filed.values.fold(0, (a, b) => a + b)
          : filed[s.filterName] ?? 0;
    }

    final start = info.startedUtc?.toLocal(), end = info.completedUtc?.toLocal();
    final stoppedAt = info.state == SequenceRunState.completed
        ? null
        : info.currentInstructionDescription;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s8,
      children: [
        Text('Night done', style: _head),
        Text(
          [
            if (!multi) run.targetName ?? info.currentTargetName ?? '',
            if (start != null && end != null) ...[
              '${_hhmm(start)} – ${_hhmm(end)}',
              simpleRunDuration(end.difference(start).inSeconds.toDouble()),
            ],
            if (info.state == SequenceRunState.stopped) 'stopped',
            if (info.state == SequenceRunState.failed) 'failed',
          ].where((s) => s.isNotEmpty).join(' · '),
          style: _dim,
        ),
        if (stoppedAt != null && stoppedAt.isNotEmpty)
          Text(
            'Stopped at: $stoppedAt',
            style: kSimpleText.copyWith(color: AraColors.errorText),
          ),
        for (var t = 0; t < run.targets.length; t++) ...[
          if (multi)
            Text(
              run.targets[t].name,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
          for (var r = 0; r < run.targets[t].steps.length; r++)
            Row(
              children: [
                Expanded(
                  child: Text(
                    run.targets[t].steps[r].filterName.isEmpty
                        ? 'Frames'
                        : run.targets[t].steps[r].filterName,
                    style: _dim,
                  ),
                ),
                Text(
                  '${done(t, r)} × ${formatExposure(Duration(milliseconds: (run.targets[t].steps[r].exposureSeconds * 1000).round()))}',
                  style: kSimpleText,
                ),
              ],
            ),
        ],
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

/// Plan's panel beside the rail while imaging: the target in hand, its
/// frames done, when the queue ends, a bar per filter, and Add to queue. The
/// shutter pauses and, held, stops.
class SimpleRunPanel extends ConsumerWidget {
  const SimpleRunPanel({super.key, required this.onAdd});

  /// Add to queue: the sheet, to add a target while imaging.
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final run = ref.watch(simpleRunProvider);
    final info = ref.watch(sequenceRunStateProvider).value;
    final ours = info?.sequenceId == run.sequenceId && run.targets.isNotEmpty;
    final allCaptured = info?.framesCaptured ?? 0;
    final cur = ours ? simpleCurrentTarget(run.targets, allCaptured) : 0;
    final target = ours ? run.targets[cur] : null;
    final steps = target?.steps ?? const <FilterPlanStep>[];
    final done = ours
        ? simpleTargetProgress(run.targets, allCaptured)[cur]
        : const <int>[];
    final captured = ours ? done.fold(0, (a, b) => a + b) : allCaptured;
    final remaining = info?.estimatedRemainingSeconds;
    final paused = _paused(info?.state);
    final mountStopped = ref.watch(simpleMountStoppedProvider);
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
              'IMAGING ${(target?.name ?? info?.currentTargetName ?? '').toUpperCase()}',
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
            if (paused && mountStopped)
              Text(
                'Mount stopped',
                style: kSimpleText.copyWith(color: AraColors.accentBusy),
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
            if (run.error != null) _RunError(run),
            if (paused) ...[
              Text(
                'Tap the shutter to resume and re-center; hold it to stop.',
                style: _dim.copyWith(fontSize: 12),
              ),
              TextButton(
                onPressed: () => ref
                    .read(simpleRunProvider.notifier)
                    .resume(recenter: false),
                child: const Text('Resume without re-centering'),
              ),
              TextButton(
                onPressed: () => ref
                    .read(simpleRunProvider.notifier)
                    .resume(refocus: true),
                child: const Text('Re-center and refocus'),
              ),
            ] else
              Text(
                'Tap the shutter to pause after this frame; hold it to stop.',
                style: _dim.copyWith(fontSize: 12),
              ),
            TextButton(
              key: const Key('simple-run-add'),
              onPressed: onAdd,
              child: const Text('Add to queue'),
            ),
          ],
        ),
      ),
    );
  }
}
