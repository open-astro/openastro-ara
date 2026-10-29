import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/integration_budget.dart';

/// #1140 — the Pandeia parity check (INTEGRATION_BUDGET P1). The budget's THEOREM,
/// T = SNR² · (S_t + S_sky) / S_t², is the same photon-noise equation STScI's
/// exposure-time calculators use. These cases pin Ara's implementation of it to
/// what STScI's own engine computes, so the planner's hours rest on the
/// professional implementation, not only on Ara agreeing with itself.
///
/// Source: Pandeia engine 2026.7 (JWST, refdata + PSFs 2026.7), NIRCam F200W
/// imaging, a flat-fν source normalized at 2.0 µm. Pandeia's detector noise is
/// switched off (read noise, excess read noise, dark, flat field, cosmic rays)
/// to compare the photon equation itself; RAPID readout with 2 groups (a CDS
/// slope, so the up-the-ramp fit adds no shot-noise factor); no background
/// annulus (Ara assumes a known sky); a 1.2″ aperture, where Pandeia's
/// fractional edge-pixel weighting moves the answer by < 1 % (at 0.15″ it is
/// 6 %, shrinking as perimeter/area — a geometry effect Ara's per-pixel surface
/// brightness has no counterpart for). `source` and `sky` are Pandeia's
/// extracted source and in-aperture sky rates (e⁻/s), `seconds` its
/// photon-counting (measurement) time, `pandeiaSnr` its answer. Regenerate with
/// `scripts/pandeia_parity.py`.
///
/// With Pandeia's read noise ON and these short 21 s integrations, its SNR is
/// 3–7× lower: exactly the regime the dropped read-noise term excludes, and why
/// the budget assumes subs at or above the Glover floor.
void main() {
  const cases = [
    (
      label: 'source-dominated (S/B ≈ 8), AB 18.5',
      source: 6418.758860551752,
      sky: 812.8661209830691,
      seconds: 1073.677,
      pandeiaSnr: 2473.603039875531,
    ),
    (
      label: 'balanced (S ≈ B), AB 20.74',
      source: 815.5508318314096,
      sky: 812.8660868892123,
      seconds: 2147.354,
      pandeiaSnr: 937.1021455545771,
    ),
    (
      label: 'deep sky-limited (S/B ≈ 1/300), AB 27.0',
      source: 2.5553539576585536,
      sky: 812.866088245218,
      seconds: 4294.708,
      pandeiaSnr: 5.871649323118075,
    ),
  ];

  for (final c in cases) {
    test('Ara matches Pandeia: ${c.label}', () {
      // The time Ara says Pandeia's SNR needs, against the time Pandeia gave it.
      final t = requiredIntegrationSeconds(
        snr: c.pandeiaSnr,
        targetRate: c.source,
        skyRate: c.sky,
      );
      expect(
        t / c.seconds,
        closeTo(1.0, 0.005),
        reason: 'integration time within 0.5 % of Pandeia (measured ≤ 0.25 %)',
      );
      // The same equation solved for SNR at Pandeia's time.
      final snr = c.source * math.sqrt(c.seconds) / math.sqrt(c.source + c.sky);
      expect(
        snr / c.pandeiaSnr,
        closeTo(1.0, 0.0025),
        reason: 'SNR within 0.25 % of Pandeia (measured ≤ 0.12 %)',
      );
    });
  }

  test('the parity cases span the regimes the planner meets', () {
    final ratios = cases.map((c) => c.source / c.sky).toList();
    expect(ratios.first, greaterThan(5), reason: 'a bright core');
    expect(ratios.last, lessThan(0.01), reason: 'a faint, sky-limited tier');
  });
}
