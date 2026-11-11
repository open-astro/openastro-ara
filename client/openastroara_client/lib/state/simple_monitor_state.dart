import 'dart:async';
import 'dart:math' as math;

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/equipment_device_status.dart';
import '../models/frame_histogram.dart';
import '../models/guider_status.dart';
import '../models/sequence/sequence_summary.dart';
import '../services/storage_space_api.dart';
import '../util/tonight_sky_local.dart';
import '../widgets/imaging/histogram_strip.dart' show frameHistogramProvider;
import 'equipment/camera_state.dart';
import 'equipment/focuser_state.dart';
import 'equipment/mount_state.dart';
import 'equipment/safety_monitor_state.dart';
import 'equipment/weather_state.dart';
import 'focus/autofocus_live_state.dart';
import 'guider/guide_step_state.dart';
import 'guider/guider_state.dart';
import 'sequencer/sequence_list_state.dart';
import 'settings/safety_policies_state.dart';
import 'settings/site_settings_state.dart';
import 'simple_messages_state.dart';
import 'simple_run_state.dart';
import 'sky_atlas/tonight_sky_state.dart';
import 'ws/ws_providers.dart';

// Monitor (#1724): Simple Mode's full-screen, read-only view of the Session.
// Every rule of the screen lives here; the screen renders [MonitorSnapshot]
// and holds no logic, timers or fetches of its own (ADR 0013).

/// How a tile or the top line is marked. A healthy tile has no dot, so any
/// dot means something changed (DESIGN.md: Monitor's exceptions).
enum MonitorStatus {
  /// All well: no dot, a grey word.
  normal,

  /// Its device works normally: no dot, the word in green.
  working,

  /// On its way between two states: a blue ring.
  changing,
  warning,
  fault,

  /// Off, parked or stale: a grey dot.
  off,
}

/// The top line's condition, the first that applies in this order (#1724).
enum MonitorVerdictKind {
  linkLost,
  deviceDropped,
  unsafe,
  question,
  starLost,
  lowDisk,
  cooler,
  flipping,
  waitingForFlip,
  pausedByAra,
  waitingForYou,
  waitingForDark,
  noPlan,
  allGood,
}

/// The gear a top line can name.
enum MonitorDevice { mount, camera, guider, focuser, safetyMonitor, weather }

class MonitorVerdict {
  const MonitorVerdict(
    this.kind,
    this.status, {
    this.device,
    this.breach,
    this.question,
    this.freeBytes,
    this.coolerTargetC,
    this.coolerSensorC,
    this.at,
    this.target,
    this.text,
  });

  final MonitorVerdictKind kind;
  final MonitorStatus status;
  final MonitorDevice? device;

  /// The weather reading past its limit, when weather made it Unsafe.
  final ({WeatherBreach kind, double value, double limit})? breach;
  final SimpleMessage? question;
  final int? freeBytes;
  final double? coolerTargetC;
  final double? coolerSensorC;

  /// Link lost: since when; waiting for dark: when the plan starts.
  final DateTime? at;
  final String? target;

  /// The daemon's own words (a pause's reason, the flip's current step).
  final String? text;
}

/// The Now tile: what the rig is doing and the frame in hand.
class MonitorNow {
  const MonitorNow({
    required this.status,
    required this.word,
    this.target,
    this.filter,
    this.subSeconds,
    this.frameEndsAt,
    this.frameSeconds,
    this.countdownTo,
    this.instruction,
    this.frame,
    this.frames,
    this.targetSecondsLeft,
    this.filters = const [],
  });

  final MonitorStatus status;
  final MonitorNowWord word;
  final String? target;
  final String? filter;
  final double? subSeconds;

  /// The frame in hand ends then; null when no frame is exposing.
  final DateTime? frameEndsAt;
  final double? frameSeconds;

  /// A wait's end (dark, the plan's start, the meridian), counted down in
  /// place of a frame.
  final DateTime? countdownTo;

  /// The daemon's current step, for words that need it (slewing, the flip).
  final String? instruction;
  final int? frame;
  final int? frames;
  final double? targetSecondsLeft;

  /// Frames done per filter on this Target, in plan order.
  final List<({String filter, int done, int total, bool inHand})> filters;
}

enum MonitorNowWord {
  exposing,
  working,
  flipping,
  waitingForFlip,
  pausedByAra,
  waitingForYou,
  waitingToStart,
  idle,
  lastKnown,
}

enum MonitorUpKind { nextFrame, meridianFlip, nextTarget, park, best }

/// One Up next row; [at] null reads "then".
class MonitorUpNext {
  const MonitorUpNext(this.kind, {this.at, this.name, this.frame, this.altitudeDeg});
  final MonitorUpKind kind;
  final DateTime? at;
  final String? name;
  final int? frame;
  final double? altitudeDeg;
}

class MonitorTarget {
  const MonitorTarget({
    required this.name,
    this.longName,
    required this.status,
    required this.word,
    this.imagingTo,
    this.maxAltitudeDeg,
    this.transit,
    this.moonPct,
    this.curve = const [],
    this.spanStart,
    this.spanEnd,
  });

  final String name;
  final String? longName;
  final MonitorStatus status;
  final MonitorTargetWord word;
  final DateTime? imagingTo;
  final double? maxAltitudeDeg;
  final DateTime? transit;
  final int? moonPct;

  /// Altitude over the night, evenly spaced from dark to dawn.
  final List<double> curve;
  final DateTime? spanStart;
  final DateTime? spanEnd;
}

enum MonitorTargetWord { tracking, queued, parked }

/// The meridian flip tile, from the run's flip field (#1597).
class MonitorFlip {
  const MonitorFlip({required this.state, this.at, this.step});

  /// `due_at`, `waiting_for_exposure`, `flipping`, `auto_flip_off`,
  /// `none_tonight`; null with no active run.
  final String? state;
  final DateTime? at;

  /// While flipping: the run's current step.
  final String? step;
}

class MonitorSafety {
  const MonitorSafety({required this.status, required this.safe, this.since});
  final MonitorStatus status;

  /// Null when the monitor isn't connected.
  final bool? safe;
  final DateTime? since;
}

enum MonitorWeatherCell { cloud, skyQuality, air, dewMargin, humidity, rain, wind }

class MonitorWeather {
  const MonitorWeather({required this.status, required this.cells, this.windDeg, this.gustKmh});
  final MonitorStatus status;

  /// Only what the station reports, each with its limit when past it.
  final List<({MonitorWeatherCell cell, double value, double? overLimit})> cells;
  final double? windDeg;
  final double? gustKmh;
}

class MonitorGuider {
  const MonitorGuider({
    required this.status,
    required this.word,
    this.rms,
    this.ra,
    this.dec,
    this.trace = const [],
    this.lastDither,
  });

  final MonitorStatus status;
  final MonitorGuiderWord word;
  final double? rms;
  final double? ra;
  final double? dec;

  /// RA and Dec error (″), oldest first.
  final List<({double ra, double dec})> trace;
  final DateTime? lastDither;
}

enum MonitorGuiderWord { guiding, starLost, disconnected, idle, working }

/// One saved frame's quality, for the Last frame trends.
class MonitorFrame {
  const MonitorFrame({
    required this.id,
    this.hfr,
    this.stars,
    this.background,
    this.clippedShare,
    this.width,
    this.height,
  });

  final String id;
  final double? hfr;
  final int? stars;

  /// The frame's median, in ADU.
  final double? background;

  /// `clipped_stars / (stars + clipped_stars)` (#1723); null when not counted.
  final double? clippedShare;
  final int? width;
  final int? height;
}

class MonitorLastFrame {
  const MonitorLastFrame({this.frames = const [], this.autofocusAt, this.tempSinceAutofocus, this.focuser = false});

  /// This run's saved frames, oldest first, at most [kMonitorTrendFrames].
  final List<MonitorFrame> frames;
  final DateTime? autofocusAt;
  final double? tempSinceAutofocus;

  /// The rig has a focuser: the autofocus line shows.
  final bool focuser;

  MonitorFrame? get last => frames.isEmpty ? null : frames.last;
}

/// Tonight's timeline.
class MonitorNight {
  const MonitorNight({this.darkStart, this.darkEnd, this.blocks = const [], this.flipAt, this.parkAt, this.imagedSeconds = 0});
  final DateTime? darkStart;
  final DateTime? darkEnd;
  final List<({String name, DateTime start, DateTime end, double? progress})> blocks;
  final DateTime? flipAt;
  final DateTime? parkAt;
  final double imagedSeconds;
}

class MonitorSnapshot {
  const MonitorSnapshot({
    required this.asOf,
    required this.verdict,
    required this.now,
    this.frozenSince,
    this.upNext = const [],
    this.tonightsBest = false,
    this.target,
    required this.flip,
    this.safety,
    this.weather,
    this.guider,
    required this.lastFrame,
    required this.night,
  });

  /// When the numbers were read: countdowns count from here, so they stop
  /// while the link is lost.
  final DateTime asOf;

  /// The link to the rig was lost then: every number is the last known.
  final DateTime? frozenSince;
  final MonitorVerdict verdict;
  final MonitorNow now;
  final List<MonitorUpNext> upNext;

  /// No plan: [upNext] is tonight's best Targets.
  final bool tonightsBest;
  final MonitorTarget? target;
  final MonitorFlip flip;

  /// Null when the profile has none: no tile.
  final MonitorSafety? safety;
  final MonitorWeather? weather;
  final MonitorGuider? guider;
  final MonitorLastFrame lastFrame;
  final MonitorNight night;

  bool get frozen => frozenSince != null;
}

/// Trends keep this many saved frames of the current run.
const kMonitorTrendFrames = 200;

/// The cooler warns once the sensor has been this far from its target...
const kMonitorCoolerMarginC = 1.0;

/// ...for this long in a row.
const kMonitorCoolerHold = Duration(minutes: 5);

/// Monitor is on screen: the message pills over pushed pages stay off, as
/// its top line carries the most important message and its question.
class MonitorShowing extends Notifier<bool> {
  @override
  bool build() => false;
  void set(bool on) => state = on;
}

final monitorShowingProvider = NotifierProvider<MonitorShowing, bool>(MonitorShowing.new);

/// Monitor's clock: one tick a second while Monitor shows.
class MonitorClock extends Notifier<DateTime> {
  @override
  DateTime build() {
    final now = ref.watch(clockProvider);
    final timer = Timer.periodic(const Duration(seconds: 1), (_) => state = now());
    ref.onDispose(timer.cancel);
    return now();
  }
}

final monitorClockProvider = NotifierProvider.autoDispose<MonitorClock, DateTime>(MonitorClock.new);

/// When the link to the rig went down; null while it is up.
class MonitorLinkDown extends Notifier<DateTime?> {
  @override
  DateTime? build() =>
      ref.watch(serverLinkUpProvider) ? null : stateOrNull ?? ref.read(clockProvider)();
}

final monitorLinkDownProvider = NotifierProvider<MonitorLinkDown, DateTime?>(MonitorLinkDown.new);

/// The gear seen connected since Simple Mode opened: only these count as
/// dropped when they disconnect. The shell keeps it alive.
class MonitorSeenConnected extends Notifier<Set<MonitorDevice>> {
  @override
  Set<MonitorDevice> build() {
    bool up(EquipmentDeviceStatus? d) => d?.connectionState == EquipmentConnectionState.connected;
    final now = {
      if (up(ref.watch(mountProvider).value)) MonitorDevice.mount,
      if (up(ref.watch(cameraStatusProvider).value)) MonitorDevice.camera,
      if (ref.watch(guiderStatusProvider).value?.isConnected ?? false) MonitorDevice.guider,
      if (up(ref.watch(focuserProvider).value)) MonitorDevice.focuser,
      if (up(ref.watch(safetyMonitorProvider).value)) MonitorDevice.safetyMonitor,
      if (up(ref.watch(weatherProvider).value)) MonitorDevice.weather,
    };
    final before = stateOrNull ?? const {};
    return before.containsAll(now) ? before : {...before, ...now};
  }
}

final monitorSeenConnectedProvider =
    NotifierProvider<MonitorSeenConnected, Set<MonitorDevice>>(MonitorSeenConnected.new);

/// Since when, during a run, the sensor has been more than
/// [kMonitorCoolerMarginC] from its target; null while it holds.
class MonitorCoolerOff extends Notifier<DateTime?> {
  @override
  DateTime? build() {
    final cam = ref.watch(cameraStatusProvider).value;
    final active = ref.watch(sequenceRunStateProvider.select((s) => s.value?.state?.isActive ?? false));
    final ccd = cam?.ccdTemperature, target = cam?.coolerSetpointC;
    final off = active &&
        cam != null &&
        cam.coolerOn &&
        ccd != null &&
        target != null &&
        (ccd - target).abs() > kMonitorCoolerMarginC;
    return off ? stateOrNull ?? ref.read(clockProvider)() : null;
  }
}

final monitorCoolerOffProvider = NotifierProvider<MonitorCoolerOff, DateTime?>(MonitorCoolerOff.new);

/// The current run's saved frames and their quality, from when Simple Mode
/// opens: one per `frame.complete`, filled in again on its `frame.analyzed`.
/// A new run starts over. The shell keeps it alive while Simple Mode shows.
class MonitorTrends extends Notifier<List<MonitorFrame>> {
  String? _runId;

  @override
  List<MonitorFrame> build() {
    ref.listen(sequenceRunStateProvider.select((s) => s.value?.runId), (_, run) {
      if (run != null && run.isNotEmpty && run != _runId) {
        _runId = run;
        state = const [];
      }
    });
    ref.listen(wsEventsProvider, (_, next) {
      final e = next.asData?.value;
      if (e == null || (e.type != 'frame.complete' && e.type != 'frame.analyzed')) return;
      final id = e.payload['frame_id'];
      // Only a run's frames: a shot or Live View is not the night's trend.
      final active = ref.read(sequenceRunStateProvider).value?.state?.isActive ?? false;
      if (id is String && id.isNotEmpty && active) unawaited(_read(id));
    });
    return const [];
  }

  Future<void> _read(String id) async {
    try {
      final sub = ref.listen(frameHistogramProvider(id).future, (_, _) {});
      final FrameHistogram h;
      try {
        h = await sub.read();
      } finally {
        sub.close();
      }
      final stars = h.stars, clipped = h.clippedStars;
      add(MonitorFrame(
        id: id,
        hfr: h.hfr,
        stars: stars,
        background: h.median,
        clippedShare: monitorClippedShare(stars, clipped),
        width: h.width,
        height: h.height,
      ));
    } catch (_) {
      // A frame that can't be read leaves no point; the next one will.
    }
  }

  /// One frame, replacing an earlier read of the same frame.
  void add(MonitorFrame f) {
    final i = state.indexWhere((x) => x.id == f.id);
    final next = [...state];
    i >= 0 ? next[i] = f : next.add(f);
    state = next.length > kMonitorTrendFrames ? next.sublist(next.length - kMonitorTrendFrames) : next;
  }
}

final monitorTrendsProvider = NotifierProvider<MonitorTrends, List<MonitorFrame>>(MonitorTrends.new);

/// The share of a frame's stars that clipped (#1723): null when the daemon
/// didn't count them, 0 with no stars at all.
double? monitorClippedShare(int? stars, int? clipped) {
  if (clipped == null) return null;
  final all = (stars ?? 0) + clipped;
  return all == 0 ? 0 : clipped / all;
}

/// The snapshot Monitor renders. While the link is lost it keeps the last
/// live one, frozen, with the age on the top line.
class MonitorState extends Notifier<MonitorSnapshot> {
  Object? _darkKey;
  (DateTime?, DateTime?)? _dark;
  Object? _curveKey;
  ({List<double> curve, double? max, DateTime? transit})? _curve;

  @override
  MonitorSnapshot build() {
    final now = ref.watch(monitorClockProvider);
    final down = ref.watch(monitorLinkDownProvider);
    // Opened while the link is down: the last values the client holds.
    final last = down == null ? null : stateOrNull ?? _live(now, down);
    if (down != null && last != null) {
      final since = last.frozenSince ?? down;
      return MonitorSnapshot(
        asOf: last.asOf,
        frozenSince: since,
        verdict: MonitorVerdict(MonitorVerdictKind.linkLost, MonitorStatus.fault, at: since),
        now: last.now,
        upNext: last.upNext,
        tonightsBest: last.tonightsBest,
        target: last.target,
        flip: last.flip,
        safety: last.safety,
        weather: last.weather,
        guider: last.guider,
        lastFrame: last.lastFrame,
        night: last.night,
      );
    }
    return _live(now, down);
  }

  MonitorSnapshot _live(DateTime now, DateTime? down) {
    final info = ref.watch(sequenceRunStateProvider).value;
    final run = ref.watch(simpleRunProvider);
    final cam = ref.watch(cameraStatusProvider).value;
    final mount = ref.watch(mountProvider).value;
    final focuser = ref.watch(focuserProvider).value;
    final guider = ref.watch(guiderStatusProvider).value;
    final safetyMon = ref.watch(safetyMonitorProvider).value;
    final weatherSt = ref.watch(weatherProvider).value;
    final policies = ref.watch(safetyPoliciesProvider);
    final messages = ref.watch(simpleMessagesProvider);
    final storage = ref.watch(storageSpaceProvider).value;
    final coolerOff = ref.watch(monitorCoolerOffProvider);
    final trends = ref.watch(monitorTrendsProvider);
    final site = ref.watch(siteSettingsProvider);
    ref.watch(skyClockProvider);

    final active = info?.state?.isActive ?? false;
    final paused = info?.state == SequenceRunState.paused;
    final awaitingUser = info?.state == SequenceRunState.pausedAwaitingUser;
    final flipping = active && info?.meridianFlip?.state == 'flipping';
    final waitLeft = active ? info?.meridianWaitLeft : null;
    final startAt = !active && run.startAt != null && run.startAt!.isAfter(now) ? run.startAt : null;
    final ours = run.sequenceId != null && info?.sequenceId == run.sequenceId;
    final targets = ours || !run.started ? run.targets : const <SimpleTarget>[];
    final captured = info?.framesCaptured ?? 0;
    final progress = simpleTargetProgress(targets, captured);
    final cur = targets.isEmpty ? -1 : simpleCurrentTarget(targets, captured);
    final target = cur < 0 ? null : targets[cur];
    final done = cur < 0 ? const <int>[] : progress[cur];
    final step = target == null ? -1 : _currentStep(target, done);
    double leftOn(int i) => [
          for (var r = 0; r < targets[i].steps.length; r++)
            (targets[i].steps[r].frameCount - progress[i][r]) * targets[i].steps[r].exposureSeconds,
        ].fold(0.0, (a, b) => a + b);
    double doneOn(int i) => [
          for (var r = 0; r < targets[i].steps.length; r++) progress[i][r] * targets[i].steps[r].exposureSeconds,
        ].fold(0.0, (a, b) => a + b);

    // The frame in hand, from the camera's own progress (#1755).
    final exposing = active && !paused && !awaitingUser && cam != null && cam.runtimeState == 'exposing';
    final sub = step < 0 ? null : target!.steps[step].exposureSeconds;
    final pct = cam?.exposureProgressPct;
    final frameEndsAt = exposing && sub != null && pct != null
        ? now.add(Duration(milliseconds: (sub * (1 - pct.clamp(0, 100) / 100) * 1000).round()))
        : null;

    // Gear: a tile for what the profile has (the daemon reports a device
    // only when its slot is filled), red when it has dropped.
    // Dropped: in error, or disconnected after it was connected this
    // session. Not yet connected tonight is off, not a fault.
    final seen = ref.watch(monitorSeenConnectedProvider);
    bool dropped(MonitorDevice which, EquipmentDeviceStatus? d) =>
        d != null &&
        (d.connectionState == EquipmentConnectionState.error ||
            (d.connectionState == EquipmentConnectionState.disconnected && seen.contains(which)));
    final guiderDropped = guider != null &&
        (guider.connectionState == GuiderConnectionState.error ||
            (guider.connectionState == GuiderConnectionState.disconnected && seen.contains(MonitorDevice.guider)));
    final droppedDevice = dropped(MonitorDevice.mount, mount)
        ? MonitorDevice.mount
        : dropped(MonitorDevice.camera, cam)
        ? MonitorDevice.camera
        : guiderDropped
        ? MonitorDevice.guider
        : dropped(MonitorDevice.focuser, focuser)
        ? MonitorDevice.focuser
        : dropped(MonitorDevice.safetyMonitor, safetyMon)
        ? MonitorDevice.safetyMonitor
        : dropped(MonitorDevice.weather, weatherSt)
        ? MonitorDevice.weather
        : null;

    final breaches = weatherSt != null && weatherSt.isConnected && policies.weatherTriggersEnabled
        ? weatherBreaches(weatherSt, policies)
        : const <({WeatherBreach kind, double value, double limit})>[];
    final unsafeMonitor = safetyMon != null && safetyMon.isConnected && !safetyMon.safe;
    final question = messages.where((m) => m.kind == SimpleMessageKind.question).firstOrNull;
    final starLost = guider != null && guider.isConnected && guider.runtimeState == GuiderRuntimeState.starLost;

    // Low disk: the Queue's frames left need more than the drive has free,
    // at two bytes a pixel of the last frame's size.
    final lastDims = trends.lastWhere((f) => f.width != null && f.height != null, orElse: () => const MonitorFrame(id: ''));
    final framesLeft = [
      for (var i = 0; i < targets.length; i++)
        for (var r = 0; r < targets[i].steps.length; r++) targets[i].steps[r].frameCount - progress[i][r],
    ].fold(0, (a, b) => a + b);
    final free = storage?.freeBytes;
    final lowDisk = free != null &&
        lastDims.width != null &&
        free < framesLeft * lastDims.width! * lastDims.height! * 2;
    final cooler = coolerOff != null && now.difference(coolerOff) >= kMonitorCoolerHold;

    final verdict = droppedDevice != null
        ? MonitorVerdict(MonitorVerdictKind.deviceDropped, MonitorStatus.fault, device: droppedDevice)
        : unsafeMonitor || breaches.isNotEmpty
        ? MonitorVerdict(MonitorVerdictKind.unsafe, MonitorStatus.warning, breach: breaches.firstOrNull)
        : question != null
        ? MonitorVerdict(MonitorVerdictKind.question, MonitorStatus.warning, question: question)
        : starLost
        ? const MonitorVerdict(MonitorVerdictKind.starLost, MonitorStatus.warning)
        : lowDisk
        ? MonitorVerdict(MonitorVerdictKind.lowDisk, MonitorStatus.warning, freeBytes: free)
        : cooler
        ? MonitorVerdict(MonitorVerdictKind.cooler, MonitorStatus.warning,
            coolerTargetC: cam?.coolerSetpointC, coolerSensorC: cam?.ccdTemperature)
        : flipping
        ? MonitorVerdict(MonitorVerdictKind.flipping, MonitorStatus.normal, text: info?.currentInstructionDescription)
        : waitLeft != null
        ? MonitorVerdict(MonitorVerdictKind.waitingForFlip, MonitorStatus.changing,
            at: now.add(Duration(seconds: waitLeft)))
        : paused
        ? MonitorVerdict(MonitorVerdictKind.pausedByAra, MonitorStatus.changing, text: info?.reasonText)
        : awaitingUser
        ? MonitorVerdict(MonitorVerdictKind.waitingForYou, MonitorStatus.warning, text: info?.reasonText)
        : startAt != null
        ? MonitorVerdict(MonitorVerdictKind.waitingForDark, MonitorStatus.changing, at: startAt)
        : !active
        ? const MonitorVerdict(MonitorVerdictKind.noPlan, MonitorStatus.normal)
        : MonitorVerdict(MonitorVerdictKind.allGood, MonitorStatus.normal,
            target: target?.tag ?? target?.name ?? info?.currentTargetName);

    // Now.
    final filters = target == null
        ? const <({String filter, int done, int total, bool inHand})>[]
        : [
            for (var r = 0; r < target.steps.length; r++)
              (filter: target.steps[r].filterName, done: done[r], total: target.steps[r].frameCount, inHand: active && r == step),
          ];
    final (word, nowStatus) = flipping
        ? (MonitorNowWord.flipping, MonitorStatus.working)
        : waitLeft != null
        ? (MonitorNowWord.waitingForFlip, MonitorStatus.changing)
        : paused
        ? (MonitorNowWord.pausedByAra, MonitorStatus.changing)
        : awaitingUser
        ? (MonitorNowWord.waitingForYou, MonitorStatus.warning)
        : exposing
        ? (MonitorNowWord.exposing, MonitorStatus.working)
        : active
        ? (MonitorNowWord.working, MonitorStatus.working)
        : startAt != null
        ? (MonitorNowWord.waitingToStart, MonitorStatus.changing)
        : (MonitorNowWord.idle, MonitorStatus.off);
    final nowTile = MonitorNow(
      status: nowStatus,
      word: word,
      target: target?.tag ?? target?.name ?? info?.currentTargetName,
      filter: step < 0 ? null : target!.steps[step].filterName,
      subSeconds: sub,
      frameEndsAt: frameEndsAt,
      frameSeconds: frameEndsAt == null ? null : sub,
      countdownTo: waitLeft != null ? now.add(Duration(seconds: waitLeft)) : startAt,
      instruction: info?.currentInstructionDescription,
      frame: target == null ? null : done.fold<int>(0, (a, b) => a + b) + (active ? 1 : 0),
      frames: target == null ? null : simplePlanTotal(target.steps),
      targetSecondsLeft: cur < 0 ? null : leftOn(cur),
      filters: filters,
    );

    // Up next, and the times the timeline shares.
    final flipInfo = active ? info?.meridianFlip : null;
    final flipAt = flipInfo?.state == 'due_at' ? flipInfo?.expectedUtc?.toLocal() : null;
    final runLeft = info?.estimatedRemainingSeconds;
    final parkAt = active && ours && runLeft != null ? now.add(Duration(seconds: runLeft.round())) : null;
    final upNext = <MonitorUpNext>[];
    var best = false;
    if (active && target != null) {
      final nextFrame = done.fold<int>(0, (a, b) => a + b) + 2;
      if (nextFrame <= simplePlanTotal(target.steps)) {
        final nextStep = _currentStep(target, [
          for (var r = 0; r < done.length; r++) r == step ? done[r] + 1 : done[r],
        ]);
        if (nextStep >= 0) {
          upNext.add(MonitorUpNext(MonitorUpKind.nextFrame,
              at: frameEndsAt, frame: nextFrame, name: target.steps[nextStep].filterName));
        }
      }
      if (flipAt != null) upNext.add(MonitorUpNext(MonitorUpKind.meridianFlip, at: flipAt));
      if (cur + 1 < targets.length) {
        final next = targets[cur + 1];
        upNext.add(MonitorUpNext(MonitorUpKind.nextTarget,
            name: next.tag ?? next.name, at: now.add(Duration(seconds: leftOn(cur).round()))));
      }
      if (ours) upNext.add(MonitorUpNext(MonitorUpKind.park, at: parkAt));
      // Known times in order; a "then" row stays right after the row before it.
      final timed = upNext.where((u) => u.at != null).toList()..sort((a, b) => a.at!.compareTo(b.at!));
      for (var i = 0; i < upNext.length; i++) {
        if (upNext[i].at == null) timed.insert(i == 0 ? 0 : timed.indexOf(upNext[i - 1]) + 1, upNext[i]);
      }
      upNext
        ..clear()
        ..addAll(timed);
    } else if (!active && startAt == null) {
      best = true;
      final sky = ref.watch(tonightSkyProvider).value ?? const [];
      for (final o in sky.take(5)) {
        upNext.add(MonitorUpNext(MonitorUpKind.best, name: o.name, altitudeDeg: o.altitudeDeg));
      }
    }

    // Tonight's dark, for the timeline and the altitude curve.
    // Worked out once a minute, not on every 1 s tick (a minute-by-minute
    // sun scan).
    final darkKey = (site, now.millisecondsSinceEpoch ~/ 60000);
    if (darkKey != _darkKey) {
      _darkKey = darkKey;
      _dark = monitorDarkSpan(site, now.toUtc());
    }
    final (darkStart, darkEnd) = _dark!;

    // Target.
    MonitorTarget? targetTile;
    if (target != null) {
      // The curve changes only with the Target or the night.
      final curveKey = (target.raDeg, target.decDeg, site.latitudeDeg, site.longitudeDeg, darkStart, darkEnd);
      if (curveKey != _curveKey) {
        _curveKey = curveKey;
        final points = <double>[];
        double? top;
        DateTime? at;
        if (darkStart != null && darkEnd != null && (site.latitudeDeg != 0 || site.longitudeDeg != 0)) {
          const n = 48;
          final span = darkEnd.difference(darkStart);
          for (var i = 0; i <= n; i++) {
            final t = darkStart.add(span * (i / n));
            final alt = altitudeDegAt(target.raDeg, target.decDeg,
                latitudeDeg: site.latitudeDeg, longitudeDeg: site.longitudeDeg, utc: t.toUtc());
            points.add(alt);
            if (top == null || alt > top) {
              top = alt;
              at = t;
            }
          }
        }
        _curve = (curve: points, max: top, transit: at);
      }
      final (:curve, max: maxAlt, :transit) = _curve!;
      final spent = doneOn(cur);
      targetTile = MonitorTarget(
        name: target.tag ?? target.name,
        longName: target.tag == null || target.tag == target.name ? null : target.name,
        status: mount?.parked ?? false
            ? MonitorStatus.off
            : active && (mount?.tracking ?? false)
            ? MonitorStatus.working
            : MonitorStatus.normal,
        word: mount?.parked ?? false
            ? MonitorTargetWord.parked
            : active && (mount?.tracking ?? false)
            ? MonitorTargetWord.tracking
            : MonitorTargetWord.queued,
        imagingTo: active ? now.add(Duration(seconds: leftOn(cur).round())) : null,
        maxAltitudeDeg: maxAlt,
        transit: transit,
        moonPct: (moonPhase(now.toUtc()).illuminatedFraction * 100).round(),
        curve: curve,
        spanStart: active ? now.subtract(Duration(seconds: spent.round())) : null,
        spanEnd: active ? now.add(Duration(seconds: leftOn(cur).round())) : null,
      );
    }

    // Safety, weather and the guider: a tile only for gear in the profile.
    final safety = safetyMon == null
        ? null
        : MonitorSafety(
            status: !safetyMon.isConnected
                ? (dropped(MonitorDevice.safetyMonitor, safetyMon) ? MonitorStatus.fault : MonitorStatus.off)
                : safetyMon.safe
                ? MonitorStatus.normal
                : MonitorStatus.warning,
            safe: safetyMon.isConnected ? safetyMon.safe : null,
            since: safetyMon.lastTransitionAt?.toLocal(),
          );
    MonitorWeather? weather;
    if (weatherSt != null) {
      double? over(WeatherBreach k) => breaches.where((b) => b.kind == k).firstOrNull?.limit;
      final w = weatherSt;
      final t = w.temperatureC, dew = w.dewPointC;
      final windKmh = w.windSpeedMs == null ? null : w.windSpeedMs! * 3.6;
      bool ok(double? v) => v != null && v.isFinite;
      weather = MonitorWeather(
        status: !w.isConnected
            ? (dropped(MonitorDevice.weather, w) ? MonitorStatus.fault : MonitorStatus.off)
            : breaches.isNotEmpty
            ? MonitorStatus.warning
            : MonitorStatus.normal,
        cells: [
          if (ok(w.cloudCoverPct)) (cell: MonitorWeatherCell.cloud, value: w.cloudCoverPct!, overLimit: null),
          if (ok(w.skyQualityMagArcsec2))
            (cell: MonitorWeatherCell.skyQuality, value: w.skyQualityMagArcsec2!, overLimit: null),
          if (ok(t)) (cell: MonitorWeatherCell.air, value: t!, overLimit: null),
          if (ok(t) && ok(dew))
            (cell: MonitorWeatherCell.dewMargin, value: t! - dew!, overLimit: over(WeatherBreach.dewMargin)),
          if (ok(w.humidityPct))
            (cell: MonitorWeatherCell.humidity, value: w.humidityPct!, overLimit: over(WeatherBreach.humidity)),
          if (ok(w.rainRate)) (cell: MonitorWeatherCell.rain, value: w.rainRate!, overLimit: null),
          if (ok(windKmh)) (cell: MonitorWeatherCell.wind, value: windKmh!, overLimit: over(WeatherBreach.wind)),
        ],
        windDeg: w.windDirectionDeg,
        gustKmh: w.windGustMs == null ? null : w.windGustMs! * 3.6,
      );
    }
    MonitorGuider? guiderTile;
    if (guider != null) {
      final steps = ref.watch(guideStepsProvider);
      final markers = ref.watch(guideMarkersProvider);
      final (gWord, gStatus) = guiderDropped
          ? (MonitorGuiderWord.disconnected, MonitorStatus.fault)
          : !guider.isConnected
          ? (MonitorGuiderWord.disconnected, MonitorStatus.off)
          : starLost
          ? (MonitorGuiderWord.starLost, MonitorStatus.warning)
          : guider.isCorrecting
          ? (MonitorGuiderWord.guiding, MonitorStatus.working)
          : guider.isBusy
          ? (MonitorGuiderWord.working, MonitorStatus.changing)
          : (MonitorGuiderWord.idle, MonitorStatus.normal);
      guiderTile = MonitorGuider(
        status: gStatus,
        word: gWord,
        rms: guider.isCorrecting ? guider.rmsTotalArcsec : null,
        ra: guider.isCorrecting ? guider.rmsRaArcsec : null,
        dec: guider.isCorrecting ? guider.rmsDecArcsec : null,
        trace: [
          for (final s in steps.length > 100 ? steps.sublist(steps.length - 100) : steps)
            if (s.raArcsec != null && s.decArcsec != null) (ra: s.raArcsec!, dec: s.decArcsec!),
        ],
        lastDither: markers.where((m) => m.kind == GuideMarkerKind.dithered).lastOrNull?.at.toLocal(),
      );
    }

    // Last frame.
    final af = ref.watch(autofocusLiveProvider).run;
    final afTemp = af.focuserTemperatureC, liveTemp = focuser?.temperature;
    final lastFrame = MonitorLastFrame(
      frames: trends,
      focuser: focuser != null,
      autofocusAt: af.completedUtc?.toLocal(),
      tempSinceAutofocus: afTemp != null && liveTemp != null ? liveTemp - afTemp : null,
    );

    // Tonight.
    final blocks = <({String name, DateTime start, DateTime end, double? progress})>[];
    if (targets.isNotEmpty) {
      // Finished targets end where their time ran out; the rest follow on.
      var t = now.subtract(Duration(seconds: [for (var i = 0; i <= math.max(cur, 0); i++) doneOn(i)].fold(0.0, (a, b) => a + b).round()));
      for (var i = 0; i < targets.length; i++) {
        final len = Duration(seconds: (doneOn(i) + leftOn(i)).round());
        final total = simplePlanTotal(targets[i].steps);
        blocks.add((
          name: targets[i].tag ?? targets[i].name,
          start: t,
          end: t.add(len),
          progress: i == cur && active && total > 0 ? progress[i].fold<int>(0, (a, b) => a + b) / total : null,
        ));
        t = t.add(len);
      }
    }
    final night = MonitorNight(
      darkStart: darkStart,
      darkEnd: darkEnd,
      blocks: blocks,
      flipAt: flipAt,
      parkAt: parkAt,
      imagedSeconds: [for (var i = 0; i < targets.length; i++) doneOn(i)].fold(0.0, (a, b) => a + b),
    );

    return MonitorSnapshot(
      asOf: now,
      verdict: verdict,
      now: nowTile,
      upNext: upNext.take(5).toList(),
      tonightsBest: best,
      target: targetTile,
      flip: MonitorFlip(
        state: flipInfo?.state,
        at: flipInfo?.expectedUtc?.toLocal(),
        step: flipping ? info?.currentInstructionDescription : null,
      ),
      safety: safety,
      weather: weather,
      guider: guiderTile,
      lastFrame: lastFrame,
      night: night,
    );
  }
}

final monitorProvider = NotifierProvider.autoDispose<MonitorState, MonitorSnapshot>(MonitorState.new);

/// The row of [t]'s plan the next frame comes from: the first not yet full.
int _currentStep(SimpleTarget t, List<int> done) {
  if (t.steps.isEmpty) return -1;
  for (var r = 0; r < t.steps.length; r++) {
    if (done[r] < t.steps[r].frameCount) return r;
  }
  return math.max(0, t.steps.length - 1);
}

/// Tonight's dark at [site] (local): from the sun going below its twilight
/// line to it coming back. While it is dark, the dark in progress. Nulls with
/// no site set, or when it doesn't get dark within a day.
(DateTime?, DateTime?) monitorDarkSpan(SiteSettings site, DateTime nowUtc) {
  if (site.latitudeDeg == 0 && site.longitudeDeg == 0) return (null, null);
  final line = twilightSunAltitudeDeg(site.twilightDefinition);
  bool dark(DateTime t) => sunMoonAltitudeDeg(t, site.latitudeDeg, site.longitudeDeg).sunAltDeg < line;
  final now = DateTime.utc(nowUtc.year, nowUtc.month, nowUtc.day, nowUtc.hour, nowUtc.minute);
  DateTime? start;
  if (dark(now)) {
    start = now;
    for (var m = 1; m <= 24 * 60 && dark(now.subtract(Duration(minutes: m))); m++) {
      start = now.subtract(Duration(minutes: m));
    }
  } else {
    for (var m = 1; m <= 24 * 60; m++) {
      final t = now.add(Duration(minutes: m));
      if (dark(t)) {
        start = t;
        break;
      }
    }
  }
  if (start == null) return (null, null);
  return (start.toLocal(), nightEnd(site, start)?.toLocal());
}
