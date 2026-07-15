import 'dart:math' as math;

import '../state/settings/filter_set_state.dart';

/// NEXTGEN §2/§3 — the Optimal-Sub calculator, ported from the daemon's
/// `OptimalSubCalculator` under the 2026-07-15 PORT_DECISIONS call (planning
/// compute lives in the client): the read-noise-limited sub-exposure floor
/// (Dr. Robin Glover's criterion, used with his permission 2026-06-30)
/// intersected with the sky-background saturation ceiling. Pure static math.
///
/// Model notes carried over verbatim from the C# original: single gray band
/// referenced to the V-band zero point; [defaultBroadbandBandwidthNm] is an
/// EFFECTIVE width (100 nm, deliberately narrower than a luminance filter's
/// physical ~300 nm); the only bounds are the read-noise floor and the
/// saturation ceiling (star/core saturation and satellite trails are later
/// refinements per NEXTGEN §3).
class OptimalSubInput {
  final double readNoiseE;
  final double fullWellE;

  /// 0 = unknown (the ADC clip on the effective well is skipped).
  final double electronsPerAdu;
  final double pixelSizeUm;
  final double apertureMm;
  final double focalLengthMm;
  final double reducerFactor;
  final double quantumEfficiency;
  final double skyMagPerArcsec2;
  final double filterBandwidthNm;
  final double noiseTolerancePct;
  final double saturationHeadroomFraction;

  const OptimalSubInput({
    required this.readNoiseE,
    required this.fullWellE,
    this.electronsPerAdu = 0,
    required this.pixelSizeUm,
    required this.apertureMm,
    required this.focalLengthMm,
    this.reducerFactor = 1.0,
    required this.quantumEfficiency,
    required this.skyMagPerArcsec2,
    required this.filterBandwidthNm,
    this.noiseTolerancePct = 5.0,
    this.saturationHeadroomFraction = 0.8,
  });
}

enum OptimalSubBound { readNoiseFloor, saturationCeiling, starFloor }

class OptimalSubResult {
  final double skyFluxEPerSecPerPx;
  final double floorSec;
  final double ceilingSec;
  final bool viable;
  final OptimalSubBound limitingBound;
  final double recommendedSec;

  // §3.1 star-detectability augmentation (null until [augmentWithStarFloor]).
  final double? starFloorSec;
  final double? starsDetectedPerSub;
  final double? starsRegistrationPerSub;
  final String? starReason;

  const OptimalSubResult({
    required this.skyFluxEPerSecPerPx,
    required this.floorSec,
    required this.ceilingSec,
    required this.viable,
    required this.limitingBound,
    required this.recommendedSec,
    this.starFloorSec,
    this.starsDetectedPerSub,
    this.starsRegistrationPerSub,
    this.starReason,
  });
}

/// Vega-referenced photon flux of a mag-0 star at V in photons/s/cm²/nm.
const double photonFluxMag0PerCm2PerNm = 1.0e4;

/// Tier-0 generic-CMOS defaults (profile unset).
const double defaultQuantumEfficiency = 0.8;
const double defaultReadNoiseE = 3.5;
const double defaultFullWellE = 50000;

/// Effective broadband (L / OSC) passband in nm — see the class remarks.
const double defaultBroadbandBandwidthNm = 100;

/// 16-bit ADC clip assumption for the effective well (see the C# original's
/// rationale — astro cameras overwhelmingly deliver padded 16-bit output).
const double _adcMaxAdu = 65535;

/// Approximate zenith sky brightness (mag/arcsec²) for a Bortle class — the
/// ONE sky model scoring and exposure planning share.
double skyMagFromBortle(int bortleClass) =>
    22.0 - (bortleClass.clamp(1, 9) - 1) * 0.5;

/// A filter kind's default EFFECTIVE passband (nm) when the profile entry
/// leaves bandwidth at 0. (Mirrors FilterKind.defaultBandwidthNm — kept as
/// the calculator-side seam like the daemon had.)
double defaultBandwidthNm(FilterKind kind) => kind.defaultBandwidthNm;

/// Plate scale in arcsec/pixel — shared with the star-detectability solver so
/// a formula change propagates (one geometry model, like the one sky model).
double pixelScaleArcsec(OptimalSubInput input) =>
    206.265 * input.pixelSizeUm / (input.focalLengthMm * input.reducerFactor);

/// Clear-aperture area in cm² (mm diameter → cm radius).
double apertureAreaCm2(OptimalSubInput input) =>
    math.pi * math.pow(input.apertureMm / 20.0, 2).toDouble();

/// `P`: the modelled sky-background flux in e⁻/s/pixel.
double skyFluxEPerSecPerPx(OptimalSubInput input) {
  _validateFluxInputs(input);
  final scale = pixelScaleArcsec(input);
  return photonFluxMag0PerCm2PerNm *
      math.pow(10.0, -0.4 * input.skyMagPerArcsec2) *
      input.filterBandwidthNm *
      apertureAreaCm2(input) *
      scale *
      scale *
      input.quantumEfficiency;
}

/// The full sub-exposure window. Floor: `C(ε)·R²/P` with
/// `C = 1/((1+ε)²−1)` (5% → C ≈ 9.756). Ceiling:
/// `headroom · effectiveWell / P` with the well clipped by the 16-bit ADC
/// when electrons/ADU is known.
OptimalSubResult computeOptimalSub(OptimalSubInput input) {
  _validateComputeInputs(input);
  final p = skyFluxEPerSecPerPx(input);

  final epsilon = input.noiseTolerancePct / 100.0;
  final c = 1.0 / (math.pow(1.0 + epsilon, 2) - 1.0);
  final floorSec = c * input.readNoiseE * input.readNoiseE / p;

  final effectiveWell = input.electronsPerAdu > 0
      ? math.min(input.fullWellE, input.electronsPerAdu * _adcMaxAdu)
      : input.fullWellE;
  final ceilingSec = input.saturationHeadroomFraction * effectiveWell / p;

  final viable = floorSec <= ceilingSec;
  return OptimalSubResult(
    skyFluxEPerSecPerPx: p,
    floorSec: floorSec,
    ceilingSec: ceilingSec,
    viable: viable,
    limitingBound: viable
        ? OptimalSubBound.readNoiseFloor
        : OptimalSubBound.saturationCeiling,
    recommendedSec: math.min(floorSec, ceilingSec),
  );
}

void _validateFluxInputs(OptimalSubInput input) {
  _requirePositive(input.pixelSizeUm, 'pixelSizeUm');
  _requirePositive(input.apertureMm, 'apertureMm');
  _requirePositive(input.focalLengthMm, 'focalLengthMm');
  _requirePositive(input.reducerFactor, 'reducerFactor');
  _requirePositive(input.quantumEfficiency, 'quantumEfficiency');
  if (input.quantumEfficiency > 1.0) {
    throw ArgumentError(
        'Quantum efficiency must be in (0, 1] — above 1 is non-physical.');
  }
  _requirePositive(input.filterBandwidthNm, 'filterBandwidthNm');
  if (!input.skyMagPerArcsec2.isFinite) {
    throw ArgumentError('Sky brightness (mag/arcsec²) must be finite.');
  }
}

void _validateComputeInputs(OptimalSubInput input) {
  _requirePositive(input.readNoiseE, 'readNoiseE');
  _requirePositive(input.fullWellE, 'fullWellE');
  _requirePositive(input.noiseTolerancePct, 'noiseTolerancePct');
  if (input.noiseTolerancePct > 100.0) {
    throw ArgumentError('Noise tolerance must be in (0, 100] %.');
  }
  if (input.electronsPerAdu < 0 || !input.electronsPerAdu.isFinite) {
    throw ArgumentError('Electrons/ADU must be ≥ 0 (0 = unknown).');
  }
  if (!(input.saturationHeadroomFraction > 0 &&
      input.saturationHeadroomFraction <= 1.0)) {
    throw ArgumentError('Saturation headroom fraction must be in (0, 1].');
  }
}

void _requirePositive(double value, String name) {
  if (!(value.isFinite && value > 0)) {
    throw ArgumentError('$name must be a positive, finite number.');
  }
}
