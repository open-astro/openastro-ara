/// Sexagesimal formatting for sky coordinates.
///
/// Rounding the fractional second up can land on 60 (e.g. RA 5.9999972 h →
/// "…59m 60s"), which is an invalid coordinate. Both helpers carry a rounded-up
/// second into the minute and a rounded-up minute into the hour/degree so the
/// output never shows 60; RA additionally wraps at 24h.
library;

String _two(int v) => v.toString().padLeft(2, '0');

/// RA in hours → "HHh MMm SSs".
String formatRaHms(double hours) {
  var h = hours.floor();
  final mF = (hours - h) * 60;
  var m = mF.floor();
  var s = ((mF - m) * 60).round();
  if (s == 60) {
    s = 0;
    m += 1;
  }
  if (m == 60) {
    m = 0;
    h += 1;
  }
  if (h == 24) h = 0;
  return '${h}h ${_two(m)}m ${_two(s)}s';
}

/// Dec in degrees → "±DD° MM' SS\"".
String formatDecDms(double deg) {
  final sign = deg < 0 ? '-' : '+';
  // Defensive: valid Dec is [-90°, +90°]. Clamp the magnitude so a server
  // rounding glitch just past a pole can never render an impossible "+91°" /
  // "+90° 30'".
  final a = deg.abs().clamp(0.0, 90.0).toDouble();
  var d = a.floor();
  final mF = (a - d) * 60;
  var m = mF.floor();
  var s = ((mF - m) * 60).round();
  if (s == 60) {
    s = 0;
    m += 1;
  }
  if (m == 60) {
    m = 0;
    d += 1;
  }
  return '$sign$d° ${_two(m)}\' ${_two(s)}"';
}
