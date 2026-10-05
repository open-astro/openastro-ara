/// Parsing of typed / pasted sky coordinates for the Plan screen's custom
/// target entry (#1267 item 1).
///
/// People paste positions from SIMBAD (`05 35 17.3 -05 23 28`), Gaia /
/// VizieR (`83.82208 -5.39111`, decimal degrees), Stellarium
/// (`5h35m17.3s/-5°23'28"`), a hand controller (`05:35:17 -05:23:28`) or type
/// a bare decimal, and nobody wants to reformat first. Everything here is
/// pure and returns null for anything it cannot read, so the dialog can show
/// a live "can't read that" state instead of guessing.
///
/// The one genuine ambiguity is a single decimal RA: `5.5` could mean 5.5 h or
/// 5.5°. A value above 24 can only be degrees; below that [RaUnit] decides,
/// and the entry UI shows the chooser only when it matters. An explicit `h`
/// marker (`5.5h`) always wins.
library;

/// How to read a single decimal RA value that could be either unit.
enum RaUnit { degrees, hours }

/// One parsed RA/Dec pair in degrees.
class ParsedCoordinates {
  final double raDeg;
  final double decDeg;
  const ParsedCoordinates(this.raDeg, this.decDeg);

  @override
  bool operator ==(Object other) =>
      other is ParsedCoordinates &&
      other.raDeg == raDeg &&
      other.decDeg == decDeg;

  @override
  int get hashCode => Object.hash(raDeg, decDeg);

  @override
  String toString() => 'ParsedCoordinates($raDeg, $decDeg)';
}

final _numberRe = RegExp(r'[+\-−]?\d+(?:[.,]\d+)?');
final _hourMarkerRe = RegExp(r'\d\s*h', caseSensitive: false);

/// Normalise unit markers (`h m s d ° ′ ″ ' " :`) to spaces and unicode
/// minus to ASCII, keeping the signs attached to their numbers. A letter
/// marker only counts directly after a digit, so the `M` of `M42` survives as
/// a letter and the whole thing is rejected as a name.
String _tokensOf(String s) => s
    .replaceAll('−', '-')
    .replaceAllMapped(
        RegExp(r'''(\d)\s*[hHmMsSdD°º′’'″”":]'''), (m) => '${m[1]} ')
    .replaceAll(RegExp(r'''[°º′’'″”":]'''), ' ')
    .trim();

List<double>? _numbers(String s, {int max = 3}) {
  final cleaned = _tokensOf(s);
  if (cleaned.isEmpty) return null;
  final matches = _numberRe.allMatches(cleaned).toList();
  // Nothing but numbers and whitespace is allowed to remain.
  final leftover = cleaned.replaceAll(_numberRe, '').trim();
  if (leftover.isNotEmpty) return null;
  if (matches.isEmpty || matches.length > max) return null;
  final out = <double>[];
  for (var i = 0; i < matches.length; i++) {
    final text = matches[i].group(0)!.replaceAll(',', '.');
    // Only the first field may carry a sign.
    if (i > 0 && (text.startsWith('+') || text.startsWith('-'))) return null;
    final v = double.tryParse(text.replaceFirst('+', ''));
    if (v == null) return null;
    out.add(v);
  }
  return out;
}

/// Sexagesimal fields → a signed decimal; null when a minute/second is ≥ 60
/// or negative. The sign of the first field applies to the whole value
/// (`-00 30 00` is −0.5, which a bare `-0` would lose).
double? _sexagesimal(String raw, List<double> f) {
  final negative = _tokensOf(raw).startsWith('-');
  var v = f[0].abs();
  if (f.length > 1) {
    if (f[1] < 0 || f[1] >= 60) return null;
    v += f[1] / 60;
  }
  if (f.length > 2) {
    if (f[2] < 0 || f[2] >= 60) return null;
    v += f[2] / 3600;
  }
  return negative ? -v : v;
}

/// Parse a right ascension to degrees in [0, 360), or null.
///
/// Two or three fields are always hours/minutes/seconds. A single value is
/// hours when the text carries an `h` marker, degrees when it exceeds 24,
/// and otherwise whatever [decimalUnit] says.
double? parseRa(String text, {RaUnit decimalUnit = RaUnit.degrees}) {
  final f = _numbers(text);
  if (f == null) return null;
  final v = _sexagesimal(text, f);
  if (v == null || v < 0) return null;
  final bool hours;
  if (f.length > 1) {
    hours = true;
  } else if (_hourMarkerRe.hasMatch(text)) {
    hours = true;
  } else if (v > 24) {
    hours = false;
  } else {
    hours = decimalUnit == RaUnit.hours;
  }
  if (hours) {
    if (v >= 24) return null;
    return v * 15;
  }
  if (v >= 360) return null;
  return v;
}

/// True when [text] is a single decimal RA at or below 24 with no `h`
/// marker — the case where [RaUnit] changes the answer.
bool raUnitIsAmbiguous(String text) {
  final f = _numbers(text);
  if (f == null || f.length != 1) return false;
  if (_hourMarkerRe.hasMatch(text)) return false;
  return f[0].abs() <= 24;
}

/// Parse a declination to degrees in [−90, 90], or null.
double? parseDec(String text) {
  final f = _numbers(text);
  if (f == null) return null;
  final v = _sexagesimal(text, f);
  if (v == null || v.abs() > 90) return null;
  return v;
}

/// Split one pasted line into its RA and Dec halves, e.g.
/// `05 35 17.3 -05 23 28`, `83.822 -5.391`, `5h35m17s +22d00m52s`,
/// `05:35:17.3, -05:23:28` or `5h35m17.3s/-5°23'28"`. Splits at the Dec's explicit sign when there is
/// one, else halves an even run of fields. Null when it cannot find two halves.
(String ra, String dec)? splitRaDec(String text) {
  // Commas (`05:35:17.3, -05:23:28`) and Stellarium's slash
  // (`5h35m17.3s/-5°23'28"`) separate the halves like whitespace.
  final t = text
      .replaceAll('−', '-')
      .replaceAll(',', ' ')
      .replaceAll('/', ' ')
      .trim();
  if (t.isEmpty) return null;
  // Explicit sign that is not the leading character (and is not the fraction
  // separator of a number): split there.
  for (var i = 1; i < t.length; i++) {
    final ch = t[i];
    if ((ch == '+' || ch == '-') && t[i - 1] != 'e' && t[i - 1] != 'E') {
      final ra = t.substring(0, i).trim();
      final dec = t.substring(i).trim();
      if (ra.isNotEmpty && dec.isNotEmpty) return (ra, dec);
    }
  }
  final fields = t.split(RegExp(r'\s+'));
  if (fields.length.isOdd || fields.isEmpty) return null;
  final half = fields.length ~/ 2;
  return (
    fields.sublist(0, half).join(' '),
    fields.sublist(half).join(' '),
  );
}

/// Parse a full RA/Dec line; see [splitRaDec] and [parseRa].
ParsedCoordinates? parseRaDec(
  String text, {
  RaUnit decimalUnit = RaUnit.degrees,
}) {
  final halves = splitRaDec(text);
  if (halves == null) return null;
  final ra = parseRa(halves.$1, decimalUnit: decimalUnit);
  final dec = parseDec(halves.$2);
  if (ra == null || dec == null) return null;
  return ParsedCoordinates(ra, dec);
}

/// Does this search-box text look like coordinates rather than a name? True
/// when it parses as a pair and contains no letters other than unit markers —
/// "NGC 7000" and "Vega" stay names.
bool looksLikeCoordinates(String text) {
  if (RegExp(r'[a-ce-gi-ln-rt-zA-CE-GI-LN-RT-Z]').hasMatch(text)) return false;
  return parseRaDec(text) != null;
}
