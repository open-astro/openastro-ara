/// §59.10 / #1139 — the collimation health verdict the daemon derives from a
/// completed autofocus sweep's donut stars (`autofocus.collimation_verdict`).
/// Pure data plus the presentation maths the pane needs.
enum CollimationSeverity {
  /// Centroid offset under 5 % of the donut diameter.
  good,

  /// 5–15 %.
  slight,

  /// Over 15 % — collimate before continuing.
  significant;

  static CollimationSeverity? fromWire(Object? v) => switch (v) {
    'good' => good,
    'slight' => slight,
    'significant' => significant,
    _ => null,
  };
}

class CollimationVerdict {
  final CollimationSeverity severity;

  /// Magnitude of the obstruction-shadow decentering, % of the donut diameter.
  final double offsetPercent;

  /// Angle of the offset in the RAW IMAGE frame: 0° = +x, increasing toward
  /// +y, and y runs DOWN as pixels are stored — so 90° points to the bottom
  /// of the image as displayed. Mapping this to the eyepiece needs the train's
  /// mirror flips, which the client does not know; the pane says "in the
  /// image" and never pretends otherwise.
  final double directionDegrees;
  final int starsUsed;
  final DateTime atUtc;

  const CollimationVerdict({
    required this.severity,
    required this.offsetPercent,
    required this.directionDegrees,
    required this.starsUsed,
    required this.atUtc,
  });

  /// Parse the WS payload `{severity, offset_percent, direction_degrees,
  /// stars_used}`; null when the severity is missing or "insufficient" (no
  /// verdict, not "fine") or the offset is not a number.
  static CollimationVerdict? fromPayload(
    Map<String, dynamic> p, {
    required DateTime at,
  }) {
    final severity = CollimationSeverity.fromWire(p['severity']);
    final offset = p['offset_percent'];
    if (severity == null || offset is! num || !offset.isFinite) return null;
    final dir = p['direction_degrees'];
    final stars = p['stars_used'];
    return CollimationVerdict(
      severity: severity,
      offsetPercent: offset.toDouble(),
      directionDegrees: dir is num && dir.isFinite ? dir.toDouble() : 0,
      starsUsed: stars is num ? stars.toInt() : 0,
      atUtc: at,
    );
  }

  /// Clock position of the offset IN THE IMAGE AS DISPLAYED: 0° (+x) is
  /// 3 o'clock, 90° (+y, down) is 6 o'clock, 270° is 12 o'clock.
  int get clockHour {
    final deg = ((directionDegrees % 360) + 360) % 360;
    final hour = ((deg / 30).round() + 3) % 12;
    return hour == 0 ? 12 : hour;
  }

  /// One line for the headline.
  String get headline => switch (severity) {
    CollimationSeverity.good => 'Collimation looks good',
    CollimationSeverity.slight => 'Slight miscollimation detected',
    CollimationSeverity.significant =>
      'Significant miscollimation — collimate before continuing',
  };

  /// The measurement in words.
  String get detail =>
      'Obstruction-shadow centroid offset ${offsetPercent.toStringAsFixed(1)} % '
      'of the donut diameter toward $clockHour o\'clock in the image '
      '(before any mirror flip), from $starsUsed donut star${starsUsed == 1 ? '' : 's'}.';
}

/// What a decentered donut usually means for this optical design (§59.10):
/// a hint, never a screw-turning instruction.
String collimationHintFor(String telescopeTypeWire, CollimationSeverity s) {
  if (s == CollimationSeverity.good) return 'Nothing to do.';
  return switch (telescopeTypeWire) {
    'sct' || 'mak' || 'rc' =>
      'On a Schmidt-Cassegrain, Maksutov or Ritchey-Chrétien this usually means the '
          'secondary mirror is tilted. Consult your scope\'s collimation procedure.',
    'newtonian' =>
      'On a Newtonian this usually means primary and/or secondary tilt. Consult your '
          'scope\'s collimation procedure.',
    'refractor' =>
      'A refractor rarely shows a decentered donut; check for a tilted camera or a '
          'loose focuser before anything else.',
    _ => 'Consult your scope\'s collimation procedure.',
  };
}
