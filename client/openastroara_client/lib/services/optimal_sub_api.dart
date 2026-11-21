/// NEXTGEN §2/§5 — the daemon's Optimal-Sub calculation for one filter: the
/// Glover read-noise floor intersected with the sky-background saturation
/// ceiling (`GET /api/v1/planning/optimal-sub`, slice 2).
class OptimalSubResult {
  /// The modelled sky-background flux P (e⁻/s/px).
  final double skyFluxEPerSecPerPx;

  /// The Glover read-noise floor (seconds) — the recommendation when viable.
  final double floorSec;

  /// The sky-background saturation ceiling (seconds).
  final double ceilingSec;

  /// floor ≤ ceiling. False = the ceiling collapsed below the floor
  /// (saturation-limited before read noise is swamped).
  final bool viable;

  /// Which bound decides: `readnoisefloor` or `saturationceiling` (wire tokens).
  final String limitingBound;

  /// min(floor, ceiling) — per Glover, past the floor buys no further
  /// read-noise gain, so the floor IS the recommendation when reachable.
  final double recommendedSec;

  /// Snake_case names of every input the daemon had to default (Tier-0
  /// generic-CMOS fallbacks) — the transparency contract. Null/empty = the
  /// figure is built entirely from configured values.
  final List<String>? assumedDefaults;

  /// §3.1 star-detectability fields — present only when the request carried a
  /// target position (and the daemon is new enough); null otherwise.
  /// [starFloorSec] is t_stars, the shortest sub predicted to reach the
  /// registration star budget (null even with a target = a starved field);
  /// when it binds, [limitingBound] reads `starfloor` and [recommendedSec]
  /// already sits on it. [starReason] is the daemon's ready-made advisory
  /// line, labelled when counts are extrapolated beyond the star catalog's
  /// mag-9 completeness — render it verbatim.
  final double? starFloorSec;
  final double? starsDetectedPerSub;
  final double? starsRegistrationPerSub;
  final String? starReason;

  const OptimalSubResult({
    required this.skyFluxEPerSecPerPx,
    required this.floorSec,
    required this.ceilingSec,
    required this.viable,
    required this.limitingBound,
    required this.recommendedSec,
    this.assumedDefaults,
    this.starFloorSec,
    this.starsDetectedPerSub,
    this.starsRegistrationPerSub,
    this.starReason,
  });

  static OptimalSubResult? fromJson(Map<String, dynamic> json) {
    final floor = json['floor_sec'];
    final ceiling = json['ceiling_sec'];
    final recommended = json['recommended_sec'];
    if (floor is! num || ceiling is! num || recommended is! num) return null;
    return OptimalSubResult(
      skyFluxEPerSecPerPx:
          (json['sky_flux_e_per_sec_per_px'] as num?)?.toDouble() ?? 0,
      floorSec: floor.toDouble(),
      ceilingSec: ceiling.toDouble(),
      viable: json['viable'] is bool ? json['viable'] as bool : true,
      limitingBound: json['limiting_bound'] is String
          ? json['limiting_bound'] as String
          : 'readnoisefloor',
      recommendedSec: recommended.toDouble(),
      assumedDefaults: switch (json['assumed_defaults']) {
        final List<dynamic> l => l.whereType<String>().toList(),
        _ => null,
      },
      starFloorSec: _optDouble(json['star_floor_sec']),
      starsDetectedPerSub: _optDouble(json['stars_detected_per_sub']),
      starsRegistrationPerSub: _optDouble(json['stars_registration_per_sub']),
      starReason: json['star_reason'] is String ? json['star_reason'] as String : null,
    );
  }

  static double? _optDouble(Object? v) => v is num ? v.toDouble() : null;
}
