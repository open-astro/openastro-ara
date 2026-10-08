/// #1311 — the polar alignment error left after Align, measured by the daemon
/// from the Dec drift the guider corrects in the first minutes of each guided
/// run (`guider.pa_residual`, `runtime.pa_residual` on `GET
/// /equipment/guider`, `GET /equipment/guider/pa-residuals`).
library;

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

  /// False when the 1σ uncertainty is more than max(1′, half the value).
  final bool reliable;
  final double? hourAngleHours;
  final double? decDeg;

  /// Tonight's last Polar Align result, for comparison.
  final double? alignErrorArcmin;
  final DateTime? alignEndedUtc;
  final DateTime? completedUtc;

  /// Why an `unavailable` run could not be measured.
  final String? reason;

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
    this.alignEndedUtc,
    this.completedUtc,
    this.reason,
  });

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
      alignEndedUtc: _t(json['align_ended_utc']),
      completedUtc: _t(json['completed_utc']),
      reason: json['reason'] is String ? json['reason'] as String : null,
    );
  }

  static double? _d(Object? v) {
    final d = v is num ? v.toDouble() : null;
    return d != null && d.isFinite ? d : null;
  }

  static DateTime? _t(Object? v) => v is String ? DateTime.tryParse(v) : null;

  /// What an `unavailable` reason means, in the user's words.
  static String reasonText(String? reason) => switch (reason) {
    'lock_shift' => 'Lock-position shift (comet tracking) is on, so the guide star drifts in Dec on purpose.',
    'no_calibration' => "The guider's calibration data could not be read.",
    'no_pixel_scale' => 'The guider has not reported its pixel scale.',
    _ => 'Not enough clean guiding to fit the drift.',
  };

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
      other.reason == reason;

  @override
  int get hashCode => Object.hash(
    id,
    status,
    sampleSeconds,
    frames,
    paErrorMinArcmin,
    uncertaintyArcmin,
    alignErrorArcmin,
    reason,
  );
}
