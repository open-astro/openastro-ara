/// §18.I — the astrometric solution returned by `POST /api/v1/platesolve/frames/{id}/solve`.
///
/// [success] false means the solver ran but found no solution (too few stars,
/// wrong field, etc.); every other field is then null. The wire shape is the
/// daemon's snake_case `PlateSolveResultDto`.
class PlateSolveResult {
  /// Whether the solver found a solution.
  final bool success;

  /// Right ascension at the frame centre, in hours. Null when [success] is false.
  final double? ra;

  /// Declination at the frame centre, in degrees. Null when [success] is false.
  final double? dec;

  /// Image rotation, degrees east-of-north. Null when [success] is false.
  final double? orientation;

  /// Plate scale, arcsec/pixel. Null when [success] is false.
  final double? pixelScale;

  /// The solved search radius, degrees. Null when [success] is false.
  final double? searchRadius;

  const PlateSolveResult({
    required this.success,
    this.ra,
    this.dec,
    this.orientation,
    this.pixelScale,
    this.searchRadius,
  });

  factory PlateSolveResult.fromJson(Map<String, dynamic> json) {
    double? asDouble(String key) => (json[key] as num?)?.toDouble();
    return PlateSolveResult(
      success: json['success'] as bool? ?? false,
      ra: asDouble('ra'),
      dec: asDouble('dec'),
      orientation: asDouble('orientation'),
      pixelScale: asDouble('pixel_scale'),
      searchRadius: asDouble('search_radius'),
    );
  }
}
