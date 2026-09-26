// §31.4 — minimal NMEA 0183 parsing for the two sentences a USB GPS dongle on the
// client computer needs: `$GxRMC` (UTC time + date + position, A/V validity flag)
// and `$GxGGA` (position + altitude, fix-quality digit). A Dart port of the daemon's
// `NmeaSentenceParser` (OpenAstroAra.Server/Services/NmeaParser.cs) with the same
// rules: talker-agnostic, checksum-verified when present, and anything that is not
// a valid, checksummed RMC/GGA with an active fix parses to null rather than
// throwing — the serial stream is untrusted line noise until proven otherwise.

/// A parsed fix. [timeUtc] is only carried by RMC (GGA has no date field);
/// [altitudeM] only by GGA.
class NmeaFix {
  const NmeaFix({this.timeUtc, this.latitudeDeg, this.longitudeDeg, this.altitudeM});

  final DateTime? timeUtc;
  final double? latitudeDeg;
  final double? longitudeDeg;
  final double? altitudeM;

  bool get hasPosition => latitudeDeg != null && longitudeDeg != null;

  @override
  String toString() =>
      'NmeaFix(time: $timeUtc, lat: $latitudeDeg, lng: $longitudeDeg, alt: $altitudeM)';
}

/// Parse one sentence. Null for anything that isn't a valid, checksummed RMC/GGA
/// with an active fix.
NmeaFix? parseNmeaSentence(String? sentence) {
  if (sentence == null) return null;
  final s = sentence.trim();
  if (s.length < 7 || !s.startsWith(r'$')) return null;

  // Optional *hh checksum: XOR of everything between '$' and '*'.
  final star = s.indexOf('*');
  String body;
  if (star >= 0) {
    if (star + 3 > s.length) return null;
    final expected = int.tryParse(s.substring(star + 1, star + 3), radix: 16);
    if (expected == null) return null;
    var actual = 0;
    for (var i = 1; i < star; i++) {
      actual ^= s.codeUnitAt(i) & 0xFF;
    }
    if (actual != expected) return null;
    body = s.substring(1, star);
  } else {
    body = s.substring(1);
  }

  final f = body.split(',');
  if (f.isEmpty || f[0].length != 5) return null;
  switch (f[0].substring(2)) {
    // drop the 2-char talker (GP/GN/GL/…)
    case 'RMC':
      return _parseRmc(f);
    case 'GGA':
      return _parseGga(f);
    default:
      return null;
  }
}

// $GPRMC,hhmmss.sss,A,llll.ll,a,yyyyy.yy,a,speed,course,ddmmyy,…  (A = active, V = void)
NmeaFix? _parseRmc(List<String> f) {
  if (f.length < 10 || f[2] != 'A') return null;
  final time = _parseUtc(f[1], f[9]);
  if (time == null) return null;
  return NmeaFix(
    timeUtc: time,
    latitudeDeg: _parseCoordinate(f[3], f[4], isLatitude: true),
    longitudeDeg: _parseCoordinate(f[5], f[6], isLatitude: false),
  );
}

// $GPGGA,hhmmss.sss,llll.ll,a,yyyyy.yy,a,quality,sats,hdop,alt,M,…  (quality 0 = no fix)
NmeaFix? _parseGga(List<String> f) {
  if (f.length < 10) return null;
  final quality = int.tryParse(f[6]);
  if (quality == null || quality <= 0) return null;
  return NmeaFix(
    latitudeDeg: _parseCoordinate(f[2], f[3], isLatitude: true),
    longitudeDeg: _parseCoordinate(f[4], f[5], isLatitude: false),
    altitudeM: double.tryParse(f[9]),
  );
}

DateTime? _parseUtc(String hhmmss, String ddmmyy) {
  if (hhmmss.length < 6 || ddmmyy.length != 6) return null;
  final hh = int.tryParse(hhmmss.substring(0, 2));
  final mm = int.tryParse(hhmmss.substring(2, 4));
  final ss = int.tryParse(hhmmss.substring(4, 6));
  final dd = int.tryParse(ddmmyy.substring(0, 2));
  final mo = int.tryParse(ddmmyy.substring(2, 4));
  final yy = int.tryParse(ddmmyy.substring(4, 6));
  if ([hh, mm, ss, dd, mo, yy].contains(null)) return null;
  var fractionalMs = 0;
  final dot = hhmmss.indexOf('.');
  if (dot >= 0) {
    final frac = double.tryParse('0${hhmmss.substring(dot)}');
    if (frac == null) return null;
    fractionalMs = (frac * 1000).round();
  }
  // Reject what DateTime would otherwise normalise (e.g. month 13 → next year):
  // a cold receiver emits nonsense dates and the daemon skips those too.
  if (mo! < 1 || mo > 12 || dd! < 1 || dd > 31 || hh! > 23 || mm! > 59 || ss! > 60) {
    return null;
  }
  // NMEA's 2-digit year is unambiguous in practice: GPS predates 2000, ARA doesn't.
  final t = DateTime.utc(2000 + yy!, mo, dd, hh, mm, ss, fractionalMs);
  if (t.month != mo || t.day != dd) return null; // e.g. 31 Feb rolled over
  return t;
}

// NMEA coordinates are ddmm.mmmm (lat) / dddmm.mmmm (lng) with a N/S/E/W hemisphere field.
double? _parseCoordinate(String value, String hemisphere, {required bool isLatitude}) {
  if (value.isEmpty || hemisphere.isEmpty) return null;
  final degDigits = isLatitude ? 2 : 3;
  if (value.length <= degDigits) return null;
  final deg = int.tryParse(value.substring(0, degDigits));
  final minutes = double.tryParse(value.substring(degDigits));
  if (deg == null || minutes == null) return null;
  final result = deg + minutes / 60.0;
  switch (hemisphere) {
    case 'N':
    case 'E':
      return result;
    case 'S':
    case 'W':
      return -result;
    default:
      return null;
  }
}
