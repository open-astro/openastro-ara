/// J2000 ⇄ JNow for typed-in targets.
///
/// Every coordinate Ara stores is J2000 (the daemon's `InputCoordinates` has
/// no epoch field, and the telescope mediator precesses to the mount's own
/// equatorial system at slew time). A user pasting a position from a mount
/// hand controller, a planetarium readout or an "of date" catalogue has a
/// JNow position, so the Plan screen's coordinate entry converts it here,
/// client-side, before it enters the run — the same path a catalogue target
/// takes, no daemon round-trip (offline-first: planning compute lives in the
/// client).
///
/// "JNow" means what the daemon means by it (`Coordinates.TransformToJNOW`,
/// SOFA `iauAtci13` with `ri − eo`): the geocentric apparent place referred to
/// the true equator and equinox of date — precession, nutation and annual
/// aberration. This is a compact port of that chain (IAU 2006 precession via
/// the Fukushima–Williams angles, the leading IAU 2000B nutation terms, the
/// first-order annual aberration from the Sun's longitude). Against astropy's
/// TETE frame it agrees to well under an arcsecond across the current
/// decades, which is far below what a slew followed by a plate-solved centre
/// can tell apart. Light deflection (≤ 4 mas away from the Sun) and the
/// frame bias (23 mas) are ignored.
library;

import 'dart:math' as math;

const double _d2r = math.pi / 180.0;
const double _as2r = _d2r / 3600.0;

/// J2000 (ICRS) → apparent place of date (true equator and equinox) at [atUtc].
({double raDeg, double decDeg}) j2000ToApparent(
  double raDeg,
  double decDeg, {
  required DateTime atUtc,
}) {
  final t = _centuriesTt(atUtc);
  final p = _unit(raDeg, decDeg);
  final a = _aberrate(p, t);
  final r = _matrix(t);
  final v = [
    r[0][0] * a[0] + r[0][1] * a[1] + r[0][2] * a[2],
    r[1][0] * a[0] + r[1][1] * a[1] + r[1][2] * a[2],
    r[2][0] * a[0] + r[2][1] * a[1] + r[2][2] * a[2],
  ];
  return _sph(v);
}

/// Apparent place of date → J2000 (ICRS): the inverse of [j2000ToApparent],
/// solved by fixed-point iteration on the spherical coordinates (the forward
/// chain is a rotation plus a ~20″ aberration shift; a dozen passes converge
/// to the micro-arcsecond level, including within a degree of the pole where
/// an RA step is a tiny sky step and the contraction is slow).
({double raDeg, double decDeg}) apparentToJ2000(
  double raDeg,
  double decDeg, {
  required DateTime atUtc,
}) {
  var ra = raDeg;
  var dec = decDeg;
  for (var i = 0; i < 12; i++) {
    final f = j2000ToApparent(ra, dec, atUtc: atUtc);
    var dRa = raDeg - f.raDeg;
    if (dRa > 180) dRa -= 360;
    if (dRa < -180) dRa += 360;
    ra = (ra + dRa) % 360;
    if (ra < 0) ra += 360;
    dec = (dec + (decDeg - f.decDeg)).clamp(-90.0, 90.0);
  }
  return (raDeg: ra, decDeg: dec);
}

/// Julian centuries of TT since J2000.0. TT − UTC is taken as the 2017+ value
/// (37 leap seconds + 32.184 s); the few seconds this could drift over the
/// coming years move the result by microarcseconds.
double _centuriesTt(DateTime atUtc) {
  final jdUtc =
      2440587.5 + atUtc.toUtc().millisecondsSinceEpoch / 86400000.0;
  final jdTt = jdUtc + 69.184 / 86400.0;
  return (jdTt - 2451545.0) / 36525.0;
}

List<double> _unit(double raDeg, double decDeg) {
  final ra = raDeg * _d2r, dec = decDeg * _d2r;
  final cd = math.cos(dec);
  return [cd * math.cos(ra), cd * math.sin(ra), math.sin(dec)];
}

({double raDeg, double decDeg}) _sph(List<double> v) {
  var ra = math.atan2(v[1], v[0]) / _d2r;
  if (ra < 0) ra += 360;
  final dec =
      math.atan2(v[2], math.sqrt(v[0] * v[0] + v[1] * v[1])) / _d2r;
  return (raDeg: ra, decDeg: dec);
}

/// First-order annual aberration: shift the direction by Earth's orbital
/// velocity (as a fraction of c) in J2000 equatorial coordinates, derived from
/// the Sun's apparent longitude and the orbit's eccentricity (Meeus ch. 25).
List<double> _aberrate(List<double> p, double t) {
  final l0 = (280.46646 + 36000.76983 * t + 0.0003032 * t * t) * _d2r;
  final m = (357.52911 + 35999.05029 * t - 0.0001537 * t * t) * _d2r;
  final e = 0.016708634 - 0.000042037 * t - 0.0000001267 * t * t;
  final c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * math.sin(m) +
      (0.019993 - 0.000101 * t) * math.sin(2 * m) +
      0.000289 * math.sin(3 * m);
  // Refer the longitudes to the J2000 equinox (general precession in
  // longitude) so the velocity comes out in the ICRS-aligned frame the
  // aberration is applied in, like SOFA's GCRS Earth velocity.
  final precession = 5029.0966 * t * _as2r;
  final lambda = l0 + c * _d2r - precession;
  // Earth's longitude of perihelion, likewise J2000-referred.
  final pi = (102.93735 + 1.71946 * t + 0.00046 * t * t) * _d2r - precession;
  // Aberration constant κ = 20.49552″ as v/c at the mean orbital speed.
  final k = 20.49552 * _as2r / math.sqrt(1 - e * e);
  // Earth's velocity / c in the J2000 ecliptic (x toward the equinox). For a
  // body at true longitude θ the velocity is k·(−(sin θ + e sin ϖ),
  // cos θ + e cos ϖ); the Earth sits at θ = λ + 180°.
  final vx = k * (math.sin(lambda) - e * math.sin(pi));
  final vy = k * (e * math.cos(pi) - math.cos(lambda));
  // Rotate ecliptic → equatorial by the J2000 obliquity.
  final eps = 84381.406 * _as2r;
  final ce = math.cos(eps), se = math.sin(eps);
  final v = [vx, vy * ce, vy * se];
  final s = [p[0] + v[0], p[1] + v[1], p[2] + v[2]];
  final n = math.sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]);
  return [s[0] / n, s[1] / n, s[2] / n];
}

/// IAU 2006 mean obliquity, arcseconds (SOFA `iauObl06`).
double _obliquityArcsec(double t) => 84381.406 +
    (-46.836769 +
            (-0.0001831 +
                    (0.00200340 + (-0.000000576 - 0.0000000434 * t) * t) *
                        t) *
                t) *
        t;

/// Bias–precession–nutation matrix (SOFA `iauPnm06a`): the IAU 2006
/// Fukushima–Williams angles with the nutation folded in, then `iauFw2m`.
List<List<double>> _matrix(double t) {
  final gamb = (-0.052928 +
          (10.556378 +
                  (0.4932044 +
                          (-0.00031238 +
                                  (-0.000002788 + 0.0000000260 * t) * t) *
                              t) *
                      t) *
              t) *
      _as2r;
  final phib = (84381.412819 +
          (-46.811016 +
                  (0.0511268 +
                          (0.00053289 +
                                  (-0.000000440 - 0.0000000176 * t) * t) *
                              t) *
                      t) *
              t) *
      _as2r;
  final psib = (-0.041775 +
          (5038.481484 +
                  (1.5584175 +
                          (-0.00018522 +
                                  (-0.000026452 - 0.0000000148 * t) * t) *
                              t) *
                      t) *
              t) *
      _as2r;
  final epsa = _obliquityArcsec(t) * _as2r;
  final nut = _nutation(t);
  // iauFw2m: Rz(γ̄) · Rx(φ̄) · Rz(−ψ) · Rx(−ε), applied as frame rotations.
  var r = _identity();
  r = _rz(gamb, r);
  r = _rx(phib, r);
  r = _rz(-(psib + nut.dpsi), r);
  r = _rx(-(epsa + nut.deps), r);
  return r;
}

/// The leading luni-solar terms of the IAU 2000B nutation series (SOFA
/// `iauNut00b` rows 1–20, plus its fixed planetary offset). The dropped tail
/// sums to a few milliarcseconds.
({double dpsi, double deps}) _nutation(double t) {
  // Delaunay arguments, arcseconds (the IAU 2000B simplified forms).
  final el = _norm(485868.249036 + 1717915923.2178 * t);
  final elp = _norm(1287104.79305 + 129596581.0481 * t);
  final f = _norm(335779.526232 + 1739527262.8478 * t);
  final d = _norm(1072260.70369 + 1602961601.2090 * t);
  final om = _norm(450160.398036 - 6962890.5431 * t);
  var dp = 0.0, de = 0.0;
  for (final row in _nutRows) {
    final arg = row[0] * el + row[1] * elp + row[2] * f + row[3] * d + row[4] * om;
    final sa = math.sin(arg), ca = math.cos(arg);
    dp += (row[5] + row[6] * t) * sa + row[7] * ca;
    de += (row[8] + row[9] * t) * ca + row[10] * sa;
  }
  // Units: 0.1 µas → arcsec; the IAU 2000B planetary-nutation constants in mas.
  return (
    dpsi: (dp * 1e-7 - 0.135e-3) * _as2r,
    deps: (de * 1e-7 + 0.388e-3) * _as2r,
  );
}

double _norm(double arcsec) => (arcsec % 1296000.0) * _as2r;

// nl nlp nF nD nOm | ps pst pc | ec ect es   (0.1 µas)
const List<List<double>> _nutRows = [
  [0, 0, 0, 0, 1, -172064161, -174666, 33386, 92052331, 9086, 15377],
  [0, 0, 2, -2, 2, -13170906, -1675, -13696, 5730336, -3015, -4587],
  [0, 0, 2, 0, 2, -2276413, -234, 2796, 978459, -485, 1374],
  [0, 0, 0, 0, 2, 2074554, 207, -698, -897492, 470, -291],
  [0, 1, 0, 0, 0, 1475877, -3633, 11817, 73871, -184, -1924],
  [0, 1, 2, -2, 2, -516821, 1226, -524, 224386, -677, -174],
  [1, 0, 0, 0, 0, 711159, 73, -872, -6750, 0, 358],
  [0, 0, 2, 0, 1, -387298, -367, 380, 200728, 18, 318],
  [1, 0, 2, 0, 2, -301461, -36, 816, 129025, -63, 367],
  [0, -1, 2, -2, 2, 215829, -494, 111, -95929, 299, 132],
  [0, 0, 2, -2, 1, 128227, 137, 181, -68982, -9, 39],
  [-1, 0, 2, 0, 2, 123457, 11, 19, -53311, 32, -4],
  [-1, 0, 0, 2, 0, 156994, 10, -168, -1235, 0, 82],
  [1, 0, 0, 0, 1, 63110, 63, 27, -33228, 0, -9],
  [-1, 0, 0, 0, 1, -57976, -63, -189, 31429, 0, -75],
  [-1, 0, 2, 2, 2, -59641, -11, 149, 25543, -11, 66],
  [1, 0, 2, 0, 1, -51613, -42, 129, 26366, 0, 78],
  [-2, 0, 2, 0, 1, 45893, 50, 31, -24236, -10, 20],
  [0, 0, 0, 2, 0, 63384, 11, -150, -1220, 0, 29],
  [0, 0, 2, 2, 2, -38571, -1, 158, 16452, -11, 68],
];

List<List<double>> _identity() => [
      [1, 0, 0],
      [0, 1, 0],
      [0, 0, 1],
    ];

/// Rotate the frame about x by [a] (SOFA `iauRx` semantics: r ← Rx(a)·r).
List<List<double>> _rx(double a, List<List<double>> r) {
  final s = math.sin(a), c = math.cos(a);
  return [
    r[0],
    [
      c * r[1][0] + s * r[2][0],
      c * r[1][1] + s * r[2][1],
      c * r[1][2] + s * r[2][2],
    ],
    [
      -s * r[1][0] + c * r[2][0],
      -s * r[1][1] + c * r[2][1],
      -s * r[1][2] + c * r[2][2],
    ],
  ];
}

/// Rotate the frame about z by [a] (SOFA `iauRz` semantics: r ← Rz(a)·r).
List<List<double>> _rz(double a, List<List<double>> r) {
  final s = math.sin(a), c = math.cos(a);
  return [
    [
      c * r[0][0] + s * r[1][0],
      c * r[0][1] + s * r[1][1],
      c * r[0][2] + s * r[1][2],
    ],
    [
      -s * r[0][0] + c * r[1][0],
      -s * r[0][1] + c * r[1][1],
      -s * r[0][2] + c * r[1][2],
    ],
    r[2],
  ];
}

