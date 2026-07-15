import 'dart:math' as math;

import 'optimal_sub.dart';

/// NEXTGEN §3.1 — the galactic star-count model + limiting-magnitude solver +
/// star-detectability floor, ported from the daemon's `StarCountModel`,
/// `StarDetectability` and `StarDetectabilityFloor` under the 2026-07-15
/// PORT_DECISIONS call. Answers "how many registerable stars will a sub of
/// length t contain in this field?" — a PLANNING prediction, distinct from
/// ASTAP's job of detecting stars in a frame that already exists.
///
/// Provenance carried from the C# original: every constant derives from the
/// canonical HYG snapshot (hygdata_v40, the same sha256 DataManagerService
/// pins) via scripts/fit-star-count-model.py, with the pinned validation gate
/// (each band's fit extrapolated to m=9 lands within 2× of the pooled
/// density). Counts extrapolated beyond mag 9 run OPTIMISTIC — the advisory
/// under-warns, and user-facing strings label the extrapolation.

/// Conventional point-source detection threshold: SNR 5 = "detected".
const double defaultDetectionSnr = 5;

/// Registration-quality centroid threshold.
const double registrationSnr = 10;

/// The advisory "comfortable" registration star budget per sub.
const double minRegistrationStars = 20;

/// Upper bound of the star-floor search — beyond an hour the advice isn't
/// actionable (guiding/wind/satellites dominate long before).
const double maxStarFloorSec = 3600;
const double _minSearchSec = 0.01;

// Band centres as sin|b| for 0°,10°,20°,30°,50°,70°,90° — full precision so a
// query at a band centre lands exactly on its knot.
final List<double> _bandSinB = [
  0.0,
  math.sin(10.0 * math.pi / 180.0),
  math.sin(20.0 * math.pi / 180.0),
  math.sin(30.0 * math.pi / 180.0),
  math.sin(50.0 * math.pi / 180.0),
  math.sin(70.0 * math.pi / 180.0),
  1.0,
];

// Actual pooled HYG densities N(<9)/deg² per band (the anchor).
const List<double> _densityAtMag9 = [
  2.941788, 2.517927, 2.121440, 1.872188, 1.649431, 1.546706, 1.458785,
];

// Validated per-band log-linear slopes d(log₁₀N)/dm from the m ∈ [5,8] fits.
const List<double> _slopePerMag = [
  0.468471, 0.466291, 0.458120, 0.502271, 0.473209, 0.457310, 0.439958,
];

/// Cumulative stars per square degree brighter than [limitingMag] at galactic
/// latitude [galacticLatitudeDeg] (sign ignored — symmetric about the plane).
double cumulativeStarsPerDeg2(double limitingMag, double galacticLatitudeDeg) {
  if (!limitingMag.isFinite) {
    throw ArgumentError('limiting magnitude must be finite');
  }
  if (!galacticLatitudeDeg.isFinite || galacticLatitudeDeg.abs() > 90.0) {
    throw ArgumentError('galactic latitude must be finite and within ±90°');
  }
  final sinB = math.sin(galacticLatitudeDeg.abs() * math.pi / 180.0);
  final n9 = _interpolateInSinB(_densityAtMag9, sinB);
  final slope = _interpolateInSinB(_slopePerMag, sinB);
  return n9 * math.pow(10.0, slope * (limitingMag - 9.0));
}

/// Galactic latitude (deg, signed) of an equatorial J2000 position — the
/// standard NGP rotation. RA is in DEGREES (catalog/planning convention, not
/// the telescope layer's hours).
double galacticLatitudeDeg(double raDeg, double decDeg) {
  if (!raDeg.isFinite) throw ArgumentError('RA must be finite');
  if (!decDeg.isFinite || decDeg.abs() > 90.0) {
    throw ArgumentError('declination must be finite and within ±90°');
  }
  const ngpRa = 192.85948 * math.pi / 180.0;
  const ngpDec = 27.12825 * math.pi / 180.0;
  final ra = raDeg * math.pi / 180.0;
  final dec = decDeg * math.pi / 180.0;
  final sinB = math.sin(ngpDec) * math.sin(dec) +
      math.cos(ngpDec) * math.cos(dec) * math.cos(ra - ngpRa);
  return math.asin(sinB.clamp(-1.0, 1.0)) * 180.0 / math.pi;
}

double _interpolateInSinB(List<double> values, double sinB) {
  if (sinB <= _bandSinB[0]) return values[0];
  for (var i = 1; i < _bandSinB.length; i++) {
    if (sinB <= _bandSinB[i]) {
      final f = (sinB - _bandSinB[i - 1]) / (_bandSinB[i] - _bandSinB[i - 1]);
      return values[i - 1] + f * (values[i] - values[i - 1]);
    }
  }
  return values.last;
}

/// The seeing-disc footprint in pixels, floored at 1 (an undersampled rig
/// concentrates the star inside one pixel).
double seeingDiscPixels(OptimalSubInput input, double seeingFwhmArcsec) {
  if (!seeingFwhmArcsec.isFinite || seeingFwhmArcsec <= 0) {
    throw ArgumentError('seeing FWHM must be a positive, finite arcsec figure');
  }
  final scale = pixelScaleArcsec(input);
  if (!scale.isFinite || scale <= 0) {
    throw ArgumentError(
        'rig geometry must yield a positive, finite plate scale');
  }
  final discAreaArcsec2 = math.pi * math.pow(seeingFwhmArcsec / 2.0, 2);
  return math.max(1.0, discAreaArcsec2 / (scale * scale));
}

/// The faintest V magnitude reaching [snrThreshold] in one sub of
/// [exposureSec]. Closed form: `S* = (k² + √(k⁴ + 4k²N)) / 2`, then
/// `m_lim = −2.5·log₁₀(S* / (F₀·BW·A·QE·t))`.
double limitingMagnitude(
    OptimalSubInput input, double exposureSec, double seeingFwhmArcsec,
    {double snrThreshold = defaultDetectionSnr}) {
  if (!exposureSec.isFinite || exposureSec <= 0) {
    throw ArgumentError('exposure must be a positive, finite seconds figure');
  }
  if (!snrThreshold.isFinite || snrThreshold <= 0) {
    throw ArgumentError('SNR threshold must be positive and finite');
  }
  if (!input.readNoiseE.isFinite || input.readNoiseE < 0) {
    throw ArgumentError('read noise must be finite and ≥ 0 (0 = unset)');
  }

  final p = skyFluxEPerSecPerPx(input); // validates rig/sky inputs too
  final nPix = seeingDiscPixels(input, seeingFwhmArcsec);
  final readNoise = input.readNoiseE > 0 ? input.readNoiseE : defaultReadNoiseE;

  final n = nPix * (p * exposureSec + readNoise * readNoise);
  final k2 = snrThreshold * snrThreshold;
  final sMin = (k2 + math.sqrt(k2 * k2 + 4.0 * k2 * n)) / 2.0;

  final mag0ElectronsPerSec = photonFluxMag0PerCm2PerNm *
      input.filterBandwidthNm *
      apertureAreaCm2(input) *
      input.quantumEfficiency;
  return -2.5 * _log10(sMin / (mag0ElectronsPerSec * exposureSec));
}

/// Predicted stars in [fovDeg2] square degrees whose per-sub SNR reaches
/// [snrThreshold] in a sub of [exposureSec].
double predictedStars(OptimalSubInput input, double exposureSec,
    double seeingFwhmArcsec, double galLatDeg, double fovDeg2,
    double snrThreshold) {
  if (!(fovDeg2.isFinite && fovDeg2 > 0)) {
    throw ArgumentError('field of view must be a positive, finite deg² figure');
  }
  final mLim = limitingMagnitude(input, exposureSec, seeingFwhmArcsec,
      snrThreshold: snrThreshold);
  return cumulativeStarsPerDeg2(mLim, galLatDeg) * fovDeg2;
}

/// `t_stars`: the shortest sub whose predicted registration-quality star
/// count reaches [minStars] — or null when even [maxStarFloorSec] can't (a
/// starved field). Log-domain bisection; monotone in exposure.
double? starFloorSec(OptimalSubInput input, double seeingFwhmArcsec,
    double galLatDeg, double fovDeg2,
    {double minStars = minRegistrationStars}) {
  if (!(minStars.isFinite && minStars > 0)) {
    throw ArgumentError('the star budget must be positive and finite');
  }
  double starsAt(double t) => predictedStars(
      input, t, seeingFwhmArcsec, galLatDeg, fovDeg2, registrationSnr);

  if (starsAt(maxStarFloorSec) < minStars) return null;
  if (starsAt(_minSearchSec) >= minStars) return _minSearchSec;
  var lo = math.log(_minSearchSec);
  var hi = math.log(maxStarFloorSec);
  for (var i = 0; i < 60; i++) {
    final mid = (lo + hi) / 2.0;
    if (starsAt(math.exp(mid)) >= minStars) {
      hi = mid;
    } else {
      lo = mid;
    }
  }
  return math.exp(hi);
}

/// Folds the star-detectability floor into a computed Glover window per the
/// §3.1 presentation rules (effective floor = max(read-noise floor, t_stars),
/// recommendation capped by the ceiling, counts evaluated at the FINAL
/// recommendation). RA in degrees.
OptimalSubResult augmentWithStarFloor(OptimalSubResult window,
    OptimalSubInput input, double seeingFwhmArcsec, double raDeg,
    double decDeg, double fovDeg2) {
  final galLat = galacticLatitudeDeg(raDeg, decDeg);
  final tStars = starFloorSec(input, seeingFwhmArcsec, galLat, fovDeg2);

  final effectiveFloor = math.max(window.floorSec, tStars ?? window.floorSec);
  final viable = effectiveFloor <= window.ceilingSec;
  final recommended = math.min(effectiveFloor, window.ceilingSec);
  final starsBind = tStars != null && tStars > window.floorSec;
  final bound = !viable
      ? OptimalSubBound.saturationCeiling
      : starsBind
          ? OptimalSubBound.starFloor
          : OptimalSubBound.readNoiseFloor;

  final detected = predictedStars(input, recommended, seeingFwhmArcsec,
      galLat, fovDeg2, defaultDetectionSnr);
  final registration = predictedStars(input, recommended, seeingFwhmArcsec,
      galLat, fovDeg2, registrationSnr);
  final mLimAtRecommended = limitingMagnitude(
      input, recommended, seeingFwhmArcsec,
      snrThreshold: registrationSnr);

  return OptimalSubResult(
    skyFluxEPerSecPerPx: window.skyFluxEPerSecPerPx,
    floorSec: window.floorSec,
    ceilingSec: window.ceilingSec,
    viable: viable,
    limitingBound: bound,
    recommendedSec: recommended,
    starFloorSec: tStars,
    starsDetectedPerSub: detected,
    starsRegistrationPerSub: registration,
    starReason: _buildReason(
        window, tStars, recommended, registration, starsBind,
        mLimAtRecommended),
  );
}

String _buildReason(OptimalSubResult gloverWindow, double? tStars,
    double recommendedSec, double registrationStars, bool starsBind,
    double mLimAtRecommended) {
  String text;
  if (tStars == null || registrationStars < minRegistrationStars) {
    text = '~${registrationStars.toStringAsFixed(0)} registration-quality '
        'stars/sub at ${_short(recommendedSec)} s — thin for registration '
        '(~${minRegistrationStars.toStringAsFixed(0)} wanted)';
  } else if (starsBind) {
    text = 'star floor ${_short(recommendedSec)} s: the '
        '${_short(gloverWindow.floorSec)} s read-noise floor is star-starved '
        '— stars, not read noise, set the floor';
  } else {
    text = '~${registrationStars.toStringAsFixed(0)} registration-quality '
        'stars/sub at ${_short(recommendedSec)} s — read noise remains the '
        'binding floor';
  }
  return mLimAtRecommended > 9.0
      ? '$text (star counts extrapolated beyond the catalog\'s mag-9 depth — '
          'optimistic)'
      : text;
}

// {0:0.#}-style formatting: whole numbers bare, else one decimal.
String _short(double v) =>
    v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

double _log10(double x) => math.log(x) / math.ln10;
