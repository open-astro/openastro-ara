import '../models/stats/stats_time.dart';

/// NEXTGEN §1 — the daemon's recommended filter approach for a target. Wire
/// tokens are the §60.6 all-lowercase enum values; an unknown/absent token
/// parses to null (advice simply doesn't render), so a newer daemon can add
/// approaches without breaking older clients.
enum TonightFilterAdvice {
  narrowband('Narrowband'),
  duoband('OSC + dual-band'),
  broadband('Broadband');

  const TonightFilterAdvice(this.label);

  /// Chip label.
  final String label;

  static TonightFilterAdvice? fromWire(Object? raw) => switch (raw) {
        'narrowband' => TonightFilterAdvice.narrowband,
        'duoband' => TonightFilterAdvice.duoband,
        'broadband' => TonightFilterAdvice.broadband,
        _ => null,
      };
}

/// §36.8 framing fit of an object against the active optical train: does it
/// sit comfortably in the sensor's field of view? Wire values are all-lowercase
/// (§60.6); anything unrecognised maps to [unknown] so a new server enum can't
/// crash an older client.
enum TonightFraming {
  unknown,
  tooSmall,
  good,
  tooBig;

  static TonightFraming fromWire(Object? raw) => switch (raw) {
        'toosmall' => TonightFraming.tooSmall,
        'good' => TonightFraming.good,
        'toobig' => TonightFraming.tooBig,
        _ => TonightFraming.unknown,
      };
}

/// §36/§25.5 — one Tonight's Sky entry from `GET /api/v1/planning/tonight`:
/// a deep-sky object ranked by its equipment-aware [score] (0–100, server-ranked
/// descending). [altitudeDeg] is its current altitude above the site horizon;
/// [maxAltitudeDeg] is its highest possible (transit) altitude from this
/// latitude; [raDeg]/[decDeg] (J2000) let the atlas recentre on it. The §36.8
/// planner fields ([framing], the window/transit times, the hours, the size and
/// surface-brightness measurements, [scoreReasons]) are optional on the wire —
/// older servers omit them, so they are nullable / defaulted, never required.
class TonightSkyObject {
  final String id;
  final String name;
  final String type;

  /// Visual magnitude, or null when the server omits/mangles it — kept nullable
  /// so a bad value can't masquerade as a genuine `mag 0.0` (≈ Vega).
  final double? magnitude;
  final double raDeg;
  final double decDeg;
  final double altitudeDeg;
  final double maxAltitudeDeg;

  // §36.8 catalog measurements (nullable — not every object records a size).
  final double? sizeMajArcmin;
  final double? sizeMinArcmin;
  final double? posAngleDeg;
  final double? surfaceBrightness;

  // §36.8 tonight's longest dark window, its transit, and the hours. The times
  // are UTC instants (parse to local for display); the hours default to 0 when
  // a pre-§36.8 server omits them so the UI can simply hide a zero.
  final DateTime? windowStartUtc;
  final DateTime? windowEndUtc;
  final DateTime? transitUtc;
  final double integrationHours;
  final double remainingHours;

  /// Equipment-aware fit against the active optical train ([TonightFraming]).
  final TonightFraming framing;

  /// Transparent 0–100 "worth shooting tonight" blend (the server's ranking key),
  /// or null when the server omits it (a pre-§36.8 daemon) — kept nullable, like
  /// [magnitude], so a missing score can't masquerade as a genuine, misleading `0`.
  final double? score;

  /// Short component tags ("fills the frame (+35)", "5 h dark window (+21)")
  /// that explain the score; null when the server omits them.
  final List<String>? scoreReasons;

  /// NEXTGEN §1 — the recommended filter approach for this target (the user's
  /// declared filter set × the target's emission character × Bortle); null when
  /// no filter set is declared, the emission character is unknown, or the daemon
  /// predates the field. Advice only — never a gate.
  final TonightFilterAdvice? filterAdvice;

  /// One-line human explanation of [filterAdvice]; null alongside it.
  final String? adviceReason;

  /// The Glover read-noise-floor sub length (seconds) for the advised approach,
  /// or null until the camera electronics + aperture are configured server-side.
  final double? optimalSubS;

  /// §36.8 slice-4 moon advisory (all three null on a pre-slice-4 daemon).
  /// Great-circle distance (degrees) from the target to the moon at the
  /// object's window midpoint. Display context only — never a gate or a score
  /// input.
  final double? moonSeparationDeg;

  /// The moon's illuminated disc fraction tonight, 0–100.
  final double? moonIlluminationPct;

  /// How much of the object's dark window has the moon above the true horizon:
  /// 0 = a genuinely moonless window, 1 = moon up throughout.
  final double? moonUpFraction;

  const TonightSkyObject({
    required this.id,
    required this.name,
    required this.type,
    required this.magnitude,
    required this.raDeg,
    required this.decDeg,
    required this.altitudeDeg,
    required this.maxAltitudeDeg,
    this.sizeMajArcmin,
    this.sizeMinArcmin,
    this.posAngleDeg,
    this.surfaceBrightness,
    this.windowStartUtc,
    this.windowEndUtc,
    this.transitUtc,
    this.integrationHours = 0,
    this.remainingHours = 0,
    this.framing = TonightFraming.unknown,
    this.score,
    this.scoreReasons,
    this.filterAdvice,
    this.adviceReason,
    this.optimalSubS,
    this.moonSeparationDeg,
    this.moonIlluminationPct,
    this.moonUpFraction,
  });

  /// Parse one wire object, or null when a required field is missing/wrong-typed
  /// (so a malformed row is skipped, not crashed on). The string identity (id /
  /// name / type), the position (ra/dec — a bad value would be a real-but-wrong
  /// (0,0) sky spot), and the altitudes (a bad value would render as a misleading
  /// "0°" on the horizon, and altitude is the ranking key) are all required;
  /// only `magnitude` is optional — left null rather than defaulting to the real
  /// value 0 (≈ Vega). The §36.8 planner fields are parsed defensively: a
  /// missing/wrong-typed value falls back to null (measurements/times) or a
  /// sensible default (hours 0, framing unknown, score null), never throwing.
  static TonightSkyObject? fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final name = json['name'];
    final type = json['type'];
    if (id is! String || name is! String || type is! String) return null;
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (ra is! num || dec is! num) return null;
    final alt = json['altitude_deg'];
    final maxAlt = json['max_altitude_deg'];
    if (alt is! num || maxAlt is! num) return null;
    final mag = json['magnitude'];
    return TonightSkyObject(
      id: id,
      name: name,
      type: type,
      magnitude: mag is num ? mag.toDouble() : null,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      altitudeDeg: alt.toDouble(),
      maxAltitudeDeg: maxAlt.toDouble(),
      sizeMajArcmin: _optDouble(json['size_maj_arcmin']),
      sizeMinArcmin: _optDouble(json['size_min_arcmin']),
      posAngleDeg: _optDouble(json['pos_angle_deg']),
      surfaceBrightness: _optDouble(json['surface_brightness']),
      windowStartUtc: _optUtc(json['window_start_utc']),
      windowEndUtc: _optUtc(json['window_end_utc']),
      transitUtc: _optUtc(json['transit_utc']),
      integrationHours: _optDouble(json['integration_hours']) ?? 0,
      remainingHours: _optDouble(json['remaining_hours']) ?? 0,
      framing: TonightFraming.fromWire(json['framing']),
      // Null when omitted (so it can't pose as a real 0); clamped to the 0–100
      // invariant at the data layer when present, for every consumer not just the badge.
      score: _optDouble(json['score'])?.clamp(0.0, 100.0).toDouble(),
      scoreReasons: _optStringList(json['score_reasons']),
      filterAdvice: TonightFilterAdvice.fromWire(json['filter_advice']),
      adviceReason: json['advice_reason'] is String ? json['advice_reason'] as String : null,
      optimalSubS: _optDouble(json['optimal_sub_s']),
      // Moon advisory (slice 4) — clamped to their invariants at the data layer,
      // like `score`, so a mangled value can't render as "430° away".
      moonSeparationDeg:
          _optDouble(json['moon_separation_deg'])?.clamp(0.0, 180.0).toDouble(),
      moonIlluminationPct:
          _optDouble(json['moon_illumination_pct'])?.clamp(0.0, 100.0).toDouble(),
      moonUpFraction: _optDouble(json['moon_up_fraction'])?.clamp(0.0, 1.0).toDouble(),
    );
  }

  static double? _optDouble(Object? v) => v is num ? v.toDouble() : null;

  /// Parse an ISO-8601 instant, normalised to UTC; null on absent/garbage so a
  /// bad timestamp simply drops out of the UI. Delegates to [parseStatsUtc], which
  /// treats a suffix-less ("naive") timestamp as wall-clock UTC rather than calling
  /// `.toUtc()` on it (which would shift it by the client's local offset).
  static DateTime? _optUtc(Object? v) => parseStatsUtc(v);

  /// Keep only the string entries — a non-list or a list with stray non-strings
  /// shouldn't blow up the "why this score" expansion.
  static List<String>? _optStringList(Object? v) {
    if (v is! List) return null;
    final out = <String>[for (final e in v) if (e is String) e];
    return out.isEmpty ? null : out;
  }
}

/// §36.8 slice 4b — a per-request "what-if" optical train + mosaic layout for
/// Tonight's Sky. Every optics field is nullable: null means "use the active
/// profile's value" (the server merges per-field, so overriding just the
/// reducer is valid). Mosaic tile counts default to 1×1 (no mosaic). The
/// bounds mirror the server's request validation ([maxReducer] /
/// [maxMosaicTiles]) so a value the dialog accepts can't come back as a 400.
class TonightOverrides {
  /// Server-side reducer ceiling (TonightSkyOverrides.MaxReducerFactor).
  static const double maxReducer = 10;

  /// Server-side per-axis mosaic tile cap.
  static const int maxMosaicTiles = 20;

  final double? focalLengthMm;
  final double? reducer;
  final int? sensorW;
  final int? sensorH;
  final double? pixelUm;
  final int mosaicX;
  final int mosaicY;

  const TonightOverrides({
    this.focalLengthMm,
    this.reducer,
    this.sensorW,
    this.sensorH,
    this.pixelUm,
    this.mosaicX = 1,
    this.mosaicY = 1,
  });

  /// The no-override sentinel — profile optics at 1×1.
  static const TonightOverrides none = TonightOverrides();

  /// Whether this override changes the request at all. An inactive override
  /// sends no extra query parameters, keeping the common path identical to
  /// the pre-slice-4b request (no profile merge server-side).
  bool get isActive =>
      focalLengthMm != null ||
      reducer != null ||
      sensorW != null ||
      sensorH != null ||
      pixelUm != null ||
      mosaicX != 1 ||
      mosaicY != 1;

  /// The query parameters this override adds to `GET /planning/tonight`.
  /// Only supplied fields are sent (the server merges the rest from the
  /// profile); a 1 tile count is omitted since it's the server default.
  Map<String, dynamic> toQueryParameters() => <String, dynamic>{
        if (focalLengthMm != null) 'focalLengthMm': focalLengthMm,
        if (reducer != null) 'reducer': reducer,
        if (sensorW != null) 'sensorW': sensorW,
        if (sensorH != null) 'sensorH': sensorH,
        if (pixelUm != null) 'pixelUm': pixelUm,
        if (mosaicX != 1) 'mosaicX': mosaicX,
        if (mosaicY != 1) 'mosaicY': mosaicY,
      };

  /// Value equality so the overrides provider only notifies (and Tonight's
  /// Sky only refetches) when a field actually changed.
  @override
  bool operator ==(Object other) =>
      other is TonightOverrides &&
      other.focalLengthMm == focalLengthMm &&
      other.reducer == reducer &&
      other.sensorW == sensorW &&
      other.sensorH == sensorH &&
      other.pixelUm == pixelUm &&
      other.mosaicX == mosaicX &&
      other.mosaicY == mosaicY;

  @override
  int get hashCode => Object.hash(
      focalLengthMm, reducer, sensorW, sensorH, pixelUm, mosaicX, mosaicY);
}
