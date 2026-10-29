/// §Integration Budget (INTEGRATION_BUDGET) — "how many hours does
/// this target need from MY sky?", the last objective link in the planning
/// chain. Pure math; every constant carries its provenance grade:
///
/// - THEOREM: stack SNR = S_t·√T / √(S_t + S_sky) — the standard astronomical
///   SNR equation (Howell, *Handbook of CCD Astronomy*; the same core as
///   NASA/STScI's HST and JWST exposure-time calculators), with the
///   read-noise and dark terms dropped under exactly the condition the
///   Glover floor guarantees (subs long enough that sky noise dominates) —
///   verified at 501.0-ADU-flat 300 s darks on real hardware (dry run #2).
/// - CONVENTION: SNR 5 = "clean", 3 = "detectable" — the Rose criterion
///   (A. Rose 1948). Airmass via Kasten & Young (1989). Scattered moonlight
///   via Krisciunas & Schaefer (1991), the model behind ESO's sky calculator.
/// - JUDGMENT (documented, tunable in [BudgetTuning]): the +2/+4 mag depth
///   tiers, the 60 h "impractical" cap, the 0.2 mag/airmass broadband
///   extinction default.
///
/// The target and sky fluxes both come from the ONE existing flux chain
/// ([skyFluxEPerSecPerPx]) — validated against 123 real frames (broadband
/// within 5–20 %, narrowband conservative; see the design doc's dry run #2).
library;

import 'dart:math' as math;

import 'optimal_sub.dart';

/// The taste constants, gathered so a future Advanced panel can surface them
/// without surgery (v1 hard-codes per the 2026-07-21 decision).
class BudgetTuning {
  /// Rose-criterion SNR goals per tier (core / full structure / faint).
  final double snrCore;
  final double snrFull;
  final double snrFaint;

  /// Depth-tier surface-brightness offsets (mag/arcsec²) below the catalog
  /// average — the honest spread between a target's bright body and the
  /// faint structure people actually chase.
  final double fullTierOffsetMag;
  final double faintTierOffsetMag;

  /// Above this many hours a tier reads "impractical" instead of a number.
  final double impracticalHours;

  /// Broadband extinction, mag per airmass (Patat 2011: 0.13–0.25 at good
  /// sites; 0.2 is the honest middle for unknown skies).
  final double extinctionMagPerAirmass;

  const BudgetTuning({
    this.snrCore = 5,
    this.snrFull = 5,
    this.snrFaint = 3,
    this.fullTierOffsetMag = 2,
    this.faintTierOffsetMag = 4,
    this.impracticalHours = 60,
    this.extinctionMagPerAirmass = 0.2,
  });

  static const BudgetTuning v1 = BudgetTuning();
}

/// One depth tier's answer: hours to reach its SNR goal, or "impractical".
class IntegrationTier {
  final String label;
  final double hours;
  final bool practical;
  const IntegrationTier(
      {required this.label, required this.hours, required this.practical});
}

/// The budget for one target through one filter approach.
class IntegrationBudget {
  final IntegrationTier core;
  final IntegrationTier full;
  final IntegrationTier faint;

  /// Airmass at the target's peak altitude (Kasten–Young) — the extinction
  /// context the hours were computed under.
  final double airmass;

  /// The extra sky brightness tonight's moon adds on top of the dark sky,
  /// in magnitudes (0 = moon down or new).
  final double moonBrighteningMag;

  /// #1140 — the inputs the hours rest on, so the number is auditable in the
  /// row's detail popover rather than oracular: the catalog surface
  /// brightness, the dark-sky magnitude, the filter passband and the sky /
  /// core-tier target flux in e⁻/s/px (the two terms of T = SNR²·(S_t+S_sky)/S_t²).
  final double surfaceBrightnessMagArcsec2;
  final double darkSkyMagArcsec2;
  final double filterBandwidthNm;
  final double skyFluxEPerSecPx;
  final double coreTargetFluxEPerSecPx;
  final BudgetTuning tuning;

  /// #1140 P2 — the sky-model calibration factor the fluxes were scaled by
  /// (1.0 = spec-sheet model, nothing fitted for this filter kind).
  final double skyCalibrationK;

  const IntegrationBudget({
    required this.core,
    required this.full,
    required this.faint,
    required this.airmass,
    required this.moonBrighteningMag,
    this.surfaceBrightnessMagArcsec2 = double.nan,
    this.darkSkyMagArcsec2 = double.nan,
    this.filterBandwidthNm = double.nan,
    this.skyFluxEPerSecPx = double.nan,
    this.coreTargetFluxEPerSecPx = double.nan,
    this.tuning = BudgetTuning.v1,
    this.skyCalibrationK = 1.0,
  });

  /// The effective sky tonight: dark sky brightened by the moon.
  double get effectiveSkyMagArcsec2 => darkSkyMagArcsec2 - moonBrighteningMag;

  /// Label/value lines for the detail popover (#1140 P3 "auditable, never
  /// oracular"). Finite inputs only — a budget built without them says so.
  List<(String, String)> get auditLines {
    if ([
      surfaceBrightnessMagArcsec2,
      darkSkyMagArcsec2,
      filterBandwidthNm,
      skyFluxEPerSecPx,
      coreTargetFluxEPerSecPx,
    ].any((v) => v.isNaN)) {
      return const [('Budget inputs', 'not recorded')];
    }
    String mag(double v) => '${v.toStringAsFixed(1)} mag/arcsec²';
    String flux(double v) => v >= 10
        ? '${v.toStringAsFixed(0)} e⁻/s/px'
        : '${v.toStringAsFixed(2)} e⁻/s/px';
    return [
      ('Surface brightness', mag(surfaceBrightnessMagArcsec2)),
      (
        'Sky tonight',
        moonBrighteningMag >= 0.05
            ? '${mag(effectiveSkyMagArcsec2)} (dark ${darkSkyMagArcsec2.toStringAsFixed(1)}, moon +${moonBrighteningMag.toStringAsFixed(1)})'
            : mag(darkSkyMagArcsec2),
      ),
      ('Filter passband', '${filterBandwidthNm.toStringAsFixed(0)} nm'),
      ('Airmass at peak', airmass.toStringAsFixed(2)),
      (
        'Sky model calibration',
        skyCalibrationK == 1.0
            ? 'not fitted — spec-sheet model'
            : 'k ${skyCalibrationK.toStringAsFixed(2)} from your frames',
      ),
      ('Sky flux', flux(skyFluxEPerSecPx)),
      ('Core target flux', flux(coreTargetFluxEPerSecPx)),
      (
        'SNR goals',
        'core ${_fmtNum(tuning.snrCore)} · full ${_fmtNum(tuning.snrFull)} (SB +${_fmtNum(tuning.fullTierOffsetMag)}) · faint ${_fmtNum(tuning.snrFaint)} (SB +${_fmtNum(tuning.faintTierOffsetMag)})',
      ),
      ('Impractical beyond', '${_fmtNum(tuning.impracticalHours)} h'),
    ];
  }

  static String _fmtNum(double v) =>
      v == v.roundToDouble() ? v.toStringAsFixed(0) : v.toStringAsFixed(1);

  /// The compact row line: "core ~2 h · full ~9 h · faint impractical".
  String get display {
    String one(IntegrationTier t) =>
        '${t.label} ${t.practical ? "~${_fmtHours(t.hours)}" : "impractical"}';
    return '${one(core)} · ${one(full)} · ${one(faint)}';
  }

  static String _fmtHours(double h) => formatBudgetHours(h);
}

/// "40 min" / "2.8 h" / "9 h" / "35 h" — one formatter for the tier line and
/// the tier standing.
String formatBudgetHours(double h) {
  if (h < 0.95) {
    final m = (h * 60 / 10).ceil() * 10;
    return '$m min';
  }
  return h < 10 ? '${h.toStringAsFixed(h < 3 ? 1 : 0)} h' : '${h.round()} h';
}

/// Kasten & Young (1989) airmass — well-behaved down to the horizon, unlike
/// the bare secant (which blows up exactly where low-dec targets live; the
/// Texas NGC 6188 campaign at 11° was airmass ≈ 5).
double kastenYoungAirmass(double altitudeDeg) {
  final alt = altitudeDeg.clamp(0.0, 90.0);
  final zenith = 90.0 - alt;
  final x = 1.0 /
      (math.cos(zenith * math.pi / 180.0) +
          0.50572 * math.pow(alt + 6.07995, -1.6364));
  // The fit dips a hair below 1 at zenith — an airmass under one atmosphere
  // is non-physical and would make extinction negative.
  return math.max(1.0, x);
}

/// Krisciunas & Schaefer (1991) scattered-moonlight brightening: how many
/// magnitudes BRIGHTER the sky at the target is with the given moon, over the
/// moonless [darkSkyMag]. 0 when the moon is below the horizon.
///
/// [moonIlluminatedFraction] 0..1; [separationDeg] = angular distance from
/// the target to the moon.
double moonBrighteningMag({
  required double darkSkyMag,
  required double moonIlluminatedFraction,
  required double moonAltitudeDeg,
  required double separationDeg,
  double extinctionMagPerAirmass = 0.2,
}) {
  if (moonAltitudeDeg <= 0) return 0;
  final f = moonIlluminatedFraction.clamp(0.0, 1.0);
  // Illuminated fraction → phase angle α (deg): f = (1 + cos α) / 2.
  final alphaDeg = math.acos((2 * f - 1).clamp(-1.0, 1.0)) * 180.0 / math.pi;

  // K&S eq. 20 — the moon's illuminance (their I*), log scale.
  final iStar = math.pow(
      10.0,
      -0.4 *
          (3.84 +
              0.026 * alphaDeg.abs() +
              4.0e-9 * math.pow(alphaDeg, 4)));

  // K&S eq. 21 — the scattering function of separation ρ (Rayleigh + Mie).
  final rho = separationDeg.clamp(0.0, 180.0);
  final cosRho = math.cos(rho * math.pi / 180.0);
  final fRho = math.pow(10.0, 5.36) * (1.06 + cosRho * cosRho) +
      math.pow(10.0, 6.15 - rho / 40.0);

  // K&S eq. 15 — moon sky brightness in nanoLamberts: scattered moonlight
  // extincted along the moon's path, integrated over the target's path.
  final xMoon = kastenYoungAirmass(moonAltitudeDeg);
  // The target term uses the SKY position being observed; callers fold the
  // target's own altitude in via the airmass they pass to the budget — here
  // we evaluate at the moon-lit sky patch, approximated by the target patch
  // being at a generic mid-sky airmass of 1.2 when not otherwise known.
  const xTarget = 1.2;
  final bMoonNl = fRho *
      iStar *
      math.pow(10.0, -0.4 * extinctionMagPerAirmass * xMoon) *
      (1 - math.pow(10.0, -0.4 * extinctionMagPerAirmass * xTarget));

  if (bMoonNl <= 0) return 0;
  // nanoLamberts ↔ V mag/arcsec² (K&S eq. 1): B = 34.08 · e^(20.7233 − 0.92104·V)
  final moonSkyMag = (20.7233 - math.log(bMoonNl / 34.08)) / 0.92104;
  // Combine with the dark sky in FLUX space and report the brightening.
  final combined = combineSkyMags(darkSkyMag, moonSkyMag);
  return (darkSkyMag - combined).clamp(0.0, double.infinity);
}

/// Flux-space sum of two sky brightnesses (mag/arcsec²) — you cannot add
/// magnitudes; you add the light.
double combineSkyMags(double a, double b) =>
    -2.5 * math.log(math.pow(10, -0.4 * a) + math.pow(10, -0.4 * b)) / math.ln10;

/// THEOREM: the integration [snr] needs, in seconds, when the target delivers
/// [targetRate] and the sky [skyRate] (same units, e.g. e⁻/s per pixel or per
/// aperture): T = SNR² · (S_t + S_sky) / S_t². The read-noise and dark terms
/// are dropped (the Glover floor makes subs sky-limited). Pinned against
/// STScI's Pandeia engine in the #1140 parity test.
double requiredIntegrationSeconds({
  required double snr,
  required double targetRate,
  required double skyRate,
}) =>
    snr * snr * (targetRate + skyRate) / (targetRate * targetRate);

/// The budget. [input] carries the rig + filter + the MOONLESS sky brightness
/// (the same [OptimalSubInput] the Glover advisor builds — one flux chain);
/// [surfaceBrightnessMagArcsec2] is the target's catalog (average) SB;
/// [peakAltitudeDeg] the best altitude the target reaches tonight.
///
/// Moonlight (optional): pass the moon's state to fold Krisciunas & Schaefer
/// brightening into the sky term — omit for the target's intrinsic
/// (dark-night) budget.
IntegrationBudget computeIntegrationBudget({
  required OptimalSubInput input,
  required double surfaceBrightnessMagArcsec2,
  required double peakAltitudeDeg,
  double? moonIlluminatedFraction,
  double? moonAltitudeDeg,
  double? moonSeparationDeg,
  BudgetTuning tuning = BudgetTuning.v1,
}) {
  final airmass = kastenYoungAirmass(peakAltitudeDeg);

  final moonMag = (moonIlluminatedFraction != null &&
          moonAltitudeDeg != null &&
          moonSeparationDeg != null)
      ? moonBrighteningMag(
          darkSkyMag: input.skyMagPerArcsec2,
          moonIlluminatedFraction: moonIlluminatedFraction,
          moonAltitudeDeg: moonAltitudeDeg,
          separationDeg: moonSeparationDeg,
          extinctionMagPerAirmass: tuning.extinctionMagPerAirmass,
        )
      : 0.0;

  // Sky flux at tonight's (possibly moonlit) brightness, through this filter.
  final skyInput = _withSkyMag(input, input.skyMagPerArcsec2 - moonMag);
  final sSky = skyFluxEPerSecPerPx(skyInput);

  // Target flux: the same chain with the tier's surface brightness DIMMED by
  // atmospheric extinction at the target's airmass (the sky background is
  // generated along the path and is not extincted the same way — the Texas
  // blue-channel measurement is the empirical record of the difference).
  final extinctionMag = tuning.extinctionMagPerAirmass * (airmass - 1.0);
  IntegrationTier tier(String label, double offsetMag, double snrGoal) {
    final sb = surfaceBrightnessMagArcsec2 + offsetMag + extinctionMag;
    final sTarget = skyFluxEPerSecPerPx(_withSkyMag(input, sb));
    final tSec = requiredIntegrationSeconds(
        snr: snrGoal, targetRate: sTarget, skyRate: sSky);
    final hours = tSec / 3600.0;
    return IntegrationTier(
      label: label,
      hours: hours,
      practical: hours <= tuning.impracticalHours,
    );
  }

  return IntegrationBudget(
    core: tier('core', 0, tuning.snrCore),
    full: tier('full', tuning.fullTierOffsetMag, tuning.snrFull),
    faint: tier('faint', tuning.faintTierOffsetMag, tuning.snrFaint),
    airmass: airmass,
    moonBrighteningMag: moonMag,
    surfaceBrightnessMagArcsec2: surfaceBrightnessMagArcsec2,
    darkSkyMagArcsec2: input.skyMagPerArcsec2,
    filterBandwidthNm: input.filterBandwidthNm,
    skyFluxEPerSecPx: sSky,
    coreTargetFluxEPerSecPx: skyFluxEPerSecPerPx(
        _withSkyMag(input, surfaceBrightnessMagArcsec2 + extinctionMag)),
    tuning: tuning,
    skyCalibrationK: input.skyCalibrationK,
  );
}

/// #1140 P5 — where a target stands against its FULL-structure tier, given
/// what the library has banked and how many dark hours are left tonight.
enum TierStanding {
  /// Banked hours meet the tier: more subs buy little — point the night at
  /// something new.
  complete,

  /// The remaining need fits inside tonight's window.
  finishesTonight,

  /// Tonight helps but another night is needed.
  needsMoreNights,
}

class TierProgress {
  final TierStanding standing;

  /// Hours still needed for the tier (0 when complete).
  final double hoursToGo;
  const TierProgress(this.standing, this.hoursToGo);

  /// The one-line row text. (Nothing banked yet is [tierProgress] returning
  /// null — the plain "needs ~N h" line already says it.)
  String get line => switch (standing) {
        TierStanding.complete =>
          'Full-structure tier complete — diminishing returns; consider a new target.',
        TierStanding.finishesTonight =>
          '${_fmt(hoursToGo)} to go — finishes tonight.',
        TierStanding.needsMoreNights =>
          '${_fmt(hoursToGo)} to go — needs another night.',
      };

  static String _fmt(double h) => formatBudgetHours(h);
}

/// Pure — unit-tested. [bankedHours] null = nothing captured yet (returns
/// null: no standing to report). [remainingTonightHours] is the dark window
/// still ahead for this target tonight.
TierProgress? tierProgress({
  required double? bankedHours,
  required double neededHours,
  required double remainingTonightHours,
}) {
  if (bankedHours == null || neededHours <= 0) return null;
  final toGo = neededHours - bankedHours;
  if (toGo <= 0) return const TierProgress(TierStanding.complete, 0);
  return TierProgress(
    toGo <= remainingTonightHours
        ? TierStanding.finishesTonight
        : TierStanding.needsMoreNights,
    toGo,
  );
}

OptimalSubInput _withSkyMag(OptimalSubInput i, double mag) => OptimalSubInput(
      readNoiseE: i.readNoiseE,
      fullWellE: i.fullWellE,
      electronsPerAdu: i.electronsPerAdu,
      pixelSizeUm: i.pixelSizeUm,
      apertureMm: i.apertureMm,
      focalLengthMm: i.focalLengthMm,
      reducerFactor: i.reducerFactor,
      quantumEfficiency: i.quantumEfficiency,
      skyMagPerArcsec2: mag,
      filterBandwidthNm: i.filterBandwidthNm,
      noiseTolerancePct: i.noiseTolerancePct,
      saturationHeadroomFraction: i.saturationHeadroomFraction,
      skyCalibrationK: i.skyCalibrationK,
    );
