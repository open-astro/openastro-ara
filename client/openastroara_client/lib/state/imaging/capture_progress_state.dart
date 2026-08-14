import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../equipment/camera_state.dart';

/// Capture lifecycle for the Imaging tab's "Take One" — from the exposure
/// POST through the daemon's background pipeline (expose → download → FITS →
/// catalog) to the frame landing. Drives the progress UI: the exposing phase
/// tracks the camera's own `exposure_progress_pct`, the downloading phase
/// covers the post-exposure window until the frame is registered.
enum CapturePhase { idle, exposing, downloading, done, failed }

/// How long each phase is guaranteed visible even on a very fast rig, so the
/// exposing → downloading → ready sequence never flashes by unseen.
const Duration kExposingMinVisible = Duration(milliseconds: 500);
const Duration kDownloadingMinVisible = Duration(milliseconds: 800);

/// The download estimate used before the first capture has been measured —
/// rigs vary wildly (sub-second locally, 10-30s on a slow bridge), so a
/// middle ground keeps "ready in" honest from the very first shot.
const Duration kDefaultDownloadEstimate = Duration(seconds: 2);

class CaptureProgress {
  final CapturePhase phase;
  final String? frameId;
  final Duration requestedExposure;
  /// 0..100 from the daemon's camera runtime while exposing (null = unknown).
  final double? exposureProgressPct;
  final DateTime? startedAt;
  final DateTime? exposureEndedAt;
  final String? error;
  /// Rolling average (ms) of how long post-exposure processing takes
  /// (download → FITS → catalog) — measured per capture and carried across
  /// captures so the "ready in" estimate is grounded in this rig's real speed.
  final int? rollingDownloadMs;
  /// Identity of the current capture cycle. Bumped on every `beginExposing`
  /// and `reset()`; `complete()`/`fail()` from a stale `_takeOne` poll loop
  /// (e.g. after a Cancel) no-op when their generation no longer matches, so
  /// a superseded cycle can't clobber a newer capture's card.
  final int generation;

  const CaptureProgress({
    this.phase = CapturePhase.idle,
    this.frameId,
    this.requestedExposure = Duration.zero,
    this.exposureProgressPct,
    this.startedAt,
    this.exposureEndedAt,
    this.error,
    this.rollingDownloadMs,
    this.generation = 0,
  });

  bool get isActive => phase != CapturePhase.idle;

  /// The progress to DISPLAY: the daemon's real percentage when it has one,
  /// otherwise a local elapsed-time estimate (elapsed / requested × 100,
  /// capped at 99 so the bar visibly completes only when the daemon confirms)
  /// — the daemon's equipment poll is slow (15 s), so a short exposure would
  /// otherwise sit at 0% the whole time.
  double? get displayProgressPct {
    if (phase != CapturePhase.exposing) return null;
    final pct = exposureProgressPct;
    if (pct != null) return pct;
    final started = startedAt;
    if (started == null || requestedExposure == Duration.zero) return 0;
    final elapsed = DateTime.now().difference(started);
    final frac = elapsed.inMilliseconds / requestedExposure.inMilliseconds;
    return (frac * 100).clamp(0.0, 99.0);
  }

  /// Seconds left in the exposure, derived from the daemon's progress
  /// percentage (falls back to the display estimate). Null when not exposing.
  Duration? get exposureRemaining {
    if (phase != CapturePhase.exposing) return null;
    final pct = displayProgressPct;
    if (pct == null) return null;
    final done = pct / 100.0;
    return requestedExposure * (1 - done);
  }

  /// Seconds elapsed in the download phase (exposure done → frame registered).
  Duration? get downloadElapsed {
    if (phase != CapturePhase.downloading) return null;
    final end = exposureEndedAt;
    if (end == null) return null;
    return DateTime.now().difference(end);
  }

  /// The rig's typical post-exposure processing time (rolling average). Null
  /// until the first capture has been measured.
  Duration? get downloadEstimate => rollingDownloadMs == null
      ? null
      : Duration(milliseconds: rollingDownloadMs!);

  /// Estimated seconds until the frame is on screen. During exposing it's the
  /// exposure remaining + the download estimate; during downloading it's the
  /// estimate minus what has already elapsed. Falls back to
  /// [kDefaultDownloadEstimate] before the first capture has been measured.
  Duration? get timeToDisplay {
    final est = downloadEstimate ?? kDefaultDownloadEstimate;
    switch (phase) {
      case CapturePhase.exposing:
        final remaining = exposureRemaining;
        if (remaining == null) return null;
        return remaining + est;
      case CapturePhase.downloading:
        final elapsed = downloadElapsed;
        if (elapsed == null || elapsed >= est) return Duration.zero;
        return est - elapsed;
      default:
        return null;
    }
  }

  CaptureProgress copyWith({
    CapturePhase? phase,
    String? frameId,
    Duration? requestedExposure,
    double? exposureProgressPct,
    bool clearProgressPct = false,
    DateTime? startedAt,
    DateTime? exposureEndedAt,
    String? error,
    int? rollingDownloadMs,
    int? generation,
  }) =>
      CaptureProgress(
        phase: phase ?? this.phase,
        frameId: frameId ?? this.frameId,
        requestedExposure: requestedExposure ?? this.requestedExposure,
        exposureProgressPct:
            clearProgressPct ? null : (exposureProgressPct ?? this.exposureProgressPct),
        startedAt: startedAt ?? this.startedAt,
        exposureEndedAt: exposureEndedAt ?? this.exposureEndedAt,
        error: error ?? this.error,
        rollingDownloadMs: rollingDownloadMs ?? this.rollingDownloadMs,
        generation: generation ?? this.generation,
      );
}

class CaptureProgressNotifier extends Notifier<CaptureProgress> {
  Timer? _exposeHold;
  Timer? _downloadHold;
  Timer? _resetTimer;

  /// How long the terminal states stay on screen: done flashes briefly,
  /// failed lingers long enough to read the reason and hit Retry.
  static const Duration doneVisible = Duration(milliseconds: 1800);
  static const Duration failedVisible = Duration(seconds: 6);

  @override
  CaptureProgress build() {
    ref.onDispose(() {
      _exposeHold?.cancel();
      _downloadHold?.cancel();
      _resetTimer?.cancel();
    });
    // Drive the exposing phase from the camera's own exposure progress —
    // no extra polling needed; the equipment status already refreshes it.
    ref.listen(cameraStatusProvider, (prev, next) {
      final cam = next.maybeWhen(data: (s) => s, orElse: () => null);
      updateExposureProgress(cam?.exposureProgressPct);
    });
    return const CaptureProgress();
  }

  /// Take One accepted (or about to POST) — the camera starts exposing.
  /// Carries the rolling download estimate across captures so "ready in"
  /// is grounded from the very start.
  void beginExposing(Duration exposure) {
    // A fresh cycle must not inherit hold timers from the previous one — a
    // stale expose-hold (armed when the daemon reported 100% within the
    // min-visible window) would fire against the new cycle's state and force
    // it straight into downloading before it has actually progressed.
    _exposeHold?.cancel();
    _downloadHold?.cancel();
    _resetTimer?.cancel();
    state = CaptureProgress(
      phase: CapturePhase.exposing,
      requestedExposure: exposure,
      exposureProgressPct: 0,
      startedAt: DateTime.now(),
      rollingDownloadMs: state.rollingDownloadMs,
      generation: state.generation + 1,
    );
  }

  /// Feed the camera's exposure progress (0..100) into the exposing phase.
  /// At 100% the phase moves to [CapturePhase.downloading] (the daemon is now
  /// downloading + writing the FITS), but only after the exposing phase has
  /// been visible at least [kExposingMinVisible] — a fast rig would otherwise
  /// jump straight to done with nothing on screen. Ignored outside exposing.
  void updateExposureProgress(double? pct) {
    if (state.phase != CapturePhase.exposing) return;
    if (pct != null && pct >= 100) {
      final started = state.startedAt ?? DateTime.now();
      final elapsed = DateTime.now().difference(started);
      if (elapsed < kExposingMinVisible) {
        _exposeHold?.cancel();
        _exposeHold = Timer(kExposingMinVisible - elapsed, () {
          if (state.phase == CapturePhase.exposing) _enterDownloading();
        });
      } else {
        _enterDownloading();
      }
    } else {
      state = state.copyWith(exposureProgressPct: pct);
    }
  }

  void _enterDownloading() {
    state = state.copyWith(
      phase: CapturePhase.downloading,
      exposureProgressPct: 100,
      exposureEndedAt: DateTime.now(),
    );
  }

  /// True while [generation] is still the notifier's current cycle. A
  /// cancelled or superseded cycle (reset/beginExposing happened after) is
  /// stale — its late complete()/fail() calls must not touch the card.
  bool isCurrent(int generation) => state.generation == generation;

  /// The generation of the cycle `beginExposing` just started — the identity
  /// a `_takeOne` poll loop carries so its late complete()/fail() no-op.
  int get currentGeneration => state.generation;

  /// The frame was registered in the catalog — the capture landed. Measures
  /// how long the post-exposure processing took (download → FITS → catalog)
  /// and folds it into the rolling estimate used for "ready in". No-ops if
  /// [generation] is stale (the cycle was cancelled or superseded).
  void complete(String frameId, {required int generation}) {
    if (!isCurrent(generation)) return;
    // Hold the downloading phase visible at least kDownloadingMinVisible even
    // when the frame registered in a few hundred ms.
    final ended = state.exposureEndedAt;
    if (ended != null &&
        state.phase == CapturePhase.downloading &&
        DateTime.now().difference(ended) < kDownloadingMinVisible) {
      _downloadHold?.cancel();
      _downloadHold = Timer(
          kDownloadingMinVisible - DateTime.now().difference(ended),
          () => _finishComplete(frameId, generation));
      return;
    }
    _finishComplete(frameId, generation);
  }

  void _finishComplete(String frameId, int generation) {
    if (!isCurrent(generation)) return;
    int? updatedRolling;
    final ended = state.exposureEndedAt;
    if (ended != null) {
      final measured = DateTime.now().difference(ended).inMilliseconds;
      final prev = state.rollingDownloadMs;
      // EMA-ish (α ≈ 0.25): the estimate tracks the rig's real speed without
      // bouncing wildly on one slow/fast capture.
      updatedRolling = prev == null
          ? measured
          : ((prev * 3) + measured) ~/ 4;
    }
    state = state.copyWith(
      phase: CapturePhase.done,
      frameId: frameId,
      rollingDownloadMs: updatedRolling ?? state.rollingDownloadMs,
    );
    _scheduleReset(doneVisible);
  }

  void fail(String error, {int? generation}) {
    // A stale cycle's late failure must not resurrect a card the user
    // already dismissed (cancel) or clobber a newer capture's progress.
    if (generation != null && !isCurrent(generation)) return;
    // Terminal state — the phase is leaving active, so any pending hold
    // timers are stale and must not fire against a later cycle.
    _exposeHold?.cancel();
    _downloadHold?.cancel();
    state = state.copyWith(phase: CapturePhase.failed, error: error);
    _scheduleReset(failedVisible);
  }

  /// Auto-clear the terminal card after [after] — the notifier owns the
  /// lifecycle so the Imaging tab doesn't have to.
  void _scheduleReset(Duration after) {
    _resetTimer?.cancel();
    _resetTimer = Timer(after, reset);
  }

  void reset() {
    _exposeHold?.cancel();
    _downloadHold?.cancel();
    _resetTimer?.cancel();
    state = CaptureProgress(
      rollingDownloadMs: state.rollingDownloadMs,
      generation: state.generation + 1,
    );
  }
}

final captureProgressProvider =
    NotifierProvider<CaptureProgressNotifier, CaptureProgress>(
        CaptureProgressNotifier.new);
