import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../services/polar_align_api.dart';
import '../saved_server_state.dart';
import '../ws/ws_providers.dart';

/// §45 `polar_align.*` WS event tokens (mirrors `WsEventCatalog` on the server).
abstract final class PolarAlignWsEvents {
  static const started = 'polar_align.started';
  static const stopped = 'polar_align.stopped';
  static const progress = 'polar_align.progress';
  static const frameStarted = 'polar_align.frame_started';
  static const frameComplete = 'polar_align.frame_complete';
  static const paused = 'polar_align.paused';
  static const error = 'polar_align.error';
  static const stage = 'polar_align.stage';
  static const needsHome = 'polar_align.needs_home';
  static const checked = 'polar_align.checked';
}

/// Live routine view folded from the `polar_align.*` WS stream — what the
/// bullseye panel renders between (or instead of) status polls. [phase] tracks
/// the server state strings ([PolarAlignStates]); error fields are null until
/// the first solved live iteration.
class PolarAlignLive {
  final String phase;
  final int iteration;
  final double? altErrorArcmin;
  final double? azErrorArcmin;
  final double? totalErrorArcmin;

  /// `red` | `yellow` | `green` from the last progress event.
  final String? zone;

  /// Consecutive failed solves from the last `frame_complete` (0 after a good
  /// solve) — drives the §45.11 "no solve — check sky" retry counter.
  final int consecutiveSolveFailures;

  /// Machine-readable reason + message from a fatal `polar_align.error`.
  final String? errorReason;
  final String? errorMessage;

  /// The frame being captured/solved right now: when this client saw its
  /// `frame_started` and its exposure. Null between frames.
  final DateTime? frameStartedAt;
  final double? frameExposureSeconds;

  /// The most recent finished frame (timings + solved pointing).
  final PolarAlignFrameInfo? lastFrame;

  /// [PolarAlignStages]; null before the first run.
  final String? stage;

  /// The mount cannot find Home: the routine waits for Continue.
  final bool waitingForHome;

  /// Check stage: solves done so far (0–3).
  final int checkSolvesDone;
  final PolarAlignCheckResult? checkResult;

  const PolarAlignLive({
    this.phase = PolarAlignStates.idle,
    this.iteration = 0,
    this.altErrorArcmin,
    this.azErrorArcmin,
    this.totalErrorArcmin,
    this.zone,
    this.consecutiveSolveFailures = 0,
    this.errorReason,
    this.errorMessage,
    this.frameStartedAt,
    this.frameExposureSeconds,
    this.lastFrame,
    this.stage,
    this.waitingForHome = false,
    this.checkSolvesDone = 0,
    this.checkResult,
  });

  /// True between a frame's `frame_started` and its `frame_complete`.
  bool get frameInProgress => frameStartedAt != null;

  /// True while the server runs any part of the routine.
  bool get isActive => PolarAlignStates.isActive(phase);

  PolarAlignLive copyWith({
    String? phase,
    int? iteration,
    double? altErrorArcmin,
    double? azErrorArcmin,
    double? totalErrorArcmin,
    String? zone,
    int? consecutiveSolveFailures,
    String? errorReason,
    String? errorMessage,
    DateTime? frameStartedAt,
    double? frameExposureSeconds,
    PolarAlignFrameInfo? lastFrame,
    bool clearFrameInProgress = false,
    String? stage,
    bool? waitingForHome,
    int? checkSolvesDone,
    PolarAlignCheckResult? checkResult,
  }) =>
      PolarAlignLive(
        phase: phase ?? this.phase,
        iteration: iteration ?? this.iteration,
        altErrorArcmin: altErrorArcmin ?? this.altErrorArcmin,
        azErrorArcmin: azErrorArcmin ?? this.azErrorArcmin,
        totalErrorArcmin: totalErrorArcmin ?? this.totalErrorArcmin,
        zone: zone ?? this.zone,
        consecutiveSolveFailures:
            consecutiveSolveFailures ?? this.consecutiveSolveFailures,
        errorReason: errorReason ?? this.errorReason,
        errorMessage: errorMessage ?? this.errorMessage,
        frameStartedAt: clearFrameInProgress
            ? null
            : frameStartedAt ?? this.frameStartedAt,
        frameExposureSeconds: clearFrameInProgress
            ? null
            : frameExposureSeconds ?? this.frameExposureSeconds,
        lastFrame: lastFrame ?? this.lastFrame,
        stage: stage ?? this.stage,
        waitingForHome: waitingForHome ?? this.waitingForHome,
        checkSolvesDone: checkSolvesDone ?? this.checkSolvesDone,
        checkResult: checkResult ?? this.checkResult,
      );
}

double? _num(Map<String, dynamic> payload, String key) =>
    (payload[key] as num?)?.toDouble();

/// Pure fold of one WS event into the live routine view. Returns null when the
/// event is not a `polar_align.*` event (no state write). [now] stamps a
/// `frame_started` (injectable for tests). Exposed for unit tests.
PolarAlignLive? foldPolarAlignEvent(PolarAlignLive current, WsEvent event,
    {DateTime Function() now = DateTime.now}) {
  switch (event.type) {
    case PolarAlignWsEvents.started:
      // A fresh routine: drop everything from the previous run.
      return PolarAlignLive(
        phase: event.payload['state'] is String
            ? event.payload['state'] as String
            : PolarAlignStates.seeding,
        stage: event.payload['stage'] is String
            ? event.payload['stage'] as String
            : PolarAlignStages.fine,
      );
    case PolarAlignWsEvents.stopped:
      return current.copyWith(
          phase: PolarAlignStates.stopped,
          clearFrameInProgress: true,
          waitingForHome: false);
    case PolarAlignWsEvents.paused:
      return current.copyWith(phase: PolarAlignStates.paused);
    case PolarAlignWsEvents.stage:
      return current.copyWith(
        stage: event.payload['stage'] is String
            ? event.payload['stage'] as String
            : current.stage,
        phase: event.payload['state'] is String
            ? event.payload['state'] as String
            : current.phase,
        checkSolvesDone:
            (event.payload['check_solves_done'] as num?)?.toInt() ?? 0,
        waitingForHome: false,
        clearFrameInProgress: true,
      );
    case PolarAlignWsEvents.needsHome:
      return current.copyWith(
          phase: PolarAlignStates.homing, waitingForHome: true);
    case PolarAlignWsEvents.checked:
      return current.copyWith(
        phase: PolarAlignStates.checked,
        checkSolvesDone: 3,
        checkResult: PolarAlignCheckResult.fromJson(event.payload),
        altErrorArcmin: _num(event.payload, 'altitude_error_arcmin'),
        azErrorArcmin: _num(event.payload, 'azimuth_error_arcmin'),
        totalErrorArcmin: _num(event.payload, 'total_error_arcmin'),
        zone: polarAlignZone(_num(event.payload, 'total_error_arcmin')),
        clearFrameInProgress: true,
      );
    case PolarAlignWsEvents.progress:
      return current.copyWith(
        phase: PolarAlignStates.adjusting,
        stage: event.payload['stage'] is String
            ? event.payload['stage'] as String
            : current.stage,
        waitingForHome: false,
        iteration: (event.payload['iteration'] as num?)?.toInt() ?? current.iteration,
        altErrorArcmin: _num(event.payload, 'altitude_error_arcmin'),
        azErrorArcmin: _num(event.payload, 'azimuth_error_arcmin'),
        totalErrorArcmin: _num(event.payload, 'total_error_arcmin'),
        zone: event.payload['zone'] is String ? event.payload['zone'] as String : current.zone,
        consecutiveSolveFailures: 0,
      );
    case PolarAlignWsEvents.frameStarted:
      return current.copyWith(
        frameStartedAt: now(),
        frameExposureSeconds: _num(event.payload, 'exposure_seconds'),
      );
    case PolarAlignWsEvents.frameComplete:
      final failures =
          (event.payload['consecutive_solve_failures'] as num?)?.toInt() ?? 0;
      return current.copyWith(
        consecutiveSolveFailures: failures,
        lastFrame: PolarAlignFrameInfo.fromJson(event.payload),
        clearFrameInProgress: true,
      );
    case PolarAlignWsEvents.error:
      return current.copyWith(
        phase: PolarAlignStates.failed,
        clearFrameInProgress: true,
        errorReason: event.payload['reason'] is String
            ? event.payload['reason'] as String
            : 'internal_error',
        errorMessage: event.payload['message'] is String
            ? event.payload['message'] as String
            : null,
      );
    default:
      return null;
  }
}

/// Builds a [PolarAlignClient] for a server. Overridable in tests.
final polarAlignApiFactoryProvider =
    Provider<PolarAlignClient Function(AraServer)>((ref) => PolarAlignApi.new);

/// [PolarAlignClient] bound to the active server, or `null` when none is saved.
final polarAlignApiProvider = Provider<PolarAlignClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(polarAlignApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// Live routine view for the active server, folded from `polar_align.*` WS
/// events. NOT autoDispose: a paused/failed routine must still be visible when
/// the user navigates back to the Imaging tab. A server switch resets it (the
/// stream provider rebuilds).
class PolarAlignLiveNotifier extends Notifier<PolarAlignLive> {
  @override
  PolarAlignLive build() {
    final stream = ref.watch(wsEventStreamProvider);
    if (stream == null) return const PolarAlignLive();
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null) return;
      final folded = foldPolarAlignEvent(state, event);
      if (folded != null) state = folded;
    });
    return const PolarAlignLive();
  }

  /// Hydrate [state] from a REST status snapshot (on panel open / reconnect,
  /// where the WS resume may have skipped past routine events).
  void hydrateFromStatus(PolarAlignStatus status) {
    state = liveFromStatus(status);
  }
}

/// Rebuild the live view from a REST snapshot — a full replacement, NOT a
/// copyWith: the resync exists precisely because the WS stream missed events,
/// so every field the snapshot doesn't carry (error reason, retry
/// streak, iteration) must reset rather than survive from a previous run.
/// Pure — unit-tested.
PolarAlignLive liveFromStatus(PolarAlignStatus status) => PolarAlignLive(
      phase: status.state,
      altErrorArcmin: status.altitudeAdjustmentArcmin,
      azErrorArcmin: status.azimuthAdjustmentArcmin,
      totalErrorArcmin: status.currentErrorArcmin,
      zone: polarAlignZone(status.currentErrorArcmin),
      lastFrame: status.lastFrame,
      stage: status.stage,
      waitingForHome: status.waitingForHome,
      checkSolvesDone: status.checkSolvesDone,
      checkResult: status.checkResult,
    );

/// The zone for a total error, by the daemon's own rule (PolarAlignService
/// PublishProgressAsync) — the REST snapshot carries the error but not the zone.
String? polarAlignZone(double? totalArcmin) => totalArcmin == null
    ? null
    : totalArcmin < 10.0
        ? 'green'
        : totalArcmin < 60.0
            ? 'yellow'
            : 'red';

final polarAlignLiveProvider =
    NotifierProvider<PolarAlignLiveNotifier, PolarAlignLive>(
        PolarAlignLiveNotifier.new);
