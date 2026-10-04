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
