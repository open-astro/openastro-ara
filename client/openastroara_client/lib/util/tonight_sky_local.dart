import 'dart:math' as math;

import '../services/dso_catalog_service.dart';
import '../services/tonight_sky_api.dart';
import '../state/settings/camera_electronics_state.dart';
import '../state/settings/filter_set_state.dart';
import '../state/settings/optics_settings_state.dart';
import '../state/settings/site_settings_state.dart';
import 'filter_advice.dart';
import 'imaging_regions.dart';
import 'integration_budget.dart';
import 'optimal_sub.dart';
import 'star_model.dart' as stars;

/// THE Tonight's Sky ranker (PORT_DECISIONS 2026-07-15 — planning compute
/// lives in the client): a faithful Dart port of the removed daemon
/// `TonightSkyService` (self-contained Meeus math, no ephemeris dependency).
/// Runs unconditionally, connected or not, over the mirrored openngc-dso
/// catalog (dso_catalog_state.dart) — or the daemon's old 20-object starter
/// list as the never-connected last resort.
///
/// TWIN ALERT: the daemon keeps a C# twin of the astronomy statics in
/// OpenAstroAra.Server/Services/SiteAstrometry.cs (its 3am safety math) — any
/// formula or constant change here MUST be mirrored there. The parity tests
/// in test/util/ pin the constants and catch drift.
///
/// Includes the NEXTGEN §1/§3.1 advisory stack (filter advice, optimal-sub
/// figures, star-detectability tags) via the client-side ports in
/// filter_advice.dart / optimal_sub.dart / star_model.dart, fed by the cached
/// filter set + camera electronics. The §36 custom terrain horizon is now
/// honored when the caller passes its points (hydrated per server since the
/// planning-settings bootstrap): visibility gates on the interpolated skyline
/// altitude at the object's azimuth instead of one flat number, so a low
/// eastern horizon FINDS more objects and a tall western treeline correctly
/// drops them. Flat default-horizon altitude gates when no polygon is given.

// ── Score weights / thresholds (mirror TonightSkyService.cs; see
//    design/TONIGHT_SKY.md for the rationale) ────────────────────────────────
const double _framingWeight = 35.0;
const double _hoursWeight = 25.0;
const double _altitudeWeight = 20.0;
const double _surfaceBrightnessWeight = 12.0;
const double _magnitudeWeight = 8.0;
// §36.8 framing tiers (framing review, 2026-07-17): ratio = object major axis
// ÷ short FOV side. "Fills frame" is reserved for targets that genuinely
// dominate (≥40%); 15–40% is an honest "good fit" (full-ish points, ramping);
// under 15% the rig is mismatched and the score should say so.
const double _framingTooSmallRatio = 0.15;
const double _framingFillsRatio = 0.40;
const double _framingTooBigRatio = 0.80;
// Q at the bottom of the good-fit ramp (ratio == tooSmall boundary).
const double _framingGoodFitFloorQ = 0.70;
const double _framingFloorQ = 0.15;
const double _hoursSaturationHours = 6.0;
const double _sbContrastSpanMag = 4.0;
const double _sbFloorQ = 0.15;
const double _magFaintFloor = 12.0;

const int _windowStepMinutes = 5;
const int _windowHalfSpanMinutes = 12 * 60;
const double _siderealDegPerDay = 360.98564736629;

PlanningDso _o(String id, String name, String type, double mag, double ra,
        double dec) =>
    PlanningDso(
        id: id, name: name, type: type, magnitude: mag, raDeg: ra, decDeg: dec);

/// The daemon's hardcoded starter catalog (TonightSkyService.Catalog),
/// verbatim: J2000 positions, OpenNGC type codes where definite. The LAST
/// resort — the cached openngc-dso mirror (dso_catalog_state.dart) supersedes
/// it whenever this machine has connected to a daemon with the catalog
/// installed.
final List<PlanningDso> starterTonightCatalog = [
  _o('M31', 'Andromeda Galaxy', 'galaxy', 3.4, 10.685, 41.269),
  _o('M33', 'Triangulum Galaxy', 'galaxy', 5.7, 23.462, 30.660),
  // OpenNGC type codes (not the old human-readable strings) so the §36.8
  // type-based score factor applies on the starter path too (#858 review).
  _o('M45', 'Pleiades', 'OCl', 1.6, 56.750, 24.117),
  _o('M1', 'Crab Nebula', 'SNR', 8.4, 83.633, 22.014),
  _o('M42', 'Orion Nebula', 'HII', 4.0, 83.822, -5.391),
  _o('NGC2237', 'Rosette Nebula', 'HII', 9.0, 97.950, 5.050),
  _o("M81", "Bode's Galaxy", 'galaxy', 6.9, 148.888, 69.065),
  _o('M97', 'Owl Nebula', 'PN', 9.9, 168.699, 55.019),
  _o('M104', 'Sombrero Galaxy', 'galaxy', 8.0, 189.998, -11.623),
  _o('M63', 'Sunflower Galaxy', 'galaxy', 8.6, 198.955, 42.029),
  _o('M51', 'Whirlpool Galaxy', 'galaxy', 8.4, 202.470, 47.195),
  _o('M101', 'Pinwheel Galaxy', 'galaxy', 7.9, 210.802, 54.349),
  _o('M13', 'Hercules Cluster', 'GCl', 5.8, 250.423, 36.461),
  _o('M20', 'Trifid Nebula', 'nebula', 6.3, 270.600, -23.030),
  _o('M8', 'Lagoon Nebula', 'HII', 6.0, 270.904, -24.387),
  _o('M16', 'Eagle Nebula', 'HII', 6.0, 274.700, -13.807),
  _o('M17', 'Omega Nebula', 'HII', 6.0, 275.196, -16.171),
  _o('M57', 'Ring Nebula', 'PN', 8.8, 283.396, 33.029),
  _o('M27', 'Dumbbell Nebula', 'PN', 7.4, 299.901, 22.721),
  _o(
      'NGC7000', 'North America Nebula', 'HII', 4.0, 314.750, 44.330),
];

/// Rank the starter catalog for [site] + [optics] at [atUtc], mirroring the
/// daemon's inclusion gate (an object is listed iff it has a non-empty dark
/// window in ±12 h) and 0–100 score. Returns highest-worth first, capped at
/// [limit]; id breaks exact ties.
List<TonightSkyObject> computeTonightSkyLocal({
  required SiteSettings site,
  required OpticsSettings optics,
  required DateTime atUtc,
  FilterSetSettings filterSet = const FilterSetSettings(filters: []),
  CameraElectronics electronics = const CameraElectronics(),
  List<PlanningDso>? catalog,

  /// §36 custom terrain skyline as (azimuthDeg, altitudeDeg) vertices —
  /// visibility gates on the interpolated altitude at the object's azimuth
  /// when non-empty AND the site has `useCustomHorizon` on. Null/empty (or
  /// the toggle off) falls back to the flat [SiteSettings.defaultHorizonAltitudeDeg].
  List<(double, double)>? customHorizon,
  int mosaicTilesX = 1,
  int mosaicTilesY = 1,
  int limit = 10,
}) {
  // The curated imaging-regions layer (realistic extents + evocative names for
  // the famous complexes, plus region-scale fields with no catalog anchor)
  // rides on top of whichever catalog is in play — see imaging_regions.dart.
  final objects = applyImagingRegions(
      (catalog == null || catalog.isEmpty) ? starterTonightCatalog : catalog);
  final at = atUtc.toUtc();
  final skyline = (site.useCustomHorizon &&
          customHorizon != null &&
          customHorizon.isNotEmpty)
      ? _HorizonSkyline(customHorizon)
      : null;
  // With a skyline, the FLAT pre-filter bar is its lowest vertex (an object
  // that never clears even the lowest terrain can be dropped early); the real
  // per-azimuth check happens in the sample loop.
  final horizon =
      skyline?.minAltitudeDeg ?? site.defaultHorizonAltitudeDeg;
  final lat = site.latitudeDeg;
  final lon = site.longitudeDeg;
  final lst0 = _localSiderealTimeDeg(at, lon);
  final fov = _fovArcmin(optics, mosaicTilesX, mosaicTilesY);

  // The night sample grid, object-independent: LST + is-the-sky-dark at each
  // 5-min sample across ±12 h. The grid is anchored on the NIGHT, not the
  // wall clock: centering ±12 h on a daytime "now" clips the previous night's
  // dusk out of the span minute by minute, so the same target scored lower at
  // noon than at 9 AM (live repro: Eastern Veil 92 → 89 between 10:58 and
  // 11:41). When the sun is up, anchor on the COMING solar midnight so the
  // window always describes tonight; when the sun is already down, "now" sits
  // inside the night and ±12 h covers all of it, so anchor there directly
  // (this also keeps the current night scored through the small hours instead
  // of skipping ahead to the next one).
  final twilight = _twilightSunAltitudeDeg(site.twilightDefinition);
  final sunAtNow = _sunEquatorialDeg(at);
  final sunHaNowDeg = _mod360(lst0 - sunAtNow.$1);
  final sunAltNowDeg =
      _altitudeFromHourAngleDeg(sunAtNow.$2, lat, sunHaNowDeg);
  // Sun hour angle 180° = solar midnight; advance to the next one. Snapped
  // to a 5-min UTC boundary so two daytime asks land on the SAME grid — the
  // midnight estimate drifts by seconds across the day (equation of time)
  // and would otherwise jitter every window timestamp with it.
  final rawAnchor = sunAltNowDeg < twilight
      ? at
      : at.add(Duration(
          milliseconds:
              (_mod360(180.0 - sunHaNowDeg) / 360.0 * 24.0 * 3600000.0)
                  .round()));
  final anchor = DateTime.fromMillisecondsSinceEpoch(
      (rawAnchor.millisecondsSinceEpoch / (_windowStepMinutes * 60000))
              .round() *
          _windowStepMinutes *
          60000,
      isUtc: true);
  const stepsPerSide = _windowHalfSpanMinutes ~/ _windowStepMinutes;
  const sampleCount = stepsPerSide * 2 + 1;
  final sampleUtc = List<DateTime>.filled(sampleCount, at);
  final sampleLstDeg = List<double>.filled(sampleCount, 0);
  final sunIsDown = List<bool>.filled(sampleCount, false);
  for (var i = 0; i < sampleCount; i++) {
    final t =
        anchor.add(Duration(minutes: (i - stepsPerSide) * _windowStepMinutes));
    final lst = _localSiderealTimeDeg(t, lon);
    final sun = _sunEquatorialDeg(t);
    final sunAlt =
        _altitudeFromHourAngleDeg(sun.$2, lat, _mod360(lst - sun.$1));
    sampleUtc[i] = t;
    sampleLstDeg[i] = lst;
    sunIsDown[i] = sunAlt < twilight;
  }

  final sinLat = math.sin(_deg2rad(lat));
  final cosLat = math.cos(_deg2rad(lat));
  final sinHorizon = math.sin(_deg2rad(horizon));

  // Moon advisory grid (display-only, never a score input — same as the
  // daemon). "Up" uses the TRUE horizon: moonlight washes the sky whenever
  // the moon is up at all.
  final moonRaDeg = List<double>.filled(sampleCount, 0);
  final moonDecDeg = List<double>.filled(sampleCount, 0);
  final moonUp = List<bool>.filled(sampleCount, false);
  for (var i = 0; i < sampleCount; i++) {
    final m = _moonEquatorialDeg(sampleUtc[i]);
    moonRaDeg[i] = m.$1;
    moonDecDeg[i] = m.$2;
    moonUp[i] = _altitudeFromHourAngleDeg(
            m.$2, lat, _mod360(sampleLstDeg[i] - m.$1)) >
        0.0;
  }
  // Illumination at the anchor (tonight's midnight when planning by day) so
  // the advisory matches the night being scored, not the daytime moment.
  final moonIlluminationPct =
      (_moonIlluminatedFraction(anchor) * 100.0).roundToDouble();

  // NEXTGEN §1/§3.1 advisory inputs — mirrors the server's Rank preamble.
  // Advice degrades gracefully: empty filter set → no advice; unset
  // electronics/aperture → no optimal-sub figure (deliberately NO Tier-0
  // fallback here, matching the tonight list's stricter contract).
  final exposureConfigured = electronics.readNoiseE > 0 &&
      electronics.fullWellE > 0 &&
      optics.apertureMm > 0 &&
      optics.focalLengthMm > 0 &&
      optics.pixelSizeUm > 0 &&
      optics.reducerFactor > 0;
  final skyMag = effectiveSkyMag(
    bortleClass: site.bortleClass,
    sqmMagPerArcsec2: site.sqmMagPerArcsec2,
  );
  // Per-approach memo: (rounded floor for display, unrounded recommendation,
  // the assembled input for the star-count tag).
  final adviceMemo =
      <TonightFilterAdvice, (double?, double, OptimalSubInput?)>{};
  (double?, double, OptimalSubInput?) adviceFor(TonightFilterAdvice approach) {
    return adviceMemo.putIfAbsent(approach, () {
      final filter = representativeFilter(filterSet, approach);
      if (!exposureConfigured || filter == null) return (null, 0, null);
      final input = OptimalSubInput(
        readNoiseE: electronics.readNoiseE,
        fullWellE: electronics.fullWellE,
        electronsPerAdu: math.max(0, electronics.electronsPerAdu),
        pixelSizeUm: optics.pixelSizeUm,
        apertureMm: optics.apertureMm,
        focalLengthMm: optics.focalLengthMm,
        reducerFactor: optics.reducerFactor,
        quantumEfficiency: electronics.quantumEfficiencyPeak > 0
            ? electronics.quantumEfficiencyPeak
            : defaultQuantumEfficiency,
        skyMagPerArcsec2: skyMag,
        filterBandwidthNm: effectiveBandwidthNm(filter),
      );
      final result = computeOptimalSub(input);
      return (
        result.recommendedSec.roundToDouble(),
        result.recommendedSec,
        input
      );
    });
  }

  // §3.1 star-detectability tag inputs: SINGLE-FRAME field (registration is
  // per sub) + the profile-snapshot seeing. Advisory-degrading throughout.
  final singleFrame = _fovArcmin(optics);
  final singleFrameFovDeg2 = singleFrame.$1 / 60.0 * (singleFrame.$2 / 60.0);
  final seeingArcsec = site.typicalSeeingArcsec;
  final starTagAvailable = exposureConfigured &&
      singleFrameFovDeg2.isFinite &&
      singleFrameFovDeg2 > 0 &&
      seeingArcsec.isFinite &&
      seeingArcsec > 0;
  final mLimMemo = <TonightFilterAdvice, double?>{};
  double? registrationMLimFor(TonightFilterAdvice approach) {
    return mLimMemo.putIfAbsent(approach, () {
      final (_, recommendedSec, input) = adviceFor(approach);
      if (!starTagAvailable || input == null || recommendedSec <= 0) {
        return null;
      }
      return stars.limitingMagnitude(input, recommendedSec, seeingArcsec,
          snrThreshold: stars.registrationSnr);
    });
  }

  final scored = <(double, TonightSkyObject)>[];
  final up = List<bool>.filled(sampleCount, false);
  for (final o in objects) {
    // Pre-filter: never clears the horizon at upper culmination → never up.
    final peakAltDeg = _maxAltitudeDeg(o.decDeg, lat);
    if (peakAltDeg < horizon) continue;

    final sinDec = math.sin(_deg2rad(o.decDeg));
    final cosDec = math.cos(_deg2rad(o.decDeg));
    for (var i = 0; i < sampleCount; i++) {
      final hDeg = sampleLstDeg[i] - o.raDeg;
      final hRad = _deg2rad(hDeg);
      final cosH = math.cos(hRad);
      final sinAlt = sinDec * sinLat + cosDec * cosLat * cosH;
      if (skyline == null) {
        up[i] = sinAlt >= sinHorizon && sunIsDown[i];
      } else if (!sunIsDown[i] || sinAlt < skyline.sinMinAltitude) {
        up[i] = false; // below even the lowest terrain — no azimuth needed
      } else {
        // Per-azimuth terrain check: azimuth from North (Meeus A + 180°).
        // Multiplied through by cosDec (≥ 0, so the angle is unchanged) —
        // matches the daemon's AzimuthFromHourAngleDeg and avoids the
        // tan(dec) division that blows up at the exact pole (#858 review).
        final azDeg = _mod360(_rad2deg(math.atan2(
                math.sin(hRad) * cosDec,
                cosH * sinLat * cosDec - sinDec * cosLat)) +
            180.0);
        final altDeg = _rad2deg(math.asin(sinAlt.clamp(-1.0, 1.0)));
        up[i] = altDeg >= skyline.altitudeAt(azDeg);
      }
    }

    final run = _longestRun(up);
    if (run.$1 < 0) continue; // no dark window tonight — the only drop

    final windowStart = sampleUtc[run.$1];
    // Each up-sample stands for its whole 5-min slot; the exclusive upper
    // bound is one step past the last up-sample (a single-sample window must
    // not report 0 h).
    final windowEnd =
        sampleUtc[run.$2].add(const Duration(minutes: _windowStepMinutes));
    final integrationHours =
        windowEnd.difference(windowStart).inMinutes / 60.0;
    final remainStart = at.isAfter(windowStart) ? at : windowStart;
    final remainingHours = math.max(
        0.0, windowEnd.difference(remainStart).inMinutes / 60.0);

    // Transit nearest atUtc: hour angle reaches 0 at the sidereal rate.
    final h0 = _mod360(lst0 - o.raDeg);
    final signedDeg = h0 <= 180.0 ? -h0 : 360.0 - h0;
    final transitUtc = at.add(Duration(
        milliseconds:
            (signedDeg / _siderealDegPerDay * 24.0 * 3600000.0).round()));

    final altNow = _altitudeFromHourAngleDeg(o.decDeg, lat, h0);
    final (score, framing, reasons, hoursScore) = _scoreObject(
        o, fov.$1, fov.$2, peakAltDeg, integrationHours, site.bortleClass);

    // Moon context over THIS window (separation at the midpoint).
    var moonUpCount = 0;
    for (var i = run.$1; i <= run.$2; i++) {
      if (moonUp[i]) moonUpCount++;
    }
    final moonUpFraction = moonUpCount / (run.$2 - run.$1 + 1);
    final mid = (run.$1 + run.$2) ~/ 2;
    final moonSeparationDeg = _angularSeparationDeg(
        o.raDeg, o.decDeg, moonRaDeg[mid], moonDecDeg[mid]);
    final moonAltMidDeg = _altitudeFromHourAngleDeg(
        moonDecDeg[mid], lat, _mod360(sampleLstDeg[mid] - moonRaDeg[mid]));

    // NEXTGEN §1 filter advice + §3.1 star tag — same zero-point reason
    // pattern as the server: visible in the "Why?" breakdown, never a score
    // input.
    TonightFilterAdvice? advice;
    String? adviceReason;
    double? optimalSubS;
    final adviceLines = <String>[];
    final advised =
        adviseFilter(classifyEmission(o.type), filterSet, site.bortleClass);
    if (advised != null) {
      advice = advised.$1;
      adviceReason = advised.$2;
      optimalSubS = adviceFor(advised.$1).$1;
      adviceLines.add('${_adviceTag(advised.$1)} recommended (+0)');
      final mLim = registrationMLimFor(advised.$1);
      final subS = optimalSubS;
      if (mLim != null && subS != null) {
        final galLat = stars.galacticLatitudeDeg(o.raDeg, o.decDeg);
        final starsPerSub = stars.cumulativeStarsPerDeg2(mLim, galLat) *
            singleFrameFovDeg2;
        if (starsPerSub < stars.minRegistrationStars) {
          adviceLines.add('~${starsPerSub.toStringAsFixed(0)} stars/sub at '
              '${_shortNum(subS)} s — thin for registration (+0)');
        }
      }
    }

    // §36.8 framing review round 2 — two gentle multiplicative adjustments so
    // the ordering matches how imagers actually pick targets:
    //  * Photogenic-type factor: sparse OPEN clusters rarely make compelling
    //    images next to nebulae/galaxies — modest discount (globulars less
    //    so). Advisory-sized on purpose: they stay listed, just ranked down.
    //  * Filter-capability factor: an emission-line target with narrowband
    //    (or duo/tri) glass in the wheel is MORE shootable than the raw score
    //    says (light pollution / moon resilience); the same target with a
    //    broadband-only filter set is less so.
    var adjusted = score;
    final adjustReasons = <String>[];
    if (o.type == 'OCl') {
      adjusted *= 0.85;
      adjustReasons.add('open cluster — usually better visual than imaging '
          'targets (−15%)');
    } else if (o.type == 'GCl') {
      adjusted *= 0.95;
      adjustReasons.add('globular cluster (−5%)');
    }
    if (classifyEmission(o.type) == EmissionClass.emissionLine &&
        filterSet.filters.isNotEmpty) {
      final hasNarrowband = filterSet.filters.any((f) =>
          f.kind == FilterKind.ha ||
          f.kind == FilterKind.oiii ||
          f.kind == FilterKind.sii ||
          f.kind == FilterKind.duo ||
          f.kind == FilterKind.tri);
      if (hasNarrowband) {
        adjusted *= 1.05;
        adjustReasons
            .add('emission target + narrowband in your wheel (+5%)');
      } else {
        adjusted *= 0.85;
        adjustReasons.add(
            'emission target but no narrowband filter in your set (−15%)');
      }
    }
    final finalScore = adjusted.clamp(0.0, 100.0);

    // §Integration Budget P3 - the tiered "how many hours from YOUR sky"
    // line for the advised approach (design/INTEGRATION_BUDGET.md; validated
    // against the Texas NGC 6188 campaign). Needs the advised approach's
    // flux input AND a catalog surface brightness; degrades to null.
    String? integrationBudgetLine;
    double? budgetFullHours;
    if (advised != null && o.surfaceBrightness != null) {
      final budgetInput = adviceFor(advised.$1).$3;
      if (budgetInput != null) {
        final budget = computeIntegrationBudget(
          input: budgetInput,
          surfaceBrightnessMagArcsec2: o.surfaceBrightness!,
          peakAltitudeDeg: peakAltDeg,
          moonIlluminatedFraction: moonIlluminationPct / 100.0,
          moonAltitudeDeg: moonAltMidDeg,
          moonSeparationDeg: moonSeparationDeg,
        );
        budgetFullHours = budget.full.practical ? budget.full.hours : null;
        integrationBudgetLine = budget.moonBrighteningMag >= 0.3
            ? '${budget.display}  (tonight\'s moon costs '
                '${budget.moonBrighteningMag.toStringAsFixed(1)} mag)'
            : budget.display;
      }
    }

    final allReasons = [
      ...reasons,
      ...adjustReasons,
      ...adviceLines,
      if (moonUpFraction > 0)
        'moon ${moonSeparationDeg.toStringAsFixed(0)}° away, '
            '${moonIlluminationPct.toStringAsFixed(0)}% lit (+0)'
      else
        'moonless window (+0)',
      // §37.5 soft-warning altitude — advisory only, mirrors the daemon.
      if (site.softWarningAltitudeDeg > 0 &&
          peakAltDeg < site.softWarningAltitudeDeg)
        'stays below your ${site.softWarningAltitudeDeg.toStringAsFixed(0)}° '
            'soft-altitude mark (+0)',
      // Make the offline provenance visible in the "Why?" breakdown.
      'offline ranking — starter catalog, cached site (+0)',
    ];

    scored.add((
      finalScore,
      TonightSkyObject(
        id: o.id,
        name: o.name,
        type: o.type,
        magnitude: o.magnitude,
        raDeg: o.raDeg,
        decDeg: o.decDeg,
        altitudeDeg: double.parse(altNow.toStringAsFixed(1)),
        maxAltitudeDeg: double.parse(peakAltDeg.toStringAsFixed(1)),
        sizeMajArcmin: o.sizeMajArcmin,
        sizeMinArcmin: o.sizeMinArcmin,
        posAngleDeg: o.posAngleDeg,
        surfaceBrightness: o.surfaceBrightness,
        windowStartUtc: windowStart,
        windowEndUtc: windowEnd,
        transitUtc: transitUtc,
        integrationHours: double.parse(integrationHours.toStringAsFixed(2)),
        remainingHours: double.parse(remainingHours.toStringAsFixed(2)),
        framing: framing,
        score: double.parse(finalScore.toStringAsFixed(1)),
        // The multiplicative adjustments scale the whole score, so the
        // hours-free remainder scales by the same finalScore/score ratio.
        integrationBudget: integrationBudgetLine,
        budgetFullHours: budgetFullHours,
        hoursFreeScore: score > 0
            ? double.parse(
                (finalScore * (score - hoursScore) / score).toStringAsFixed(1))
            : 0,
        scoreReasons: allReasons,
        filterAdvice: advice,
        adviceReason: adviceReason,
        optimalSubS: optimalSubS,
        moonSeparationDeg:
            double.parse(moonSeparationDeg.toStringAsFixed(1)),
        moonIlluminationPct: moonIlluminationPct,
        moonUpFraction: double.parse(moonUpFraction.toStringAsFixed(2)),
      )
    ));
  }

  scored.sort((a, b) {
    final c = b.$1.compareTo(a.$1);
    return c != 0 ? c : a.$2.id.compareTo(b.$2.id);
  });
  final capped = limit > 0 && scored.length > limit
      ? scored.sublist(0, limit)
      : scored;
  return [for (final s in capped) s.$2];
}

// ── Scoring (port of ScoreObject) ──────────────────────────────────────────

(double, TonightFraming, List<String>, double) _scoreObject(
    PlanningDso o,
    double fovWidthArcmin,
    double fovHeightArcmin,
    double peakAltDeg,
    double integrationHours,
    int bortleClass) {
  final reasons = <String>[];

  // 1. Framing fit (dominant) — full parity with the daemon now that the
  //    cached catalog carries sizes; the starter list (no sizes) stays Unknown.
  final framing =
      _classifyFraming(o.sizeMajArcmin, fovWidthArcmin, fovHeightArcmin);
  double framingQ;
  String framingTag;
  if (framing == TonightFraming.unknown) {
    framingQ = 0.5; // neutral — no size to judge
    framingTag = 'size unknown';
  } else {
    final ratio =
        o.sizeMajArcmin! / math.min(fovWidthArcmin, fovHeightArcmin);
    switch (framing) {
      case TonightFraming.good:
        framingQ = 1.0;
        framingTag = 'fills the frame';
      case TonightFraming.goodFit:
        // Ramp 0.70 → 1.0 across the 15–40% band: a clear, worthwhile target
        // that would still benefit from a longer focal length.
        framingQ = _framingGoodFitFloorQ +
            (1.0 - _framingGoodFitFloorQ) *
                (ratio - _framingTooSmallRatio) /
                (_framingFillsRatio - _framingTooSmallRatio);
        framingTag = 'good fit in frame';
      case TonightFraming.tooSmall:
        // Ramp toward the good-fit floor as the object approaches 15%.
        framingQ = math.max(
            _framingFloorQ, _framingGoodFitFloorQ * ratio / _framingTooSmallRatio);
        framingTag = 'small in frame';
      default: // tooBig
        framingQ = math.max(_framingFloorQ, _framingTooBigRatio / ratio);
        framingTag = 'overflows the frame';
    }
  }
  final framingScore = _framingWeight * framingQ;
  reasons.add('$framingTag (+${framingScore.toStringAsFixed(0)})');

  // 2. Integration hours — linear ramp saturating at 6 h.
  final hoursScore = _hoursWeight *
      (integrationHours / _hoursSaturationHours).clamp(0.0, 1.0);
  final h = integrationHours;
  final hoursLabel =
      h == h.roundToDouble() ? h.toStringAsFixed(0) : h.toStringAsFixed(1);
  reasons.add('$hoursLabel h dark window (+${hoursScore.toStringAsFixed(0)})');

  // 3. Peak altitude — sin(peak alt) tracks 1/airmass.
  final altScore =
      _altitudeWeight * math.max(0.0, math.sin(_deg2rad(peakAltDeg)));
  reasons.add(
      'peak ${peakAltDeg.toStringAsFixed(0)}° (+${altScore.toStringAsFixed(0)})');

  // 4. Surface brightness vs the Bortle sky — penalised, never zeroed.
  double sbQ;
  String sbTag;
  final sb = o.surfaceBrightness;
  if (sb != null) {
    final contrastMag = skyMagFromBortle(bortleClass) - sb;
    sbQ = ((contrastMag + _sbContrastSpanMag) / _sbContrastSpanMag)
        .clamp(_sbFloorQ, 1.0);
    sbTag = contrastMag >= 0
        ? 'bright for Bortle $bortleClass sky'
        : 'faint for Bortle $bortleClass sky';
  } else {
    sbQ = 0.5;
    sbTag = 'surface brightness unknown';
  }
  final sbScore = _surfaceBrightnessWeight * sbQ;
  reasons.add('$sbTag (+${sbScore.toStringAsFixed(0)})');

  // 5. Integrated magnitude — brighter nudged up, saturating at mag 12.
  final magScore = _magnitudeWeight *
      ((_magFaintFloor - o.magnitude) / _magFaintFloor).clamp(0.0, 1.0);
  final m = o.magnitude;
  final magLabel =
      m == m.roundToDouble() ? m.toStringAsFixed(0) : m.toStringAsFixed(1);
  reasons.add('mag $magLabel (+${magScore.toStringAsFixed(0)})');

  final score = (framingScore + hoursScore + altScore + sbScore + magScore)
      .clamp(0.0, 100.0);
  return (score, framing, reasons, hoursScore);
}

TonightFraming _classifyFraming(
    double? sizeMajArcmin, double fovWidthArcmin, double fovHeightArcmin) {
  if (sizeMajArcmin == null ||
      sizeMajArcmin <= 0 ||
      fovWidthArcmin.isNaN ||
      fovHeightArcmin.isNaN) {
    return TonightFraming.unknown;
  }
  final minFov = math.min(fovWidthArcmin, fovHeightArcmin);
  if (minFov <= 0) return TonightFraming.unknown;
  final ratio = sizeMajArcmin / minFov;
  if (ratio < _framingTooSmallRatio) return TonightFraming.tooSmall;
  if (ratio > _framingTooBigRatio) return TonightFraming.tooBig;
  return ratio >= _framingFillsRatio
      ? TonightFraming.good
      : TonightFraming.goodFit;
}

/// FOV (arcmin) of the optical train, enlarged by the mosaic tile count per
/// axis; (NaN, NaN) when unconfigured.
(double, double) _fovArcmin(OpticsSettings optics,
    [int mosaicTilesX = 1, int mosaicTilesY = 1]) {
  final effectiveFocalMm = optics.focalLengthMm * optics.reducerFactor;
  if (effectiveFocalMm <= 0 ||
      optics.pixelSizeUm <= 0 ||
      optics.sensorWidthPx <= 0 ||
      optics.sensorHeightPx <= 0) {
    return (double.nan, double.nan);
  }
  final pixelScaleArcsec = 206.265 * optics.pixelSizeUm / effectiveFocalMm;
  return (
    optics.sensorWidthPx * pixelScaleArcsec / 60.0 * math.max(1, mosaicTilesX),
    optics.sensorHeightPx * pixelScaleArcsec / 60.0 * math.max(1, mosaicTilesY),
  );
}

// ── Astronomy (ports of the Meeus helpers in TonightSkyService.cs) ─────────

double _twilightSunAltitudeDeg(TwilightDefinition twilight) =>
    switch (twilight) {
      TwilightDefinition.civil => -6.0,
      TwilightDefinition.nautical => -12.0,
      TwilightDefinition.astronomical => -18.0,
    };

double _julianDate(DateTime utc) =>
    utc.millisecondsSinceEpoch / 86400000.0 + 2440587.5;

/// Sun RA/Dec (deg) — Meeus ch. 25 low-accuracy solar position (≈0.01°).
(double, double) _sunEquatorialDeg(DateTime atUtc) {
  final t = (_julianDate(atUtc) - 2451545.0) / 36525.0;
  final l0 = _mod360(280.46646 + 36000.76983 * t + 0.0003032 * t * t);
  final m = _deg2rad(_mod360(357.52911 + 35999.05029 * t - 0.0001537 * t * t));
  final c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * math.sin(m) +
      (0.019993 - 0.000101 * t) * math.sin(2 * m) +
      0.000289 * math.sin(3 * m);
  final lambda = _deg2rad(l0 + c);
  final eps = _deg2rad(23.439);
  final ra = math.atan2(math.cos(eps) * math.sin(lambda), math.cos(lambda));
  final dec =
      math.asin((math.sin(eps) * math.sin(lambda)).clamp(-1.0, 1.0));
  return (_mod360(_rad2deg(ra)), _rad2deg(dec));
}

/// Moon RA/Dec (deg) — Astronomical Almanac low-precision series (≈0.3°,
/// geocentric; an advisory figure, not an ephemeris).
(double, double) _moonEquatorialDeg(DateTime atUtc) {
  final t = (_julianDate(atUtc) - 2451545.0) / 36525.0;
  final lambda = _mod360(218.32 + 481267.881 * t) +
      6.29 * _sinDeg(135.0 + 477198.87 * t) -
      1.27 * _sinDeg(259.3 - 413335.36 * t) +
      0.66 * _sinDeg(235.7 + 890534.22 * t) +
      0.21 * _sinDeg(269.9 + 954397.74 * t) -
      0.19 * _sinDeg(357.5 + 35999.05 * t) -
      0.11 * _sinDeg(186.5 + 966404.03 * t);
  final beta = 5.13 * _sinDeg(93.3 + 483202.02 * t) +
      0.28 * _sinDeg(228.2 + 960400.89 * t) -
      0.28 * _sinDeg(318.3 + 6003.15 * t) -
      0.17 * _sinDeg(217.6 - 407332.21 * t);
  final l = _deg2rad(lambda);
  final b = _deg2rad(beta);
  final eps = _deg2rad(23.439);
  final x = math.cos(b) * math.cos(l);
  final y = math.cos(eps) * math.cos(b) * math.sin(l) -
      math.sin(eps) * math.sin(b);
  final z = math.sin(eps) * math.cos(b) * math.sin(l) +
      math.cos(eps) * math.sin(b);
  final ra = math.atan2(y, x);
  final dec = math.asin(z.clamp(-1.0, 1.0));
  return (_mod360(_rad2deg(ra)), _rad2deg(dec));
}

/// Illuminated disc fraction 0–1 (Meeus ch. 48, sun at infinity).
double _moonIlluminatedFraction(DateTime atUtc) {
  final sun = _sunEquatorialDeg(atUtc);
  final moon = _moonEquatorialDeg(atUtc);
  final psi =
      _deg2rad(_angularSeparationDeg(sun.$1, sun.$2, moon.$1, moon.$2));
  return (1.0 - math.cos(psi)) / 2.0;
}

double _angularSeparationDeg(
    double ra1Deg, double dec1Deg, double ra2Deg, double dec2Deg) {
  final d1 = _deg2rad(dec1Deg);
  final d2 = _deg2rad(dec2Deg);
  final cosSep = math.sin(d1) * math.sin(d2) +
      math.cos(d1) * math.cos(d2) * math.cos(_deg2rad(ra1Deg - ra2Deg));
  return _rad2deg(math.acos(cosSep.clamp(-1.0, 1.0)));
}

/// Local apparent sidereal time (deg) — Meeus low-precision GMST +
/// east-positive longitude.
double _localSiderealTimeDeg(DateTime atUtc, double longitudeDeg) {
  final d = _julianDate(atUtc) - 2451545.0;
  final gmst = _mod360(280.46061837 + 360.98564736629 * d);
  return _mod360(gmst + longitudeDeg);
}

double _altitudeFromHourAngleDeg(
    double decDeg, double latDeg, double hourAngleDeg) {
  final dec = _deg2rad(decDeg);
  final lat = _deg2rad(latDeg);
  final h = _deg2rad(hourAngleDeg);
  final sinAlt = math.sin(dec) * math.sin(lat) +
      math.cos(dec) * math.cos(lat) * math.cos(h);
  return _rad2deg(math.asin(sinAlt.clamp(-1.0, 1.0)));
}

/// Geometric upper-culmination altitude: 90 − |φ − δ|.
double _maxAltitudeDeg(double decDeg, double latDeg) =>
    (90.0 - (latDeg - decDeg).abs()).clamp(-90.0, 90.0);

/// Longest contiguous run of true, inclusive (start, end); (−1, −1) if none.
(int, int) _longestRun(List<bool> flags) {
  var bestStart = -1, bestEnd = -1, curStart = -1;
  for (var i = 0; i < flags.length; i++) {
    if (flags[i] && curStart < 0) curStart = i;
    final runEnds = curStart >= 0 && (!flags[i] || i == flags.length - 1);
    if (runEnds) {
      final curEnd = flags[i] ? i : i - 1;
      if (bestStart < 0 || curEnd - curStart > bestEnd - bestStart) {
        bestStart = curStart;
        bestEnd = curEnd;
      }
      curStart = -1;
    }
  }
  return (bestStart, bestEnd);
}

double _mod360(double x) {
  final r = x % 360.0;
  return r < 0 ? r + 360.0 : r;
}

/// §36 custom terrain skyline: sorted (azimuth, altitude) vertices with
/// wrap-around linear interpolation (the daemon interpolates the same way).
class _HorizonSkyline {
  final List<(double, double)> _sorted;
  final double minAltitudeDeg;
  final double sinMinAltitude;

  _HorizonSkyline(List<(double, double)> vertices)
      : _sorted = vertices.map((v) => (_mod360(v.$1), v.$2)).toList()
          ..sort((a, b) => a.$1.compareTo(b.$1)),
        minAltitudeDeg =
            vertices.map((v) => v.$2).reduce((a, b) => a < b ? a : b),
        sinMinAltitude = math.sin(_deg2rad(
            vertices.map((v) => v.$2).reduce((a, b) => a < b ? a : b)));

  /// Interpolated skyline altitude at [azDeg] (0–360, wrapping north).
  double altitudeAt(double azDeg) {
    final az = _mod360(azDeg);
    if (_sorted.length == 1) return _sorted.first.$2;
    // Find the bracketing pair (wrapping past 360° back to the first vertex).
    for (var i = 0; i < _sorted.length; i++) {
      final a = _sorted[i];
      final b = _sorted[(i + 1) % _sorted.length];
      final spanEnd = i + 1 == _sorted.length ? b.$1 + 360.0 : b.$1;
      final target = az < a.$1 && i + 1 == _sorted.length ? az + 360.0 : az;
      if (target >= a.$1 && target <= spanEnd) {
        final span = spanEnd - a.$1;
        if (span <= 0) return a.$2;
        final t = (target - a.$1) / span;
        return a.$2 + (b.$2 - a.$2) * t;
      }
    }
    return _sorted.first.$2; // unreachable with a well-formed polygon
  }
}

double _deg2rad(double d) => d * math.pi / 180.0;
double _rad2deg(double r) => r * 180.0 / math.pi;
double _sinDeg(double deg) => math.sin(_deg2rad(_mod360(deg)));

String _adviceTag(TonightFilterAdvice approach) => switch (approach) {
      TonightFilterAdvice.narrowband => 'narrowband',
      TonightFilterAdvice.duoband => 'OSC + dual-band',
      TonightFilterAdvice.broadband => 'broadband',
    };

String _shortNum(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);
