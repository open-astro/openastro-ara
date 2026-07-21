import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/integration_budget.dart';
import 'package:openastroara/util/optimal_sub.dart';

// The RedCat 91 + 2600MM rig the design doc's dry runs validated against.
OptimalSubInput rig({double skyMag = 21.9, double bandwidthNm = 300}) =>
    OptimalSubInput(
      readNoiseE: 1.5,
      fullWellE: 50000,
      pixelSizeUm: 3.76,
      apertureMm: 91,
      focalLengthMm: 447,
      quantumEfficiency: 0.8,
      skyMagPerArcsec2: skyMag,
      filterBandwidthNm: bandwidthNm,
    );

void main() {
  group('kastenYoungAirmass', () {
    test('zenith is 1, Texas-low is ~5, horizon stays finite', () {
      expect(kastenYoungAirmass(90), closeTo(1.0, 0.01));
      expect(kastenYoungAirmass(11), inInclusiveRange(4.5, 5.5));
      expect(kastenYoungAirmass(0), inInclusiveRange(30, 45));
    });
  });

  group('combineSkyMags', () {
    test('two equal skies are 0.753 mag brighter than one', () {
      expect(combineSkyMags(21.9, 21.9), closeTo(21.9 - 0.7526, 0.001));
    });
    test('a vastly fainter component changes nothing', () {
      expect(combineSkyMags(18, 30), closeTo(18, 0.001));
    });
  });

  group('moonBrighteningMag (Krisciunas & Schaefer)', () {
    test('moon below the horizon adds nothing', () {
      expect(
        moonBrighteningMag(
          darkSkyMag: 21.9,
          moonIlluminatedFraction: 1,
          moonAltitudeDeg: -5,
          separationDeg: 90,
        ),
        0,
      );
    });
    test('full moon at 45° alt, 90° away brightens a dark sky by 2–4.5 mag',
        () {
      final dMag = moonBrighteningMag(
        darkSkyMag: 21.9,
        moonIlluminatedFraction: 1,
        moonAltitudeDeg: 45,
        separationDeg: 90,
      );
      expect(dMag, inInclusiveRange(2.0, 4.5));
    });
    test('a thin crescent brightens far less than full', () {
      final crescent = moonBrighteningMag(
        darkSkyMag: 21.9,
        moonIlluminatedFraction: 0.15,
        moonAltitudeDeg: 45,
        separationDeg: 90,
      );
      final full = moonBrighteningMag(
        darkSkyMag: 21.9,
        moonIlluminatedFraction: 1,
        moonAltitudeDeg: 45,
        separationDeg: 90,
      );
      expect(crescent, lessThan(full / 3));
    });
  });

  group('computeIntegrationBudget', () {
    test('THEOREM golden case: S_target == S_sky at zenith → T = 2·SNR²/P',
        () {
      // Target SB set equal to the sky mag, peak at zenith (no extinction):
      // S_t = S_sky = P, so T_core = 5² · 2P / P² = 50 / P seconds.
      final input = rig();
      final p = skyFluxEPerSecPerPx(input);
      final b = computeIntegrationBudget(
        input: input,
        surfaceBrightnessMagArcsec2: 21.9,
        peakAltitudeDeg: 90,
      );
      expect(b.core.hours, closeTo(50 / p / 3600.0, 1e-9));
      expect(b.airmass, closeTo(1.0, 0.01));
      expect(b.moonBrighteningMag, 0);
    });

    test('tiers order core < full < faint, and a dim faint tier goes '
        'impractical', () {
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 24,
        peakAltitudeDeg: 60,
      );
      expect(b.core.hours, lessThan(b.full.hours));
      expect(b.full.hours, lessThan(b.faint.hours));
      expect(b.faint.practical, isFalse);
    });

    test('a bright core is fast', () {
      // M42-core-like SB through broadband from a dark sky: minutes-to-few-h.
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 17,
        peakAltitudeDeg: 60,
      );
      expect(b.core.practical, isTrue);
      expect(b.core.hours, lessThan(1.0));
    });

    test('low altitude costs hours (the 11° Texas lesson)', () {
      final high = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 21,
        peakAltitudeDeg: 80,
      );
      final low = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 21,
        peakAltitudeDeg: 11,
      );
      expect(low.airmass, greaterThan(4.5));
      // ~0.8 mag extinction → roughly 2–3× the hours at same tier.
      expect(low.core.hours / high.core.hours, greaterThan(1.8));
    });

    test('the moon adds hours; omitting moon params gives the intrinsic '
        'budget', () {
      final dark = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 21,
        peakAltitudeDeg: 60,
      );
      final moonlit = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 21,
        peakAltitudeDeg: 60,
        moonIlluminatedFraction: 1,
        moonAltitudeDeg: 45,
        moonSeparationDeg: 60,
      );
      expect(moonlit.moonBrighteningMag, greaterThan(1));
      expect(moonlit.core.hours, greaterThan(dark.core.hours * 2));
    });

    test('display line reads like the spec', () {
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 24,
        peakAltitudeDeg: 60,
      );
      expect(b.display, contains('core'));
      expect(b.display, contains('full'));
      expect(b.display, contains('faint impractical'));
    });
  });
}
