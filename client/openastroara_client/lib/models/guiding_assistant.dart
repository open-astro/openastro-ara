/// #1364 item 4 — the guider's Guiding Assistant as Ara shows it
/// (`GET /api/v1/equipment/guider/live/assistant`): the unguided-drift
/// measurement PHD2's dialog runs, headless (openastro-guider #83), its
/// figures once two samples are in, and its recommendations once done.
class GuidingAssistantStatus {
  final bool connected;

  /// idle | measuring | measuring_backlash | done | failed.
  final String state;
  final double? durationSec;
  final bool? measureBacklash;
  final double? elapsedSec;
  final int? samples;
  final double? pixelScale;
  final double? raDriftPxMin;
  final double? decDriftPxMin;
  final double? raRmsPx;
  final double? decRmsPx;
  final double? highFreqRmsPx;
  final double? raPeakPx;
  final double? decPeakPx;
  final double? raPeakPeakPx;
  final double? raMaxDriftPxSec;
  final double? snr;
  final double? starMass;
  final double? polarAlignErrorArcmin;
  final bool? polarAlignErrorIsLowerBound;
  final String? backlashStatus;
  final String? backlashResult;
  final double? backlashMs;
  final double? backlashSigmaMs;
  final double? backlashPx;
  final GuidingAssistantRecommendations? recommendations;
  final String? error;

  const GuidingAssistantStatus({
    this.connected = false,
    this.state = 'idle',
    this.durationSec,
    this.measureBacklash,
    this.elapsedSec,
    this.samples,
    this.pixelScale,
    this.raDriftPxMin,
    this.decDriftPxMin,
    this.raRmsPx,
    this.decRmsPx,
    this.highFreqRmsPx,
    this.raPeakPx,
    this.decPeakPx,
    this.raPeakPeakPx,
    this.raMaxDriftPxSec,
    this.snr,
    this.starMass,
    this.polarAlignErrorArcmin,
    this.polarAlignErrorIsLowerBound,
    this.backlashStatus,
    this.backlashResult,
    this.backlashMs,
    this.backlashSigmaMs,
    this.backlashPx,
    this.recommendations,
    this.error,
  });

  static const disconnected = GuidingAssistantStatus();

  bool get isRunning => state == 'measuring' || state == 'measuring_backlash';
  bool get isDone => state == 'done';
  bool get isFailed => state == 'failed';
  bool get hasStats => raRmsPx != null;

  /// Progress through the sampling leg, 0..1, or null before it starts.
  double? get progress {
    final d = durationSec;
    final e = elapsedSec;
    if (d == null || e == null || d <= 0) return null;
    return (e / d).clamp(0.0, 1.0);
  }

  /// Convert a guide-pixel figure to arcseconds when the scale is known.
  double? arcsec(double? px) =>
      px == null || pixelScale == null ? null : px * pixelScale!;

  factory GuidingAssistantStatus.fromJson(Map<String, dynamic> json) {
    double? num_(String k) {
      final v = json[k];
      return v is num && v.isFinite ? v.toDouble() : null;
    }

    bool? flag(String k) => json[k] is bool ? json[k] as bool : null;
    String? str(String k) {
      final v = json[k];
      return v is String && v.isNotEmpty ? v : null;
    }

    final recs = json['recommendations'];
    final samples = json['samples'];
    return GuidingAssistantStatus(
      connected: json['connected'] as bool? ?? false,
      state: str('state') ?? 'idle',
      durationSec: num_('duration_sec'),
      measureBacklash: flag('measure_backlash'),
      elapsedSec: num_('elapsed_sec'),
      samples: samples is num ? samples.toInt() : null,
      pixelScale: num_('pixel_scale'),
      raDriftPxMin: num_('ra_drift_px_min'),
      decDriftPxMin: num_('dec_drift_px_min'),
      raRmsPx: num_('ra_rms_px'),
      decRmsPx: num_('dec_rms_px'),
      highFreqRmsPx: num_('high_freq_rms_px'),
      raPeakPx: num_('ra_peak_px'),
      decPeakPx: num_('dec_peak_px'),
      raPeakPeakPx: num_('ra_peak_peak_px'),
      raMaxDriftPxSec: num_('ra_max_drift_px_sec'),
      snr: num_('snr'),
      starMass: num_('star_mass'),
      polarAlignErrorArcmin: num_('polar_align_error_arcmin'),
      polarAlignErrorIsLowerBound: flag('polar_align_error_is_lower_bound'),
      backlashStatus: str('backlash_status'),
      backlashResult: str('backlash_result'),
      backlashMs: num_('backlash_ms'),
      backlashSigmaMs: num_('backlash_sigma_ms'),
      backlashPx: num_('backlash_px'),
      recommendations: recs is Map<String, dynamic>
          ? GuidingAssistantRecommendations.fromJson(recs)
          : null,
      error: str('error'),
    );
  }
}

/// One recommendation the user can accept: its apply name (what
/// `POST …/assistant/apply` takes), a label and the value as text.
class GuidingAssistantSuggestion {
  final String key;
  final String label;
  final String value;
  const GuidingAssistantSuggestion(this.key, this.label, this.value);
}

class GuidingAssistantRecommendations {
  final double? minMoveRaPx;
  final double? minMoveDecPx;
  final bool? minMoveSeeingBased;
  final double? exposureSec;
  final double? exposureMaxSec;
  final double? driftLimitingExposureSec;
  final double? decBacklashMs;
  final String? decGuideMode;
  final String? decAlgorithm;
  final List<GuidingAssistantMessage> messages;

  const GuidingAssistantRecommendations({
    this.minMoveRaPx,
    this.minMoveDecPx,
    this.minMoveSeeingBased,
    this.exposureSec,
    this.exposureMaxSec,
    this.driftLimitingExposureSec,
    this.decBacklashMs,
    this.decGuideMode,
    this.decAlgorithm,
    this.messages = const [],
  });

  factory GuidingAssistantRecommendations.fromJson(Map<String, dynamic> json) {
    double? num_(String k) {
      final v = json[k];
      return v is num && v.isFinite ? v.toDouble() : null;
    }

    String? str(String k) {
      final v = json[k];
      return v is String && v.isNotEmpty ? v : null;
    }

    final raw = json['messages'];
    return GuidingAssistantRecommendations(
      minMoveRaPx: num_('min_move_ra_px'),
      minMoveDecPx: num_('min_move_dec_px'),
      minMoveSeeingBased: json['min_move_seeing_based'] is bool
          ? json['min_move_seeing_based'] as bool
          : null,
      exposureSec: num_('exposure_sec'),
      exposureMaxSec: num_('exposure_max_sec'),
      driftLimitingExposureSec: num_('drift_limiting_exposure_sec'),
      decBacklashMs: num_('dec_backlash_ms'),
      decGuideMode: str('dec_guide_mode'),
      decAlgorithm: str('dec_algorithm'),
      messages: [
        if (raw is List)
          for (final m in raw)
            if (m is Map<String, dynamic> && m['text'] is String)
              GuidingAssistantMessage(
                m['id'] is String ? m['id'] as String : '',
                m['text'] as String,
              ),
      ],
    );
  }

  /// The acceptable suggestions in the order the dialog lists them. Each key
  /// is a name `guiding_assistant_apply` understands. Pure — tested.
  List<GuidingAssistantSuggestion> get suggestions => [
    if (exposureSec != null)
      GuidingAssistantSuggestion(
        'exposure',
        'Guide exposure',
        exposureMaxSec != null && exposureMaxSec != exposureSec
            ? '${_s(exposureSec!)}–${_s(exposureMaxSec!)} s'
            : '${_s(exposureSec!)} s',
      ),
    if (minMoveRaPx != null)
      GuidingAssistantSuggestion(
        'min_move_ra',
        'RA minimum move',
        '${minMoveRaPx!.toStringAsFixed(2)} px',
      ),
    if (minMoveDecPx != null)
      GuidingAssistantSuggestion(
        'min_move_dec',
        'Dec minimum move',
        '${minMoveDecPx!.toStringAsFixed(2)} px',
      ),
    if (decBacklashMs != null)
      GuidingAssistantSuggestion(
        'dec_backlash',
        'Dec backlash compensation',
        '${decBacklashMs!.round()} ms',
      ),
    if (decGuideMode != null)
      GuidingAssistantSuggestion(
        'dec_guide_mode',
        'Dec guide mode',
        decGuideMode!,
      ),
    if (decAlgorithm != null)
      GuidingAssistantSuggestion(
        'dec_algorithm',
        'Dec algorithm',
        decAlgorithm!,
      ),
  ];

  static String _s(double v) =>
      v == v.roundToDouble() ? v.round().toString() : v.toStringAsFixed(1);
}

class GuidingAssistantMessage {
  final String id;
  final String text;
  const GuidingAssistantMessage(this.id, this.text);
}
