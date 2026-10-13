/// §57.4 — `POST /api/v1/platesolve/verify-position`: where the mount actually
/// points after a Stop Mount. The solve beside what the mount reports and where
/// the last slew was aimed; separations in arcminutes.
class VerifyPositionResult {
  final bool success;
  final double? solvedRaHours;
  final double? solvedDecDegrees;
  final double? mountRaHours;
  final double? mountDecDegrees;
  final double? intendedRaHours;
  final double? intendedDecDegrees;
  final double? solvedToMountArcmin;
  final double? solvedToIntendedArcmin;
  final double exposureSeconds;
  final String? note;

  const VerifyPositionResult({
    required this.success,
    this.solvedRaHours,
    this.solvedDecDegrees,
    this.mountRaHours,
    this.mountDecDegrees,
    this.intendedRaHours,
    this.intendedDecDegrees,
    this.solvedToMountArcmin,
    this.solvedToIntendedArcmin,
    required this.exposureSeconds,
    this.note,
  });

  factory VerifyPositionResult.fromJson(Map<String, dynamic> json) {
    double? d(String k) => (json[k] as num?)?.toDouble();
    return VerifyPositionResult(
      success: json['success'] == true,
      solvedRaHours: d('solved_ra_hours'),
      solvedDecDegrees: d('solved_dec_degrees'),
      mountRaHours: d('mount_ra_hours'),
      mountDecDegrees: d('mount_dec_degrees'),
      intendedRaHours: d('intended_ra_hours'),
      intendedDecDegrees: d('intended_dec_degrees'),
      solvedToMountArcmin: d('solved_to_mount_arcmin'),
      solvedToIntendedArcmin: d('solved_to_intended_arcmin'),
      exposureSeconds: d('exposure_seconds') ?? 0,
      note: json['note'] as String?,
    );
  }

  /// The lines the post-stop modal shows, in order. Pure — unit-tested.
  List<String> get lines {
    String pos(double? ra, double? dec) => ra == null || dec == null
        ? 'unknown'
        : 'RA ${ra.toStringAsFixed(2)}h, Dec ${dec >= 0 ? '+' : ''}${dec.toStringAsFixed(2)}°';
    String sep(double? arcmin) => arcmin == null
        ? ''
        : arcmin < 1
        ? ' (${(arcmin * 60).round()}″ off)'
        : arcmin < 120
        ? ' (${arcmin.toStringAsFixed(1)}′ off)'
        : ' (${(arcmin / 60).toStringAsFixed(1)}° off)';
    // Offsets only make sense against a solve.
    final mountSep = success ? sep(solvedToMountArcmin) : '';
    final intendedSep = success ? sep(solvedToIntendedArcmin) : '';
    final mount = pos(mountRaHours, mountDecDegrees);
    final intended = pos(intendedRaHours, intendedDecDegrees);
    return [
      if (success)
        'Image solved: ${pos(solvedRaHours, solvedDecDegrees)}'
      else
        'Image did not solve.',
      'Mount reports: $mount$mountSep',
      if (intendedRaHours != null) 'Slew was aimed at: $intended$intendedSep',
      ?note,
    ];
  }
}
