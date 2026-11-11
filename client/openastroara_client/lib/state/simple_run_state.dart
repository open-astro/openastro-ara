import 'dart:async';
import 'dart:math' as math;
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../models/sequence/imaging_run_body.dart';
import '../models/sequence/nina_dom.dart' show childrenOf, withChildren;
import '../models/sequence/sequence_summary.dart';
import '../services/sequence_api.dart' show SequenceClient;
import 'equipment/filter_wheel_state.dart';
import 'sequencer/create_imaging_run.dart';
import 'sequencer/rig_capabilities_state.dart';
import 'sequencer/sequence_list_state.dart';
import 'settings/filter_set_state.dart';
import 'simple_messages_state.dart';
import 'time_sync_state.dart';
import 'settings/imaging_defaults_state.dart';
import 'settings/site_settings_state.dart';
import '../services/dso_catalog_service.dart'
    show designationKey;
import 'sky_atlas/planetarium_selection_state.dart';
import 'sky_atlas/tonight_sky_state.dart';
import '../l10n/l10n.dart';
import 'app_locale_state.dart';
import '../util/friendly_error.dart';
import '../util/tonight_sky_local.dart'
    show darkStart, dawnStopFor, scheduleAllowed;
import '../widgets/sequencer/target_plan_dialog.dart'
    show planFrameCount, planOptionsAvailable;
import 'ws/ws_providers.dart';
import '../widgets/stop_mount_overlay.dart' show SlewWsEvents;

/// The first plan for a filter set: LRGB when the set has any of it, else the
/// narrowband filters, else every filter; one row with no filter name when
/// there is no choice to make (OSC) or the rig cannot change filters
/// ([canChangeFilters] false: every row would shoot through the same glass). The rows split [remainingDarkHours]
/// (60 frames without a window) evenly. Pure — unit-tested.
// Subs start at the profile's default exposure; the sheet offers the
// suggested ones (simpleRunSuggestion).
List<FilterPlanStep> simpleRunDefaultPlan(
  List<PlanningFilter> filters,
  double exposureSeconds, {
  double? remainingDarkHours,
  bool canChangeFilters = true,
}) {
  if (!canChangeFilters || !planOptionsAvailable(filters)) {
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

int simplePlanTotal(List<FilterPlanStep> steps) =>
    steps.fold(0, (n, s) => n + s.frameCount);

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

/// [t]'s time of day at its next occurrence after [now], on the wheel's
/// 5-minute grid.
DateTime simpleNextAt(DateTime t, DateTime now) {
  final at = DateTime(now.year, now.month, now.day, t.hour, t.minute ~/ 5 * 5);
  return at.isAfter(now) ? at : at.add(const Duration(days: 1));
}

/// When it gets dark tonight (local), or null once dark: worked out once per
/// site and 5-minute tick, not on every rebuild of the sheet (a minute-by-
/// minute sun scan).
final simpleDarkStartProvider = Provider<DateTime?>((ref) {
  ref.watch(skyClockProvider);
  return darkStart(
    ref.watch(siteSettingsProvider),
    ref.watch(clockProvider)().toUtc(),
  )?.toLocal();
});

/// Whether a start time can be picked now: not from the night's end until
/// sunrise (the daemon would read it as yesterday's and start at once).
final simpleScheduleAllowedProvider = Provider<bool>((ref) {
  ref.watch(skyClockProvider);
  return scheduleAllowed(
    ref.watch(siteSettingsProvider),
    ref.watch(clockProvider)().toUtc(),
  );
});

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

  /// A queued target's rows ([load]): settings changes leave them alone.
  bool _loaded = false;

  @override
  List<FilterPlanStep> build() {
    _loaded = false;
    ref.listen(simpleDarkHoursProvider, (prev, next) {
      if (prev == next) return;
      _darkHours = next;
      state = [for (final s in state) _fit(s, s.exposureSeconds)];
    });
    // A re-read of the settings assigns new objects (they have no ==), so
    // only a real change of filters or default sub starts the rows afresh,
    // and never under a queued target's own rows.
    ref.listen(
      filterSetProvider.select(
        (s) => [for (final f in s.filters) '${f.name}/${f.kind}'].join('|'),
      ),
      (_, _) => _reseed(),
    );
    ref.listen(
      imagingDefaultsProvider.select((d) => d.defaultExposure),
      (_, _) => _reseed(),
    );
    ref.listen(filterWheelProvider.select((w) => w.value != null), (_, _) => _reseed());
    _darkHours = ref.read(simpleDarkHoursProvider);
    return _defaults();
  }

  List<FilterPlanStep> _defaults() => simpleRunDefaultPlan(
    ref.read(filterSetProvider).filters,
    ref.read(imagingDefaultsProvider).defaultExposure.inMilliseconds / 1000,
    remainingDarkHours: _darkHours,
    // A wheel the daemon retains, connected or not (readRigCapabilities' rule).
    canChangeFilters: ref.read(filterWheelProvider).value != null,
  );

  void _reseed() {
    if (!_loaded) state = _defaults();
  }

  FilterPlanStep _fit(FilterPlanStep s, double exposureSeconds) =>
      FilterPlanStep(
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
  /// Editing a queued target: its rows, to change and save back.
  void load(List<FilterPlanStep> steps) {
    _loaded = true;
    state = [...steps];
  }

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

/// The gain of the plan in Plan's sheet: seeded from the camera's when the
/// sheet opens (or the edited target's), changed there without touching the
/// camera's.
class SimpleRunGainNotifier extends Notifier<int?> {
  @override
  int? build() => null;

  /// The gain the sheet opened with: an edit compares against this, not the
  /// camera's gain now, which another client may change meanwhile (#1572).
  int? seeded;

  void set(int? g) => state = g;

  void seed(int? g) {
    seeded = g;
    state = g;
  }
}

final simpleRunGainProvider = NotifierProvider<SimpleRunGainNotifier, int?>(
  SimpleRunGainNotifier.new,
);

/// When a target's flats are shot (#1757, round 2): not at all, right after
/// its last frame (at its rotator angle), or with the queue's end-of-night
/// flats.
enum SimpleTargetFlats { none, afterTarget, endOfNight }

/// The Plan sheet's Flats choice for the target it queues.
final simpleRunFlatsProvider =
    NotifierProvider<SimpleRunFlatsNotifier, SimpleTargetFlats>(
      SimpleRunFlatsNotifier.new,
    );

class SimpleRunFlatsNotifier extends Notifier<SimpleTargetFlats> {
  @override
  SimpleTargetFlats build() => SimpleTargetFlats.none;
  void set(SimpleTargetFlats v) => state = v;
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
    this.gain,
    this.calibration = false,
    this.flats = SimpleTargetFlats.none,
  });

  final String name;

  /// When this target's flats are shot (#1757).
  final SimpleTargetFlats flats;

  /// #1757 — one of the Queue's blocks after the targets (ADR 0010 rule 4):
  /// the end-of-night flats ([kEndFlatsBlockName]) or the Calibration block
  /// (darks and bias). No sky, no edit; they stay last, flats first.
  final bool calibration;

  /// The plan's own gain (seeded from the camera's when the sheet opened);
  /// null: the profile's default. Fixed once queued: changing the camera's
  /// gain later doesn't touch a queued or running target.
  final int? gain;

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
    if (simplePlanTotal(targets[i].steps) > done[i].fold(0, (a, b) => a + b)) return i;
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
    this.startAt,
    this.dawnStop,
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

  /// A scheduled start: the run waits on the rig until then.
  final DateTime? startAt;

  /// #1560 — the dawn every target block stops at (a `TimeCondition`
  /// provider label), or null: guessed when the queue is made, settled from
  /// the start time when it starts ([SimpleRunNotifier.start]), kept by the
  /// targets added to it.
  final String? dawnStop;

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
    DateTime? startAt,
    bool startNow = false,
    // The dawn stop the queue starts with; null with [settleDawn] = none.
    String? dawnStop,
    bool settleDawn = false,
  }) => SimpleRun(
    sequenceId: sequenceId,
    targets: targets ?? this.targets,
    started: started ?? this.started,
    busy: busy ?? this.busy,
    pausing: pausing ?? this.pausing,
    error: error,
    noStorage: noStorage,
    startAt: startNow ? null : startAt ?? this.startAt,
    dawnStop: settleDawn ? dawnStop : this.dawnStop,
  );
}

/// What Plan's shutter, tracker and sheet show.
enum SimpleRunPhase { none, ready, imaging, pausing, paused, done }

bool simpleRunPaused(SequenceRunState? s) =>
    s == SequenceRunState.paused || s == SequenceRunState.pausedAwaitingUser;

/// Pure — unit-tested. A run in progress wins (Simple Mode's or one adopted
/// from the daemon); a plan this app started ends once the daemon reports it
/// finished, stopped or failed (not merely idle, which a just-started run
/// still reads).
SimpleRunPhase simpleRunPhase(SimpleRun run, SequenceRunStateInfo? info) {
  if (info?.state?.isActive ?? false) {
    if (simpleRunPaused(info!.state)) return SimpleRunPhase.paused;
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
      if (state.pausing && simpleRunPaused(next.value?.state)) {
        state = state.copyWith(pausing: false);
      }
    });
    return const SimpleRun();
  }

  /// Add to queue: the picked target joins the end of the queue (the one
  /// sequence; live when it is imaging), or makes it. [first] puts it at the
  /// front instead (Start now). A new queue stops at dawn when it starts in
  /// the dark: at [startsAt], else when Preflight starts it (at dark before
  /// dark, else now). True once on the rig.
  Future<bool> create({bool first = false, DateTime? startsAt}) async {
    final sel = ref.read(planetariumSelectionProvider);
    final steps = ref.read(simpleRunPlanProvider);
    final gain = ref.read(simpleRunGainProvider);
    final flats = ref.read(simpleRunFlatsProvider);
    if (sel == null || state.busy) return false;
    // One block per name (the rig finds a target's block by it): a queued
    // target isn't added again; Start now moves it to the front instead.
    final queued = state.targets.indexWhere((t) => t.name == sel.name);
    if (queued >= 0) {
      if (!first) {
        state = state.copyWith(
          error: l10nFor(ref.read(appLocaleProvider))
              .planAlreadyQueued(sel.name),
        );
        return false;
      }
      return queued == 0 || await reorder(queued, 0);
    }
    final queue = state.sequenceId;
    final dawn = queue != null
        ? state.dawnStop
        : dawnStopFor(
            ref.read(siteSettingsProvider),
            (startsAt ??
                    (ref.read(simpleScheduleAllowedProvider)
                        ? ref.read(simpleDarkStartProvider)
                        : null) ??
                    ref.read(clockProvider)())
                .toUtc(),
          );
    state = state.copyWith(busy: true);
    try {
      // The append goes to the selected sequence.
      if (queue != null) {
        ref.read(selectedSequenceIdProvider.notifier).select(queue);
      }
      final r = await createImagingRun(
        null,
        container: ref.container,
        raDeg: sel.raJ2000Hours * 15,
        decDeg: sel.decJ2000Deg,
        targetName: sel.name,
        jumpToRun: false,
        alwaysCenter: true,
        appendToOpen: queue != null,
        filterPlan: steps.first.filterName.isEmpty ? null : steps,
        exposureSeconds: steps.first.exposureSeconds,
        frameCount: steps.first.frameCount,
        gain: gain,
        stopAtDawn: dawn,
        recalibrateGuider: true,
        parkAtEnd: true,
        pauseForFilterSwap: false,
        flatsAfterTarget: flats == SimpleTargetFlats.afterTarget,
      );
      if (r == null || r.draft) {
        _fail(l10nFor(ref.read(appLocaleProvider)).planRigUnreachable);
        return false;
      }
      if (queue != null && r.sequenceId != queue) {
        // The daemon refused the append (the run is winding down) and a
        // second sequence was made: don't leave it behind.
        try {
          await ref
              .read(sequenceApiProvider)
              ?.deleteSequence(r.sequenceId);
        } catch (_) {}
        state = state.copyWith(
          busy: false,
          error: l10nFor(ref.read(appLocaleProvider)).planRunEnding,
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
        gain: gain,
        flats: flats,
      );
      if (queue == null) {
        state = SimpleRun(
          sequenceId: r.sequenceId,
          targets: [target],
          dawnStop: dawn,
        );
      } else if (first && state.targets.isNotEmpty) {
        await _moveBefore(queue, target.name, state.targets.first.name);
        state = state.copyWith(
          targets: [target, ...state.targets],
          busy: false,
        );
      } else if (state.targets.indexWhere((t) => t.calibration)
          case final trailing when trailing >= 0) {
        // The blocks after the targets stay after the last target.
        await _moveBefore(queue, target.name, state.targets[trailing].name);
        state = state.copyWith(
          targets: [...state.targets]..insert(trailing, target),
          busy: false,
        );
      } else {
        state = state.copyWith(
          targets: [...state.targets, target],
          busy: false,
        );
      }
      if (flats == SimpleTargetFlats.endOfNight) await _syncEndFlats();
      return true;
    } catch (e) {
      _fail(_why(e, (l) => l.planActionCreate));
      return false;
    }
  }

  // The named block (just appended, so last) goes before [before]'s: Start
  // now puts a target first, an edit puts it back. Live while it runs.
  Future<void> _moveBefore(String id, String name, String before) async {
    final api = ref.read(sequenceApiProvider);
    if (api == null) return;
    final body = (await api.getSequenceDetail(id)).body;
    final from = indexOfTargetBlock(body, name);
    final to = indexOfTargetBlock(body, before);
    if (from < 0 || to < 0 || from < to) return;
    if (ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false) {
      await api.moveRunItem(id, [from], to);
      return;
    }
    final children = childrenOf(body);
    children.insert(to, children.removeAt(from));
    await api.updateSequence(id, body: withChildren(body, children));
  }

  /// Edit target [i]'s rows (one not started yet): its block is replaced on
  /// the rig (removed, added again with [steps], moved back into place), live
  /// while imaging. True once saved.
  Future<bool> edit(
    int i,
    List<FilterPlanStep> steps, {
    int? gain,
  }) async {
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    if (api == null || id == null || i >= state.targets.length || state.busy) {
      return false;
    }
    final t = state.targets[i];
    final next = i + 1 < state.targets.length
        ? state.targets[i + 1].name
        : null;
    state = state.copyWith(busy: true);
    try {
      await _removeBlock(api, id, t.name);
      ref.read(selectedSequenceIdProvider.notifier).select(id);
      final r = await createImagingRun(
        null,
        pauseForFilterSwap: false,
        container: ref.container,
        raDeg: t.raDeg,
        decDeg: t.decDeg,
        targetName: t.name,
        jumpToRun: false,
        alwaysCenter: true,
        appendToOpen: true,
        filterPlan: steps.first.filterName.isEmpty ? null : steps,
        exposureSeconds: steps.first.exposureSeconds,
        frameCount: steps.first.frameCount,
        gain: gain,
        stopAtDawn: state.dawnStop,
        recalibrateGuider: true,
      );
      if (r == null || r.draft || r.sequenceId != id) {
        throw StateError("the rig didn't take the edited plan");
      }
      if (next != null) await _moveBefore(id, t.name, next);
      final edited = SimpleTarget(
        name: t.name,
        tag: t.tag,
        steps: steps,
        raDeg: t.raDeg,
        decDeg: t.decDeg,
        rise: t.rise,
        gain: gain,
      );
      state = state.copyWith(
        targets: [...state.targets]..[i] = edited,
        busy: false,
      );
      return true;
    } catch (e) {
      _fail(
        _why(e, (l) => l.planActionSaveEdit),
        undo: state.copyWith(targets: await _asOnRig(api, id)),
      );
      return false;
    }
  }

  /// The queue as the rig holds it, after an edit or reorder that stopped
  /// halfway: the targets still on it, in its order. The list as it was when
  /// the rig can't be read either.
  Future<List<SimpleTarget>> _asOnRig(SequenceClient api, String id) async {
    try {
      final body = (await api.getSequenceDetail(id)).body;
      final at = {
        for (final t in state.targets) t.name: indexOfTargetBlock(body, t.name),
      };
      return [
        for (final t in state.targets)
          if (at[t.name]! >= 0) t,
      ]..sort((a, b) => at[a.name]!.compareTo(at[b.name]!));
    } catch (_) {
      return state.targets;
    }
  }

  // The named target block out of the sequence: live while it runs.
  Future<void> _removeBlock(SequenceClient api, String id, String name) async {
    final body = (await api.getSequenceDetail(id)).body;
    final active =
        ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false;
    if (active) {
      final at = indexOfTargetBlock(body, name);
      if (at >= 0) await api.removeRunItem(id, [at]);
    } else if (removeTargetFromRunBody(body, name) case final next?) {
      await api.updateSequence(id, body: next);
    }
  }

  /// Put a removed target back at [at] (Undo after a swipe): added to the
  /// rig again and moved into place. True once back. Through this notifier's
  /// container, not a widget's ref: the pill outlives the queue panel.
  Future<bool> restore(SimpleTarget t, int at) async {
    final id = state.sequenceId;
    if (id == null || state.busy || t.steps.isEmpty) return false;
    state = state.copyWith(busy: true);
    try {
      ref.read(selectedSequenceIdProvider.notifier).select(id);
      final r = await createImagingRun(
        null,
        pauseForFilterSwap: false,
        container: ref.container,
        raDeg: t.raDeg,
        decDeg: t.decDeg,
        targetName: t.name,
        jumpToRun: false,
        alwaysCenter: true,
        appendToOpen: true,
        filterPlan: t.steps.first.filterName.isEmpty ? null : t.steps,
        exposureSeconds: t.steps.first.exposureSeconds,
        frameCount: t.steps.first.frameCount,
        gain: t.gain,
        stopAtDawn: state.dawnStop,
        recalibrateGuider: true,
      );
      if (r == null || r.draft || r.sequenceId != id) {
        throw StateError("the rig didn't take it back");
      }
      final at2 = at.clamp(0, state.targets.length);
      if (at2 < state.targets.length) {
        await _moveBefore(id, t.name, state.targets[at2].name);
      }
      state = state.copyWith(
        targets: [...state.targets]..insert(at2, t),
        busy: false,
      );
      return true;
    } catch (e) {
      _fail(_why(e, (l) => l.planActionPutBack));
      return false;
    }
  }

  /// Move queued target [from] to position [to] (drag to reorder), on the
  /// rig too. Only moves earlier exist there, so the new order is built
  /// front to back, each target moved before the one in its place.
  /// True once the rig has the new order.
  Future<bool> reorder(int from, int to) async {
    final id = state.sequenceId;
    final n = state.targets.length;
    if (id == null ||
        state.busy ||
        from == to ||
        from < 0 ||
        to < 0 ||
        from >= n ||
        to >= n) {
      return false;
    }
    // The blocks after the targets stay there: nothing moves among them.
    if (state.targets[from].calibration || state.targets[to].calibration) {
      return false;
    }
    final wanted = [...state.targets];
    wanted.insert(to, wanted.removeAt(from));
    state = state.copyWith(busy: true);
    try {
      final now = [for (final t in state.targets) t.name];
      for (var k = 0; k < wanted.length; k++) {
        if (now[k] == wanted[k].name) continue;
        await _moveBefore(id, wanted[k].name, now[k]);
        now.insert(k, now.removeAt(now.indexOf(wanted[k].name)));
      }
      state = state.copyWith(targets: wanted, busy: false);
      return true;
    } catch (e) {
      final api = ref.read(sequenceApiProvider);
      _fail(
        _why(e, (l) => l.planActionReorder),
        undo: api == null
            ? null
            : state.copyWith(targets: await _asOnRig(api, id)),
      );
      return false;
    }
  }

  /// #1757 — the queue's end-of-night flats follow its targets set to End
  /// of night: one block, before the Calibration block, for the filters they
  /// use; none when no target wants them. Rebuilt whole (out, then in), live
  /// while it runs.
  Future<void> _syncEndFlats() async {
    final id = state.sequenceId;
    final api = ref.read(sequenceApiProvider);
    if (id == null || api == null) return;
    final filters = {
      for (final t in state.targets)
        if (t.flats == SimpleTargetFlats.endOfNight)
          for (final s in t.steps)
            if (s.filterName.isNotEmpty) s.filterName,
    }.toList();
    var targets = [...state.targets];
    if (targets.any((t) => t.calibration && t.name == kEndFlatsBlockName)) {
      await _removeBlock(api, id, kEndFlatsBlockName);
      targets.removeWhere((t) => t.calibration && t.name == kEndFlatsBlockName);
    }
    if (filters.isNotEmpty) {
      final rig = await readRigCapabilities(ref.container);
      final block = endFlatsBlock(filters, rig);
      final body = (await api.getSequenceDetail(id)).body;
      if (ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false) {
        await api.addRunItem(
          id,
          parentPath: const [],
          index: liveAppendIndex(body),
          item: block,
        );
      } else {
        await api.updateSequence(id, body: appendTargetToRunBody(body, block));
      }
      final at = targets.indexWhere((t) => t.calibration);
      if (at >= 0) await _moveBefore(id, kEndFlatsBlockName, targets[at].name);
      targets.insert(
        at >= 0 ? at : targets.length,
        SimpleTarget(
          name: kEndFlatsBlockName,
          calibration: true,
          steps: [
            for (final f in filters)
              FilterPlanStep(
                filterName: f,
                exposureSeconds: 1,
                frameCount: kSimpleFlatFrames,
              ),
          ],
        ),
      );
    }
    state = state.copyWith(targets: targets);
  }

  /// #1757 — puts the Calibration [block] after the queue's targets: live
  /// while it runs. The queue holds one; a second is refused with the reason.
  /// True once on the rig.
  Future<bool> addCalibration(Map<String, dynamic> block) async {
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    final l = l10nFor(ref.read(appLocaleProvider));
    if (api == null || id == null || state.busy) {
      _fail(l.calibrationNoQueue);
      return false;
    }
    if (state.targets.any((t) => t.calibration)) {
      _fail(l.calibrationAlreadyQueued);
      return false;
    }
    state = state.copyWith(busy: true);
    try {
      final body = (await api.getSequenceDetail(id)).body;
      if (ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false) {
        await api.addRunItem(
          id,
          parentPath: const [],
          index: liveAppendIndex(body),
          item: block,
        );
      } else {
        await api.updateSequence(id, body: appendTargetToRunBody(body, block));
      }
      final time = calibrationBlockTime(block);
      state = state.copyWith(
        targets: [
          ...state.targets,
          SimpleTarget(
            name: kCalibrationBlockName,
            calibration: true,
            steps: [
              FilterPlanStep(
                filterName: '',
                exposureSeconds: time.frames == 0
                    ? 1
                    : math.max(0.001, time.seconds / time.frames),
                frameCount: math.max(1, time.frames),
              ),
            ],
          ),
        ],
        busy: false,
      );
      return true;
    } catch (e) {
      _fail(_why(e, (l) => l.calibrationActionQueue));
      return false;
    }
  }

  /// Remove target [i] from the queue (one not started yet): live from a
  /// running sequence, else from the stored body.
  Future<void> remove(int i) async {
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    if (api == null || id == null || i >= state.targets.length) return;
    final name = state.targets[i].name;
    try {
      await _removeBlock(api, id, name);
      final gone = state.targets[i];
      final left = [...state.targets]..removeAt(i);
      if (left.isEmpty && !state.started) {
        // Nothing left to run: the empty sequence goes too.
        await api.deleteSequence(id);
        state = const SimpleRun();
      } else {
        state = state.copyWith(targets: left);
        if (gone.flats == SimpleTargetFlats.endOfNight) await _syncEndFlats();
      }
    } catch (e) {
      _fail(_why(e, (l) => l.planActionRemove));
    }
  }

  /// Create and start (or ▶ with a target picked and no plan yet); with a
  /// queue, Start now: the target goes first. With [startAt] the run starts
  /// at once but waits on the rig until then (cooling 15 min before).
  Future<bool> createAndStart({DateTime? startAt}) async {
    final startsAt = startAt ?? ref.read(clockProvider)();
    if (!await create(first: state.sequenceId != null, startsAt: startsAt)) {
      return false;
    }
    return startQueue(startAt: startAt);
  }

  /// Starts the queue as it stands (Preflight's last Done). With [startAt]
  /// the run starts at once but waits on the rig until then.
  Future<bool> startQueue({DateTime? startAt}) async {
    // An edit or reorder still on its way: _op would skip the start quietly
    // and this would report success (review of #1486).
    if (state.busy) {
      ref
          .read(simpleMessagesProvider.notifier)
          .show(
            SimpleMessageKind.error,
            l10nFor(ref.read(appLocaleProvider)).planQueueBusy,
            origin: SimpleStatusOrigin.plan,
          );
      return false;
    }
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    final active =
        ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false;
    if (startAt != null && api != null && id != null && !active) {
      try {
        final body = (await api.getSequenceDetail(id)).body;
        final clock = await ref.read(timeSyncApiProvider)?.getState();
        await api.updateSequence(
          id,
          body: scheduleRunBody(
            body,
            startAt,
            daemonUtcOffsetMinutes: clock?.utcOffsetMinutes,
          ),
        );
      } catch (e) {
        _fail(_why(e, (l) => l.planActionSchedule));
        return false;
      }
    }
    state = state.copyWith(startAt: startAt, startNow: startAt == null);
    return start();
  }

  /// The shutter's actions. Starting settles the dawn stop first.
  Future<bool> start() async {
    if (!await _settleDawn()) return false;
    return _op(
      (api, id) => api.start(id),
      (l) => l.planActionStart,
      before: state.copyWith(started: true),
    );
  }

  /// #1560 — every target stops at dawn only when the queue starts in the
  /// dark: the daemon would read a dawn stop started in daylight as already
  /// past and skip every target. Rewrites the queue on the rig when the start
  /// differs from the guess it was made with. True when it can start.
  Future<bool> _settleDawn() async {
    final api = ref.read(sequenceApiProvider);
    final id = state.sequenceId;
    final active =
        ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false;
    if (api == null || id == null || active || state.busy) return true;
    final dawn = dawnStopFor(
      ref.read(siteSettingsProvider),
      (state.startAt ?? ref.read(clockProvider)()).toUtc(),
    );
    if (dawn == state.dawnStop) return true;
    try {
      final body = (await api.getSequenceDetail(id)).body;
      await api.updateSequence(id, body: withDawnStop(body, dawn));
      state = state.copyWith(dawnStop: dawn, settleDawn: true);
      return true;
    } catch (e) {
      _fail(_why(e, (l) => l.planActionStart));
      return false;
    }
  }
  Future<void> pause() => _op(
    (api, id) => api.pause(id),
    (l) => l.planActionPause,
    before: state.copyWith(pausing: true),
  );
  // The shutter's resume re-centers (pointing may have drifted while
  // paused); the progress panel offers the other two.
  Future<void> resume({bool recenter = true, bool refocus = false}) => _op(
    (api, id) => api.resume(id, recenter: recenter, refocus: refocus),
    (l) => l.planActionResume,
  );
  Future<void> stop() => _op((api, id) => api.stop(id), (l) => l.planActionStop);

  /// Night done's New plan: back to the sheet.
  void newPlan() => state = const SimpleRun();

  Future<bool> _op(
    Future<String> Function(SequenceClient api, String id) op,
    String Function(AppLocalizations l) action, {
    SimpleRun? before,
  }) async {
    final api = ref.read(sequenceApiProvider);
    final info = ref.read(sequenceRunStateProvider).value;
    // A run in progress (perhaps adopted from the daemon), else our plan.
    final id = (info?.state?.isActive ?? false)
        ? info!.sequenceId
        : state.sequenceId;
    // One at a time: a second tap while the first is on its way isn't sent.
    if (api == null || id == null || id.isEmpty || state.busy) return false;
    final undo = state;
    state = (before ?? state).copyWith(busy: true);
    try {
      await op(api, id);
      state = state.copyWith(busy: false);
    } catch (e) {
      _fail(
        _why(e, action),
        undo: undo,
        noStorage:
            e is DioException &&
            e.response?.data is Map &&
            e.response!.data['title'] == 'store_unavailable',
      );
      await ref.read(sequenceRunStateProvider.notifier).refresh();
      return false;
    }
    await ref.read(sequenceRunStateProvider.notifier).refresh();
    return true;
  }

  /// A failure after a rig round-trip: one pill, and the inline error stays
  /// clear. The sheet that would show it may be closed (Preflight's last
  /// Done, a Stop from the shutter); pre-flight refusals a sheet can show
  /// (already queued, run ending) set [SimpleRun.error] instead.
  void _fail(String why, {SimpleRun? undo, bool noStorage = false}) {
    state = (undo ?? state).copyWith(busy: false, noStorage: noStorage);
    // From Plan: a tap on the pill opens it (#1490).
    ref
        .read(simpleMessagesProvider.notifier)
        .show(SimpleMessageKind.error, why, origin: SimpleStatusOrigin.plan);
  }

  // [action] is picked from the app's locale, so the whole line reads in it.
  String _why(Object e, String Function(AppLocalizations l) action) {
    final l = l10nFor(ref.read(appLocaleProvider));
    return friendlyError(l, e, action: action(l));
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
      if (simpleRunPaused(prev?.value?.state) && !simpleRunPaused(next.value?.state)) {
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

