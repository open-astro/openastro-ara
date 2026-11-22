import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../../models/server.dart';
import '../../models/ws_event.dart';
import '../../models/ws_event_types.dart';
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
  static const correction = WsEventTypes.polarAlignCorrection;
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

  /// #2072 the 1σ of [totalErrorArcmin] (arcmin): the fit the readout rests
  /// on. Null with no readout and from an older daemon.
  final double? totalSigmaArcmin;

  /// `red` | `yellow` | `green` from the last progress event.
  final String? zone;

  /// Consecutive failed solves from the last `frame_complete` (0 after a good
  /// solve) — drives the §45.11 "no solve — check sky" retry counter.
  final int consecutiveSolveFailures;

  /// Machine-readable reason + message from a fatal `polar_align.error`.
  final String? errorReason;
  final String? errorMessage;

  /// #2107 what stopped the mount when [errorReason] is
  /// `slew_aborted_externally` (the event's `stopped_by`: `user_request`,
  /// `emergency_stop`, `pad_stop`, `fault_reaction`, …); null when the daemon
  /// did not say. The card words the end from it in the app's language.
  final String? errorStoppedBy;

  /// #2066 the run could not confirm its stop after the error (the event's
  /// `stop_unconfirmed`): the mount may still move. The card that words the
  /// end itself adds the cut-power warning the daemon's message carries.
  final bool errorStopUnconfirmed;

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

  /// Check stage: solves the daemon takes (`check_solves_total`); 3 unless it says otherwise.
  final int checkSolvesTotal;
  final PolarAlignCheckResult? checkResult;

  /// #1971 the Polar Aligner's run; null without one Ara drives, before its
  /// first measurement, and once the routine ends.
  final PolarAlignerRun? correction;

  /// #2072 a caution on the live readout ([PolarAlignReadoutCautions]) from
  /// the last progress event; null when the readout is sound.
  final String? readoutCaution;

  /// #2082 the run's camera ([PolarAlignCameras]) from `started` or the
  /// status; null before a run or from an older daemon.
  final String? camera;

  /// #2082 a point of the measurement stayed scattered over 3″ after its
  /// retake (from each progress event and the status).
  final bool measurementNoisy;

  /// #2083 this stage's first readout (the seed's), for NINA's large- and
  /// huge-error warnings; null before it and after a hydrate.
  final double? initialErrorArcmin;

  /// #2081 the mount's Dec spread across the seed's three points (arcsec)
  /// when over 2″, from the last progress event; null when it held.
  final double? decSpreadArcsec;

  const PolarAlignLive({
    this.phase = PolarAlignStates.idle,
    this.iteration = 0,
    this.altErrorArcmin,
    this.azErrorArcmin,
    this.totalErrorArcmin,
    this.totalSigmaArcmin,
    this.zone,
    this.consecutiveSolveFailures = 0,
    this.errorReason,
    this.errorMessage,
    this.errorStoppedBy,
    this.errorStopUnconfirmed = false,
    this.frameStartedAt,
    this.frameExposureSeconds,
    this.lastFrame,
    this.stage,
    this.waitingForHome = false,
    this.checkSolvesDone = 0,
    this.checkSolvesTotal = 3,
    this.checkResult,
    this.correction,
    this.readoutCaution,
    this.camera,
    this.measurementNoisy = false,
    this.initialErrorArcmin,
    this.decSpreadArcsec,
  });

  /// #2082 the run shoots the main camera (Rough first, or the profile's
  /// camera): the guider has no picture of it to show.
  bool get usesMainCamera =>
      stage == PolarAlignStages.coarse || camera == PolarAlignCameras.main;

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
    double? totalSigmaArcmin,
    bool clearSigma = false,
    String? zone,
    int? consecutiveSolveFailures,
    String? errorReason,
    String? errorMessage,
    String? errorStoppedBy,
    bool clearErrorStoppedBy = false,
    bool? errorStopUnconfirmed,
    DateTime? frameStartedAt,
    double? frameExposureSeconds,
    PolarAlignFrameInfo? lastFrame,
    bool clearFrameInProgress = false,
    bool clearReadout = false,
    String? stage,
    bool? waitingForHome,
    int? checkSolvesDone,
    int? checkSolvesTotal,
    PolarAlignCheckResult? checkResult,
    PolarAlignerRun? correction,
    bool clearCorrection = false,
    String? readoutCaution,
    bool clearReadoutCaution = false,
    bool? measurementNoisy,
    double? initialErrorArcmin,
    double? decSpreadArcsec,
    bool clearDecSpread = false,
  }) =>
      PolarAlignLive(
        phase: phase ?? this.phase,
        iteration: iteration ?? this.iteration,
        altErrorArcmin: clearReadout ? null : altErrorArcmin ?? this.altErrorArcmin,
        azErrorArcmin: clearReadout ? null : azErrorArcmin ?? this.azErrorArcmin,
        totalErrorArcmin: clearReadout ? null : totalErrorArcmin ?? this.totalErrorArcmin,
        totalSigmaArcmin: clearReadout || clearSigma ? null : totalSigmaArcmin ?? this.totalSigmaArcmin,
        zone: clearReadout ? null : zone ?? this.zone,
        consecutiveSolveFailures:
            consecutiveSolveFailures ?? this.consecutiveSolveFailures,
        errorReason: errorReason ?? this.errorReason,
        errorMessage: errorMessage ?? this.errorMessage,
        errorStoppedBy: clearErrorStoppedBy
            ? null
            : errorStoppedBy ?? this.errorStoppedBy,
        errorStopUnconfirmed: errorStopUnconfirmed ?? this.errorStopUnconfirmed,
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
        checkSolvesTotal: checkSolvesTotal ?? this.checkSolvesTotal,
        checkResult: checkResult ?? this.checkResult,
        correction: clearCorrection ? null : correction ?? this.correction,
        // The readout's own caution goes with it.
        readoutCaution: clearReadout || clearReadoutCaution ? null : readoutCaution ?? this.readoutCaution,
        camera: camera,
        measurementNoisy: clearReadout ? false : measurementNoisy ?? this.measurementNoisy,
        // A new stage's readout starts afresh: so does its first readout.
        initialErrorArcmin: clearReadout ? null : initialErrorArcmin ?? this.initialErrorArcmin,
        decSpreadArcsec: clearReadout || clearDecSpread ? null : decSpreadArcsec ?? this.decSpreadArcsec,
      );
}

double? _num(Map<String, dynamic> payload, String key) =>
    (payload[key] as num?)?.toDouble();

String? _str(Map<String, dynamic> payload, String key) =>
    payload[key] is String ? payload[key] as String : null;

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
        camera: _str(event.payload, 'camera'),
      );
    case PolarAlignWsEvents.stopped:
      // The run's end clears the correction without an event of its own.
      return current.copyWith(
          phase: PolarAlignStates.stopped,
          clearFrameInProgress: true,
          waitingForHome: false,
          clearCorrection: true);
    case PolarAlignWsEvents.paused:
      return current.copyWith(phase: PolarAlignStates.paused);
    case PolarAlignWsEvents.stage:
      final nextStage = event.payload['stage'] is String
          ? event.payload['stage'] as String
          : current.stage;
      return current.copyWith(
        stage: nextStage,
        phase: event.payload['state'] is String
            ? event.payload['state'] as String
            : current.phase,
        checkSolvesDone:
            (event.payload['check_solves_done'] as num?)?.toInt() ?? 0,
        checkSolvesTotal:
            (event.payload['check_solves_total'] as num?)?.toInt() ?? current.checkSolvesTotal,
        waitingForHome: false,
        clearFrameInProgress: true,
        // A new stage measures afresh: the previous stage's readout (and its
        // green) must not stand for it while it homes and seeds.
        clearReadout: nextStage != current.stage,
      );
    case PolarAlignWsEvents.needsHome:
      return current.copyWith(
          phase: PolarAlignStates.homing, waitingForHome: true);
    case PolarAlignWsEvents.checked:
      return current.copyWith(
        phase: PolarAlignStates.checked,
        checkSolvesDone: current.checkSolvesTotal,
        checkResult: PolarAlignCheckResult.fromJson(event.payload),
        altErrorArcmin: _num(event.payload, 'altitude_error_arcmin'),
        azErrorArcmin: _num(event.payload, 'azimuth_error_arcmin'),
        totalErrorArcmin: _num(event.payload, 'total_error_arcmin'),
        totalSigmaArcmin: _num(event.payload, 'total_sigma_arcmin'),
        // #2072 review — each readout says its σ afresh: one without (an older
        // daemon) shows none rather than an earlier fit's.
        clearSigma: _num(event.payload, 'total_sigma_arcmin') == null,
        zone: polarAlignZone(_num(event.payload, 'total_error_arcmin')),
        clearFrameInProgress: true,
      );
    case PolarAlignWsEvents.progress:
      final iteration = (event.payload['iteration'] as num?)?.toInt();
      return current.copyWith(
        phase: PolarAlignStates.adjusting,
        // Iteration 0 is the seed's readout: the error the stage started from.
        initialErrorArcmin: iteration == 0 ? _num(event.payload, 'total_error_arcmin') : null,
        stage: event.payload['stage'] is String
            ? event.payload['stage'] as String
            : current.stage,
        waitingForHome: false,
        iteration: iteration ?? current.iteration,
        altErrorArcmin: _num(event.payload, 'altitude_error_arcmin'),
        azErrorArcmin: _num(event.payload, 'azimuth_error_arcmin'),
        totalErrorArcmin: _num(event.payload, 'total_error_arcmin'),
        totalSigmaArcmin: _num(event.payload, 'total_sigma_arcmin'),
        // #2072 review — each readout says its σ afresh: one without (an older
        // daemon) shows none rather than an earlier fit's.
        clearSigma: _num(event.payload, 'total_sigma_arcmin') == null,
        zone: event.payload['zone'] is String ? event.payload['zone'] as String : current.zone,
        consecutiveSolveFailures: 0,
        // Every progress event says the readout's caution afresh (absent = sound).
        readoutCaution: _str(event.payload, 'readout_caution'),
        clearReadoutCaution: _str(event.payload, 'readout_caution') == null,
        measurementNoisy: event.payload['measurement_noisy'] == true,
        // #2081 the same for the Dec-spread warning.
        decSpreadArcsec: _num(event.payload, 'dec_spread_arcsec'),
        clearDecSpread: _num(event.payload, 'dec_spread_arcsec') == null,
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
        clearCorrection: true,
        errorReason: event.payload['reason'] is String
            ? event.payload['reason'] as String
            : 'internal_error',
        errorMessage: event.payload['message'] is String
            ? event.payload['message'] as String
            : null,
        // Each error says its own: a stop's name never carries over to the next.
        errorStoppedBy: _str(event.payload, 'stopped_by'),
        clearErrorStoppedBy: _str(event.payload, 'stopped_by') == null,
        errorStopUnconfirmed: event.payload['stop_unconfirmed'] == true,
      );
    case PolarAlignWsEvents.correction:
      final run = PolarAlignerRun.tryParse(event.payload);
      return run == null ? null : current.copyWith(correction: run);
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

  /// Seed the live view from REST when a panel opens: the WS stream may have
  /// skipped past routine events fired before this client connected. Best
  /// effort; the panel still follows the WS stream. Returns the snapshot.
  Future<PolarAlignStatus?> hydrate() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return null;
    try {
      final status = await api.getStatus();
      if (status != null && ref.mounted) hydrateFromStatus(status);
      return status;
    } catch (_) {
      return null;
    }
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
      totalSigmaArcmin: status.currentSigmaArcmin,
      zone: polarAlignZone(status.currentErrorArcmin),
      lastFrame: status.lastFrame,
      stage: status.stage,
      waitingForHome: status.waitingForHome,
      checkSolvesDone: status.checkSolvesDone,
      checkResult: status.checkResult,
      correction: status.correction,
      readoutCaution: status.readoutCaution,
      camera: status.camera,
      measurementNoisy: status.measurementNoisy,
      decSpreadArcsec: status.decSpreadArcsec,
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
