/// #1311 — the polar alignment error left after Align, measured by the daemon
/// from the Dec drift the guider corrects in the first minutes of each guided
/// run (`guider.pa_residual`, `runtime.pa_residual` on `GET
/// /equipment/guider`, `GET /equipment/guider/pa-residuals`).
library;

import 'dart:math' as math;

enum PaResidualStatus { measuring, done, unavailable }

class PaResidual {
  final String id;
  final PaResidualStatus status;
  final double sampleSeconds;
  final double targetSeconds;
  final int frames;
  final double? driftArcsecPerMin;

  /// A LOWER BOUND, in arcminutes: Dec drift at one hour angle sees one
  /// component of the polar axis error.
  final double? paErrorMinArcmin;
  final double? uncertaintyArcmin;

  /// False when the 1σ uncertainty is more than max(1′, half the value), or
  /// when the value less 2σ is more than 2′ above [alignErrorArcmin] (#1731).
  final bool reliable;
  final double? hourAngleHours;
  final double? decDeg;

  /// Tonight's last Polar Align result, for comparison.
  final double? alignErrorArcmin;

  /// #2072 that result's own 1σ (`align_sigma_arcmin`, kept with a stored
  /// row too); null from an older daemon, a row stored before it, or an
  /// alignment measured before it.
  final double? alignSigmaArcmin;
  final DateTime? alignEndedUtc;
  final DateTime? completedUtc;

  /// Why an `unavailable` run could not be measured.
  final String? reason;

  /// #2072 the daemon's own call that this figure disagrees with
  /// [alignErrorArcmin] (`above_align`); null from an older daemon, a stored
  /// row, or with no Polar Align result to compare.
  final bool? aboveAlignFlag;

  /// #2072 the star did not answer the Dec pulses (`dec_pulses_unanswered`):
  /// Dec backlash, so the figure is the pulses, not drift. Nullable with no
  /// default because an older daemon does not send it, and a missing key must
  /// not make the whole residual unreadable: it only picks the wording (null
  /// reads as plain "noisy"). A display hint, not a safety key, so it is not
  /// in `ProfileApi.safetyModeledKeys`.
  final bool? decPulsesUnanswered;

  const PaResidual({
    required this.id,
    required this.status,
    this.sampleSeconds = 0,
    this.targetSeconds = 0,
    this.frames = 0,
    this.driftArcsecPerMin,
    this.paErrorMinArcmin,
    this.uncertaintyArcmin,
    this.reliable = true,
    this.hourAngleHours,
    this.decDeg,
    this.alignErrorArcmin,
    this.alignSigmaArcmin,
    this.alignEndedUtc,
    this.completedUtc,
    this.reason,
    this.aboveAlignFlag,
    this.decPulsesUnanswered,
  });

  /// #2072 the figure is Dec backlash: worded as such, never as a rating or a
  /// disagreement with Polar Align.
  bool get backlash => decPulsesUnanswered == true;

  /// #1731 / #2072 — the lower bound sits past the total error Polar Align
  /// measured: the two numbers disagree. The daemon's `above_align` decides;
  /// without it (an older daemon, a stored row) the same rule is applied here:
  /// with Align's own 1σ, the value more than twice their combined 1σ above
  /// Align; without it, the value less 2σ more than
  /// [alignDisagreementMarginArcmin] above Align.
  bool get aboveAlign {
    if (backlash) return false; // the daemon sends no verdict: the figure says nothing about the alignment
    final flag = aboveAlignFlag;
    if (flag != null) return flag;
    final value = paErrorMinArcmin,
        sigma = uncertaintyArcmin,
        align = alignErrorArcmin,
        alignSigma = alignSigmaArcmin;
    if (value == null || sigma == null || align == null) return false;
    if (alignSigma != null) {
      return value - align > 2 * math.sqrt(sigma * sigma + alignSigma * alignSigma);
    }
    return !reliable && value - 2 * sigma > align + alignDisagreementMarginArcmin;
  }

  /// The daemon's margin for Polar Align's own error
  /// (`GuiderService.AlignDisagreementMarginArcmin`).
  static const alignDisagreementMarginArcmin = 2.0;

  /// Null for `{status: "idle"}` (nothing measured) or an unreadable payload.
  static PaResidual? fromJson(Object? json) {
    if (json is! Map) return null;
    final status = switch (json['status']) {
      'measuring' => PaResidualStatus.measuring,
      'done' => PaResidualStatus.done,
      'unavailable' => PaResidualStatus.unavailable,
      _ => null,
    };
    final id = json['id'];
    if (status == null || id is! String) return null;
    if (status == PaResidualStatus.done &&
        _d(json['pa_error_min_arcmin']) == null) {
      return null;
    }
    return PaResidual(
      id: id,
      status: status,
      sampleSeconds: _d(json['sample_seconds']) ?? 0,
      targetSeconds: _d(json['target_seconds']) ?? 0,
      frames: (json['frames'] as num?)?.toInt() ?? 0,
      driftArcsecPerMin: _d(json['drift_arcsec_per_min']),
      paErrorMinArcmin: _d(json['pa_error_min_arcmin']),
      uncertaintyArcmin: _d(json['uncertainty_arcmin']),
      reliable: json['reliable'] != false,
      hourAngleHours: _d(json['hour_angle_hours']),
      decDeg: _d(json['dec_deg']),
      alignErrorArcmin: _d(json['align_error_arcmin']),
      alignSigmaArcmin: _d(json['align_sigma_arcmin']),
      alignEndedUtc: _t(json['align_ended_utc']),
      completedUtc: _t(json['completed_utc']),
      reason: json['reason'] is String ? json['reason'] as String : null,
      aboveAlignFlag: json['above_align'] is bool ? json['above_align'] as bool : null,
      decPulsesUnanswered: json['dec_pulses_unanswered'] is bool ? json['dec_pulses_unanswered'] as bool : null,
    );
  }

  static double? _d(Object? v) {
    final d = v is num ? v.toDouble() : null;
    return d != null && d.isFinite ? d : null;
  }

  static DateTime? _t(Object? v) => v is String ? DateTime.tryParse(v) : null;

  @override
  bool operator ==(Object other) =>
      other is PaResidual &&
      other.id == id &&
      other.status == status &&
      other.sampleSeconds == sampleSeconds &&
      other.frames == frames &&
      other.paErrorMinArcmin == paErrorMinArcmin &&
      other.uncertaintyArcmin == uncertaintyArcmin &&
      other.alignErrorArcmin == alignErrorArcmin &&
      other.alignSigmaArcmin == alignSigmaArcmin &&
      other.reason == reason &&
      other.aboveAlignFlag == aboveAlignFlag &&
      other.decPulsesUnanswered == decPulsesUnanswered;

  @override
  int get hashCode => Object.hash(
    id,
    status,
    sampleSeconds,
    frames,
    paErrorMinArcmin,
    uncertaintyArcmin,
    alignErrorArcmin,
    alignSigmaArcmin,
    reason,
    aboveAlignFlag,
    decPulsesUnanswered,
  );
}
