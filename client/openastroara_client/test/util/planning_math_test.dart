import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart'
    show TonightFilterAdvice;
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/util/filter_advice.dart';
import 'package:openastroara/util/optimal_sub.dart';
import 'package:openastroara/util/star_model.dart' as stars;

/// Parity tests for the planning-math ports (PORT_DECISIONS 2026-07-15):
/// values pinned against the daemon's C# implementations so a drift in either
/// port shows up as a red test, not a silently different recommendation.
void main() {
  // The worked RedCat-51 / IMX571-ish example: aperture 51 mm, 250 mm f/4.9,
  // 3.76 µm pixels, RN 1.5 e⁻, FW 51k e⁻, QE 0.8, Bortle 6 sky (19.5
  // mag/arcsec²), broadband 100 nm.
  const input = OptimalSubInput(
    readNoiseE: 1.5,
    fullWellE: 51000,
    electronsPerAdu: 0.78,
    pixelSizeUm: 3.76,
    apertureMm: 51,
    focalLengthMm: 250,
    reducerFactor: 1.0,
    quantumEfficiency: 0.8,
    skyMagPerArcsec2: 19.5,
    filterBandwidthNm: 100,
  );

  group('optimal sub', () {
    test('sky flux P matches the closed-form model', () {
      // P = 1e4 · 10^(−0.4·19.5) · 100 · π·(51/20)² · (206.265·3.76/250)² · 0.8
      final scale = 206.265 * 3.76 / 250.0;
      final expected = 1.0e4 *
          math.pow(10.0, -0.4 * 19.5) *
          100 *
          math.pi *
          math.pow(51 / 20.0, 2) *
          scale *
          scale *
          0.8;
      expect(skyFluxEPerSecPerPx(input), closeTo(expected, expected * 1e-12));
    });

    test('floor uses C(5%) ≈ 9.756 and ceiling the ADC-clipped well', () {
      final r = computeOptimalSub(input);
      final p = skyFluxEPerSecPerPx(input);
      final c = 1.0 / (math.pow(1.05, 2) - 1.0); // ≈ 9.7561
      expect(c, closeTo(9.7561, 0.0001));
      expect(r.floorSec, closeTo(c * 1.5 * 1.5 / p, 1e-9));
      // effective well = min(51000, 0.78·65535 = 51117.3) = 51000 → ·0.8
      expect(r.ceilingSec, closeTo(0.8 * 51000 / p, 1e-9));
      expect(r.viable, isTrue);
      expect(r.limitingBound, OptimalSubBound.readNoiseFloor);
      expect(r.recommendedSec, r.floorSec);
    });

    test('ADC clip binds when electrons/ADU is small', () {
      const clipped = OptimalSubInput(
        readNoiseE: 1.5,
        fullWellE: 51000,
        electronsPerAdu: 0.25, // 0.25·65535 = 16383.75 < full well
        pixelSizeUm: 3.76,
        apertureMm: 51,
        focalLengthMm: 250,
        quantumEfficiency: 0.8,
        skyMagPerArcsec2: 19.5,
        filterBandwidthNm: 100,
      );
      final r = computeOptimalSub(clipped);
      final p = skyFluxEPerSecPerPx(clipped);
      expect(r.ceilingSec, closeTo(0.8 * 0.25 * 65535 / p, 1e-9));
    });

    test('Bortle sky-mag table matches the daemon (22.0 − 0.5/class)', () {
      expect(skyMagFromBortle(1), 22.0);
      expect(skyMagFromBortle(6), 19.5);
      expect(skyMagFromBortle(9), 18.0);
      expect(skyMagFromBortle(0), 22.0); // clamped
      expect(skyMagFromBortle(12), 18.0); // clamped
    });

    test('narrowband floor scales linearly with bandwidth (P linearity)', () {
      const ha = OptimalSubInput(
        readNoiseE: 1.5,
        fullWellE: 51000,
        pixelSizeUm: 3.76,
        apertureMm: 51,
        focalLengthMm: 250,
        quantumEfficiency: 0.8,
        skyMagPerArcsec2: 19.5,
        filterBandwidthNm: 7,
      );
      final broadband = computeOptimalSub(input);
      final narrow = computeOptimalSub(ha);
      expect(narrow.floorSec / broadband.floorSec, closeTo(100 / 7, 1e-6));
    });

    test('non-physical inputs are rejected', () {
      expect(
          () => computeOptimalSub(const OptimalSubInput(
                readNoiseE: 1.5,
                fullWellE: 51000,
                pixelSizeUm: 3.76,
                apertureMm: 51,
                focalLengthMm: 250,
                quantumEfficiency: 1.2, // > 1
                skyMagPerArcsec2: 19.5,
                filterBandwidthNm: 100,
              )),
          throwsArgumentError);
    });
  });

  group('star model', () {
    test('galactic latitude: NGP ≈ +90°, plane targets near 0°', () {
      expect(stars.galacticLatitudeDeg(192.85948, 27.12825), closeTo(90, 0.01));
      // Deneb region (NGC 7000) sits close to the plane.
      expect(stars.galacticLatitudeDeg(314.75, 44.33).abs(), lessThan(5));
    });

    test('m=9 anchors interpolate to the pinned HYG densities', () {
      expect(stars.cumulativeStarsPerDeg2(9.0, 90), closeTo(1.458785, 1e-6));
      expect(stars.cumulativeStarsPerDeg2(9.0, 0), closeTo(2.941788, 1e-6));
    });

    test('counts increase with limiting magnitude and toward the plane', () {
      final faint = stars.cumulativeStarsPerDeg2(12, 30);
      final bright = stars.cumulativeStarsPerDeg2(9, 30);
      expect(faint, greaterThan(bright));
      expect(stars.cumulativeStarsPerDeg2(10, 0),
          greaterThan(stars.cumulativeStarsPerDeg2(10, 90)));
    });

    test('limiting magnitude deepens with exposure', () {
      final m30 = stars.limitingMagnitude(input, 30, 2.5);
      final m300 = stars.limitingMagnitude(input, 300, 2.5);
      expect(m300, greaterThan(m30));
    });

    test('star floor: wide fast field is unconstrained, tiny field starves',
        () {
      // ~3.4°×2.3° field at 250 mm on this sensor — plenty of stars.
      final fovDeg2 = (6248 * 206.265 * 3.76 / 250 / 3600) *
          (4176 * 206.265 * 3.76 / 250 / 3600);
      final t = stars.starFloorSec(input, 2.5, 30, fovDeg2);
      expect(t, isNotNull);
      expect(t!, lessThan(60)); // far below any realistic sub length
      // A 0.001 deg² field CAN still get there, just needs long subs — the
      // floor moves up rather than starving.
      final narrow = stars.starFloorSec(input, 2.5, 90, 0.001);
      expect(narrow, isNotNull);
      expect(narrow!, greaterThan(t));
      // A 0.0001 deg² field at the pole can't reach 20 registration stars
      // even at the 3600 s search bound — genuinely starved.
      expect(stars.starFloorSec(input, 2.5, 90, 0.0001), isNull);
    });

    test('augment: healthy field keeps the read-noise floor as binding', () {
      final window = computeOptimalSub(input);
      final fovDeg2 = (6248 * 206.265 * 3.76 / 250 / 3600) *
          (4176 * 206.265 * 3.76 / 250 / 3600);
      final a = stars.augmentWithStarFloor(window, input, 2.5, 83.8, -5.4,
          fovDeg2);
      expect(a.limitingBound, OptimalSubBound.readNoiseFloor);
      expect(a.recommendedSec, closeTo(window.recommendedSec, 1e-9));
      expect(a.starsRegistrationPerSub, greaterThan(stars.minRegistrationStars));
      expect(a.starReason, contains('read noise remains the binding floor'));
    });
  });

  group('filter advice', () {
    const haFilter =
        PlanningFilter(name: 'Ha', kind: FilterKind.ha, bandwidthNm: 7);
    const lFilter =
        PlanningFilter(name: 'L', kind: FilterKind.l, bandwidthNm: 0);
    const duoFilter =
        PlanningFilter(name: 'L-eXtreme', kind: FilterKind.duo, bandwidthNm: 7);

    test('emission classification matches the daemon table', () {
      expect(classifyEmission('HII'), EmissionClass.emissionLine);
      expect(classifyEmission('PN'), EmissionClass.emissionLine);
      expect(classifyEmission('G'), EmissionClass.continuum);
      expect(classifyEmission('galaxy'), EmissionClass.continuum);
      expect(classifyEmission('Neb'), EmissionClass.mixed);
      expect(classifyEmission('nebula'), EmissionClass.mixed);
      expect(classifyEmission('quasar?'), EmissionClass.unknown);
      expect(classifyEmission(null), EmissionClass.unknown);
    });

    test('emission target + mono narrowband → narrowband with the ~14× ratio',
        () {
      final advice = adviseFilter(EmissionClass.emissionLine,
          const FilterSetSettings(filters: [haFilter, lFilter]), 6);
      expect(advice, isNotNull);
      expect(advice!.$1, TonightFilterAdvice.narrowband);
      expect(advice.$2, contains('~14×'));
    });

    test('continuum target → broadband even when narrowband exists', () {
      final advice = adviseFilter(EmissionClass.continuum,
          const FilterSetSettings(filters: [haFilter, lFilter]), 4);
      expect(advice!.$1, TonightFilterAdvice.broadband);
    });

    test('mixed under a bright sky tips to duoband with OSC kit', () {
      final advice = adviseFilter(EmissionClass.mixed,
          const FilterSetSettings(filters: [duoFilter]), 7);
      expect(advice!.$1, TonightFilterAdvice.duoband);
    });

    test('no honest advice: empty set or unknown emission', () {
      expect(
          adviseFilter(EmissionClass.emissionLine,
              const FilterSetSettings(filters: []), 6),
          isNull);
      expect(
          adviseFilter(EmissionClass.unknown,
              const FilterSetSettings(filters: [haFilter]), 6),
          isNull);
    });

    test('representative filter prefers Ha, then L, then OSC', () {
      const set = FilterSetSettings(filters: [lFilter, haFilter]);
      expect(representativeFilter(set, TonightFilterAdvice.narrowband)!.name,
          'Ha');
      expect(
          representativeFilter(set, TonightFilterAdvice.broadband)!.name, 'L');
      expect(representativeFilter(set, TonightFilterAdvice.duoband), isNull);
    });

    test('effective bandwidth falls back to the kind default', () {
      expect(effectiveBandwidthNm(lFilter), 100); // 0 → L default
      expect(effectiveBandwidthNm(haFilter), 7); // explicit
    });
  });
}
