import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../ws/ws_providers.dart';

/// One guide frame as the daemon relayed it (`guider.step` WS event): the
/// star's RA/Dec offset and the pulse the guider sent back. Distances are in
/// guide-camera pixels; the arcsec pair is present once the guider knows its
/// pixel scale. Pulses are signed PHD2-style (negative = East / South).
class GuideStep {
  final DateTime at;
  final double? raPx;
  final double? decPx;
  final double? raArcsec;
  final double? decArcsec;
  final double raPulseMs;
  final double decPulseMs;
  final double? pixelScaleArcsec;
  final double? snr;

  const GuideStep({
    required this.at,
    this.raPx,
    this.decPx,
    this.raArcsec,
    this.decArcsec,
    this.raPulseMs = 0,
    this.decPulseMs = 0,
    this.pixelScaleArcsec,
    this.snr,
  });

  /// Parses a `guider.step` payload; null when it carries no usable offset
  /// (a lost-star frame reports NaN, which the daemon nulls out).
  static GuideStep? fromPayload(Map<String, dynamic> p, DateTime at) {
    final raPx = _num(p['ra_raw_px']);
    final decPx = _num(p['dec_raw_px']);
    if (raPx == null && decPx == null) return null;
    return GuideStep(
      at: at,
      raPx: raPx,
      decPx: decPx,
      raArcsec: _num(p['ra_arcsec']),
      decArcsec: _num(p['dec_arcsec']),
      raPulseMs: _num(p['ra_duration_ms']) ?? 0,
      decPulseMs: _num(p['dec_duration_ms']) ?? 0,
      pixelScaleArcsec: _num(p['pixel_scale_arcsec']),
      snr: _num(p['snr']),
    );
  }

  static double? _num(dynamic v) {
    final d = v is num ? v.toDouble() : null;
    return d != null && d.isFinite ? d : null;
  }

  /// RA offset in arcsec: the daemon's figure, else the client's own scale
  /// (§63.5 guide train), else null.
  double? raArcsecWith(double? fallbackScale) =>
      raArcsec ?? (raPx != null && fallbackScale != null ? raPx! * fallbackScale : null);

  double? decArcsecWith(double? fallbackScale) =>
      decArcsec ?? (decPx != null && fallbackScale != null ? decPx! * fallbackScale : null);

  @override
  bool operator ==(Object other) =>
      other is GuideStep &&
      other.at == at &&
      other.raPx == raPx &&
      other.decPx == decPx &&
      other.raArcsec == raArcsec &&
      other.decArcsec == decArcsec &&
      other.raPulseMs == raPulseMs &&
      other.decPulseMs == decPulseMs &&
      other.pixelScaleArcsec == pixelScaleArcsec &&
      other.snr == snr;

  @override
  int get hashCode => Object.hash(at, raPx, decPx, raArcsec, decArcsec,
      raPulseMs, decPulseMs, pixelScaleArcsec, snr);
}

/// How many guide frames the Live-tab graph keeps — PHD2's widest graph
/// setting. At a 2 s guide cadence that is ~13 minutes of history.
const int kGuideStepHistory = 400;

/// A PHD2 session event other than a step (`guider.event`): what PHD2's own
/// graph annotates — a dither, the settle window around it, a lost star —
/// plus the calibration / guiding transitions.
enum GuideMarkerKind {
  dithered,
  settling,
  settleDone,
  starLost,
  calibrationStarted,
  calibrationComplete,
  calibrationFailed,
  guidingStarted,
  guidingStopped,
  paused,
  resumed,
  lockPositionLost,
}

class GuideMarker {
  final DateTime at;
  final GuideMarkerKind kind;
  /// Dither offset (px) for [GuideMarkerKind.dithered].
  final double? dxPx;
  final double? dyPx;
  /// PHD2's status: settle_done 0 = ok; star_lost error code.
  final int? status;
  final String? error;

  const GuideMarker({
    required this.at,
    required this.kind,
    this.dxPx,
    this.dyPx,
    this.status,
    this.error,
  });

  static GuideMarkerKind? kindFromWire(String? token) => switch (token) {
        'dithered' => GuideMarkerKind.dithered,
        'settling' => GuideMarkerKind.settling,
        'settle_done' => GuideMarkerKind.settleDone,
        'star_lost' => GuideMarkerKind.starLost,
        'calibration_started' => GuideMarkerKind.calibrationStarted,
        'calibration_complete' => GuideMarkerKind.calibrationComplete,
        'calibration_failed' => GuideMarkerKind.calibrationFailed,
        'guiding_started' => GuideMarkerKind.guidingStarted,
        'guiding_stopped' => GuideMarkerKind.guidingStopped,
        'paused' => GuideMarkerKind.paused,
        'resumed' => GuideMarkerKind.resumed,
        'lock_position_lost' => GuideMarkerKind.lockPositionLost,
        _ => null,
      };

  static GuideMarker? fromPayload(Map<String, dynamic> p, DateTime at) {
    final kind = kindFromWire(p['kind'] is String ? p['kind'] as String : null);
    if (kind == null) return null;
    final status = p['status'];
    final error = p['error'];
    return GuideMarker(
      at: at,
      kind: kind,
      dxPx: GuideStep._num(p['dx_px']),
      dyPx: GuideStep._num(p['dy_px']),
      status: status is int ? status : null,
      error: error is String && error.isNotEmpty ? error : null,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is GuideMarker &&
      other.at == at &&
      other.kind == kind &&
      other.dxPx == dxPx &&
      other.dyPx == dyPx &&
      other.status == status &&
      other.error == error;

  @override
  int get hashCode => Object.hash(at, kind, dxPx, dyPx, status, error);
}

/// Markers kept alongside the step history. Bounded like the steps; a marker
/// older than the oldest step is off the graph anyway. Larger than the step
/// history: PHD2 sends a Settling event on every settle frame, so at frequent
/// dithers markers outnumber steps.
const int kGuideMarkerHistory = 1000;

/// PHD2 session markers for the active server, newest last. Root-scoped for
/// the same reason as [guideStepsProvider].
class GuideMarkerNotifier extends Notifier<List<GuideMarker>> {
  DateTime Function() now = DateTime.now;

  @override
  List<GuideMarker> build() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'guider.event') return;
      final marker = GuideMarker.fromPayload(event.payload, now());
      if (marker != null) add(marker);
    });
    return const [];
  }

  void add(GuideMarker marker) {
    final next = [...state, marker];
    if (next.length > kGuideMarkerHistory) {
      next.removeRange(0, next.length - kGuideMarkerHistory);
    }
    state = next;
  }

  void clear() => state = const [];
}

final guideMarkersProvider =
    NotifierProvider<GuideMarkerNotifier, List<GuideMarker>>(
        GuideMarkerNotifier.new);

/// Rolling guide-step history for the active server, newest last. Root-scoped
/// (not autoDispose) so the trace keeps filling while the user is on another
/// tab and is already there when they come back to Live.
class GuideStepNotifier extends Notifier<List<GuideStep>> {
  /// Injectable clock for tests (the payload's `time_sec` is the guider's
  /// epoch stamp; the client stamps on arrival so the x-axis is in its own
  /// time base, like every other live series here).
  DateTime Function() now = DateTime.now;

  @override
  List<GuideStep> build() {
    ref.listen(wsEventsProvider, (prev, next) {
      final event = next.asData?.value;
      if (event == null || event.type != 'guider.step') return;
      final step = GuideStep.fromPayload(event.payload, now());
      if (step != null) add(step);
    });
    return const [];
  }

  void add(GuideStep step) {
    final next = [...state, step];
    if (next.length > kGuideStepHistory) {
      next.removeRange(0, next.length - kGuideStepHistory);
    }
    state = next;
  }

  void clear() => state = const [];
}

final guideStepsProvider =
    NotifierProvider<GuideStepNotifier, List<GuideStep>>(GuideStepNotifier.new);
