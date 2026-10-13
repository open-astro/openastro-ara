import 'dart:math' as math;

/// #1264 stage 1 — the mount's spec sheet as AlpacaBridge #815 publishes it
/// and the daemon relays it in the telescope capabilities (`mount_specs`):
/// the two tracking figures Ara needs (worm period, periodic error) plus the
/// informational rest. Null fields mean "unknown", never a guess.
class MountSpecSheet {
  final String? driverId;
  final String? model;

  /// `manufacturer` | `measured` | `community` — shown next to the number.
  final String source;
  final double? wormPeriodS;
  final double? periodicErrorPpArcsec;
  final bool hasEncoders;
  final bool hasPec;
  final String? mountType;
  final double? maxPayloadKg;
  final String? notes;

  const MountSpecSheet({
    this.driverId,
    this.model,
    this.source = 'manufacturer',
    this.wormPeriodS,
    this.periodicErrorPpArcsec,
    this.hasEncoders = false,
    this.hasPec = false,
    this.mountType,
    this.maxPayloadKg,
    this.notes,
  });

  /// Parse the capabilities' `mount_specs` block; null when absent or not an
  /// object.
  static MountSpecSheet? fromJson(Object? json) {
    if (json is! Map<String, dynamic>) return null;
    double? num_(String k) {
      final v = json[k];
      return v is num && v.isFinite ? v.toDouble() : null;
    }

    String? str(String k) => json[k] is String ? json[k] as String : null;
    return MountSpecSheet(
      driverId: str('driver_id'),
      model: str('model'),
      source: str('source') ?? 'manufacturer',
      wormPeriodS: num_('worm_period_s'),
      periodicErrorPpArcsec: num_('periodic_error_pp_arcsec'),
      hasEncoders: json['has_encoders'] == true,
      hasPec: json['has_pec'] == true,
      mountType: str('mount_type'),
      maxPayloadKg: num_('max_payload_kg'),
      notes: str('notes'),
    );
  }

  /// True when the sheet carries both tracking figures.
  bool get hasTrackingFigures =>
      wormPeriodS != null &&
      wormPeriodS! > 0 &&
      periodicErrorPpArcsec != null &&
      periodicErrorPpArcsec! > 0;

  /// The readiness fact: "Periodic error 25″ p-p over 638 s · manufacturer",
  /// or what the sheet does say when a figure is missing.
  String get trackingFact {
    final pe = periodicErrorPpArcsec;
    final worm = wormPeriodS;
    final head = hasEncoders
        ? 'Encoders (no periodic error)'
        : pe == null
        ? 'Periodic error unknown'
        : 'Periodic error ${_fmt(pe)}″ p-p${worm == null ? '' : ' over ${_fmt(worm)} s'}';
    return '$head · $source';
  }

  static String _fmt(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is MountSpecSheet &&
          other.driverId == driverId &&
          other.model == model &&
          other.source == source &&
          other.wormPeriodS == wormPeriodS &&
          other.periodicErrorPpArcsec == periodicErrorPpArcsec &&
          other.hasEncoders == hasEncoders &&
          other.hasPec == hasPec &&
          other.mountType == mountType &&
          other.maxPayloadKg == maxPayloadKg &&
          other.notes == notes);

  @override
  int get hashCode => Object.hash(
    driverId,
    model,
    source,
    wormPeriodS,
    periodicErrorPpArcsec,
    hasEncoders,
    hasPec,
    mountType,
    maxPayloadKg,
    notes,
  );
}

/// The shortest prior ever suggested (the daemon's floor; the ladder starts at
/// 10 s).
const double minUnguidedPriorSeconds = 5;

/// The trailing tolerance in arcseconds: a pixel, or half the seeing disc,
/// whichever is larger. Null without either figure.
double? trackingTolerance({
  double? pixelScaleArcsec,
  double? seeingFwhmArcsec,
}) {
  final px = pixelScaleArcsec != null && pixelScaleArcsec > 0
      ? pixelScaleArcsec
      : null;
  final see = seeingFwhmArcsec != null && seeingFwhmArcsec > 0
      ? 0.5 * seeingFwhmArcsec
      : null;
  if (px == null && see == null) return null;
  return math.max(px ?? 0, see ?? 0);
}

/// The unguided exposure prior the sheet implies for THIS rig, in seconds —
/// the same formula the daemon's `MountSpecSheet.UnguidedPriorSeconds` uses,
/// so the advisor's number matches the probe's starting point:
///
///     tolerance = max(1 px × pixel scale, 0.5 × seeing FWHM)
///     pe_rate   = 2π · (periodic_error_pp / 2) / worm_period   (0 with encoders)
///     pa_rate   = 15.04 · sin(polar-align error)               (worst case, cos dec = 1)
///     t         = tolerance / (pe_rate + pa_rate), floored at 5 s
///
/// Null when no drift term is known or no tolerance can be set. Pure.
double? unguidedPriorFromSpecs(
  MountSpecSheet? sheet, {
  double? pixelScaleArcsec,
  double? seeingFwhmArcsec,
  double? polarAlignErrorArcmin,
}) {
  final tol = trackingTolerance(
    pixelScaleArcsec: pixelScaleArcsec,
    seeingFwhmArcsec: seeingFwhmArcsec,
  );
  if (tol == null || tol <= 0) return null;
  var rate = 0.0;
  var anyTerm = false;
  if (sheet != null && !sheet.hasEncoders && sheet.hasTrackingFigures) {
    rate +=
        2 * math.pi * (sheet.periodicErrorPpArcsec! / 2) / sheet.wormPeriodS!;
    anyTerm = true;
  }
  if (polarAlignErrorArcmin != null && polarAlignErrorArcmin > 0) {
    rate += 15.04 * math.sin(polarAlignErrorArcmin / 60 * math.pi / 180);
    anyTerm = true;
  }
  if (!anyTerm || rate <= 0) return null;
  return math.max(minUnguidedPriorSeconds, tol / rate);
}
