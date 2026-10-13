/// #1264 stage 1 — the mount's spec sheet as AlpacaBridge #815 publishes it
/// and the daemon relays it in the telescope capabilities (`mount_specs`):
/// the two tracking figures Ara needs (worm period, periodic error) plus the
/// informational rest. Null fields mean "unknown", never a guess.
class MountSpecSheet {
  final String? driverId;
  final String? model;

  /// `manufacturer` | `measured` | `community` — shown next to the number;
  /// null when the sheet does not say (never a guess).
  final String? source;
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
    this.source,
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
      source: str('source'),
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
    return source == null ? head : '$head · $source';
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
