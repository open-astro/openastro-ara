/// Converts decimal RA/Dec (degrees) into the NINA `InputCoordinates` JSON DOM
/// the sequencer expects — the inverse of the display formatters in
/// `coord_format.dart`, which only go decimal → string.
///
/// Catalog objects and the sky atlas carry coordinates as decimal degrees
/// (`ra_deg` / `dec_deg`), but a `SlewScopeToRaDec` instruction stores them
/// decomposed: RA as integer hours/minutes + fractional seconds, Dec as a sign
/// flag plus integer degrees/minutes + fractional seconds. This builds that map.
library;

/// The assembly-qualified `$type` of a NINA `InputCoordinates` value — the
/// single source of truth for the string (mirrors the catalog's
/// `defaultCoordinates`). Setting a `SlewScopeToRaDec` node's `Coordinates`
/// field to the output of [inputCoordinatesFromDeg] keeps this `$type`.
const String inputCoordinatesType =
    'OpenAstroAra.Astrometry.InputCoordinates, OpenAstroAra.Astrometry';

/// Build the `InputCoordinates` JSON DOM for [raDeg]/[decDeg] (decimal degrees).
///
/// RA is normalized into `[0, 360)` first (so a wrapped 360°, or a small
/// negative from rounding, lands cleanly), then expressed as hours = deg / 15.
/// Dec is clamped to `[-90, 90]`; its sign is carried by `NegativeDec` and the
/// degree/minute/second fields hold the magnitude (matching NINA, which keeps
/// the components unsigned).
///
/// Seconds stay fractional (`double`) so no precision is lost — H + M/60 + S/3600
/// reproduces the input to within floating-point error.
Map<String, dynamic> inputCoordinatesFromDeg(double raDeg, double decDeg) {
  final raHours = _normalizeRaDeg(raDeg) / 15.0;
  final (rh, rm, rs) = _decompose(raHours);

  final negativeDec = decDeg < 0;
  final (dd, dm, ds) = _decompose(decDeg.abs().clamp(0.0, 90.0).toDouble());

  return <String, dynamic>{
    r'$type': inputCoordinatesType,
    'RAHours': rh,
    'RAMinutes': rm,
    'RASeconds': rs,
    'NegativeDec': negativeDec,
    'DecDegrees': dd,
    'DecMinutes': dm,
    'DecSeconds': ds,
  };
}

/// The decimal-degree (raDeg, decDeg) of a NINA `InputCoordinates` JSON DOM —
/// the inverse of [inputCoordinatesFromDeg]. Returns null when [coordinates]
/// isn't a map carrying the numeric component fields.
///
/// NOTE THE UNITS: NINA decomposes RA in HOURS (H + M/60 + S/3600), so this
/// multiplies by 15 to return DEGREES — the convention every planning API
/// (`raDeg`, catalog `ra_deg`) uses. Passing raw RAHours downstream would
/// silently produce plausible-but-wrong galactic latitudes and star counts.
({double raDeg, double decDeg})? degFromInputCoordinates(Object? coordinates) {
  if (coordinates is! Map) return null;
  final rh = coordinates['RAHours'];
  final rm = coordinates['RAMinutes'];
  final rs = coordinates['RASeconds'];
  final dd = coordinates['DecDegrees'];
  final dm = coordinates['DecMinutes'];
  final ds = coordinates['DecSeconds'];
  if (rh is! num || rm is! num || rs is! num || dd is! num || dm is! num || ds is! num) {
    return null;
  }
  final raHours = rh + rm / 60.0 + rs / 3600.0;
  final raDeg = _normalizeRaDeg(raHours * 15.0);
  final decMagnitude =
      (dd + dm / 60.0 + ds / 3600.0).clamp(0.0, 90.0).toDouble();
  final decDeg =
      coordinates['NegativeDec'] == true ? -decMagnitude : decMagnitude;
  return (raDeg: raDeg, decDeg: decDeg);
}

/// Fold [raDeg] into `[0, 360)`. For `double` operands Dart's `%` follows
/// truncating division — the remainder takes the sign of the dividend, so
/// `-15.0 % 360.0 == -15.0`, not `345.0` (unlike Dart's integer `%`, which is
/// always non-negative). The `< 0` guard is therefore required, not defensive:
/// it adds 360 back so a negative input (e.g. -0.001 from upstream rounding, or
/// a genuine negative RA) maps to a non-negative hour rather than a negative one.
double _normalizeRaDeg(double raDeg) {
  final wrapped = raDeg % 360.0;
  return wrapped < 0 ? wrapped + 360.0 : wrapped;
}

/// Split a non-negative sexagesimal magnitude into (whole units, whole minutes,
/// fractional seconds). Used for both RA (units = hours) and Dec (units =
/// degrees); the field semantics differ but the decomposition is identical.
(int, int, double) _decompose(double value) {
  final units = value.floor();
  final minutesFull = (value - units) * 60.0;
  final minutes = minutesFull.floor();
  final seconds = (minutesFull - minutes) * 60.0;
  return (units, minutes, seconds);
}
