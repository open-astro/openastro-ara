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

  group('#1140 golden cases (INTEGRATION_BUDGET P1: M42 core fast / NAN mid / IFN absurd)', () {
    // Dark-sky (SQM 21.9) broadband through the RedCat 91 + 2600MM fixture.
    test('M42 core (SB 17) is minutes, its faint tier is still practical', () {
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 17,
        peakAltitudeDeg: 60,
      );
      expect(b.core.hours, lessThan(0.25), reason: 'the bright body is a handful of minutes');
      expect(b.full.practical, isTrue);
      expect(b.faint.practical, isTrue, reason: 'M42\'s halo is a one-night object from a dark site');
      expect(b.core.hours < b.full.hours && b.full.hours < b.faint.hours, isTrue);
    });

    test('North America Nebula (SB ~22) from a Bortle-6 yard is a campaign: minutes for the core, ~10 h full, faint impractical', () {
      final b = computeIntegrationBudget(
        input: rig(skyMag: 18.5),
        surfaceBrightnessMagArcsec2: 22,
        peakAltitudeDeg: 60,
      );
      expect(b.core.practical, isTrue);
      expect(b.core.hours, inInclusiveRange(0.1, 1), reason: 'the bright body is a short session');
      expect(b.full.practical, isTrue, reason: 'the full structure is a multi-night campaign');
      expect(b.full.hours, inInclusiveRange(5, 20));
      expect(b.faint.practical, isFalse, reason: 'SB +4 from a suburban broadband sky is beyond 60 h');
      expect(b.display, contains('faint impractical'));
    });

    test('IFN (SB 27) is absurd even from a dark site: every tier impractical', () {
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 27,
        peakAltitudeDeg: 80,
      );
      expect(b.core.practical, isFalse);
      expect(b.full.practical, isFalse);
      expect(b.faint.practical, isFalse);
      expect(b.display, 'core impractical · full impractical · faint impractical');
    });

    test('a brighter sky lengthens every tier (the narrowband-tolerates-cities corollary)', () {
      final dark = computeIntegrationBudget(input: rig(), surfaceBrightnessMagArcsec2: 21, peakAltitudeDeg: 60);
      final city = computeIntegrationBudget(input: rig(skyMag: 18.5), surfaceBrightnessMagArcsec2: 21, peakAltitudeDeg: 60);
      expect(city.core.hours, greaterThan(dark.core.hours * 5));
      // Narrowband (7 nm) under that city sky: the sky term shrinks with the passband.
      final cityNb = computeIntegrationBudget(input: rig(skyMag: 18.5, bandwidthNm: 7), surfaceBrightnessMagArcsec2: 21, peakAltitudeDeg: 60);
      expect(cityNb.skyFluxEPerSecPx, lessThan(city.skyFluxEPerSecPx / 10));
    });
  });

  group('#1140 audit lines', () {
    test('expose the inputs the hours rest on, moon included', () {
      final b = computeIntegrationBudget(
        input: rig(),
        surfaceBrightnessMagArcsec2: 21,
        peakAltitudeDeg: 45,
        moonIlluminatedFraction: 0.9,
        moonAltitudeDeg: 40,
        moonSeparationDeg: 40,
      );
      final lines = Map.fromEntries(b.auditLines.map((l) => MapEntry(l.$1, l.$2)));
      expect(lines['Surface brightness'], '21.0 mag/arcsec²');
      expect(lines['Filter passband'], '300 nm');
      expect(lines['Airmass at peak'], b.airmass.toStringAsFixed(2));
      expect(lines['Sky tonight'], contains('moon +'));
      expect(lines['SNR goals'], 'core 5 · full 5 (SB +2) · faint 3 (SB +4)');
      expect(lines['Impractical beyond'], '60 h');
      expect(b.coreTargetFluxEPerSecPx, lessThan(b.skyFluxEPerSecPx), reason: 'SB 21 fainter than the moonlit sky');
    });

    test('a budget built without inputs says so instead of printing NaN', () {
      const b = IntegrationBudget(
        core: IntegrationTier(label: 'core', hours: 1, practical: true),
        full: IntegrationTier(label: 'full', hours: 2, practical: true),
        faint: IntegrationTier(label: 'faint', hours: 3, practical: true),
        airmass: 1,
        moonBrighteningMag: 0,
      );
      expect(b.auditLines, [('Budget inputs', 'not recorded')]);
    });
  });

  group('#1140 P5 tierProgress', () {
    test('nothing banked → no standing; banked at or past the tier → complete', () {
      expect(tierProgress(bankedHours: null, neededHours: 9, remainingTonightHours: 4), isNull);
      final done = tierProgress(bankedHours: 9.2, neededHours: 9, remainingTonightHours: 4)!;
      expect(done.standing, TierStanding.complete);
      expect(done.hoursToGo, 0);
      expect(done.line, contains('diminishing returns'));
    });

    test('the rest fits tonight vs needs another night', () {
      final tonight = tierProgress(bankedHours: 6.2, neededHours: 9, remainingTonightHours: 4)!;
      expect(tonight.standing, TierStanding.finishesTonight);
      expect(tonight.hoursToGo, closeTo(2.8, 1e-9));
      expect(tonight.line, '2.8 h to go — finishes tonight.');
      final later = tierProgress(bankedHours: 2, neededHours: 9, remainingTonightHours: 4)!;
      expect(later.standing, TierStanding.needsMoreNights);
      expect(later.line, '7 h to go — needs another night.');
      expect(tierProgress(bankedHours: 8.5, neededHours: 9, remainingTonightHours: 1)!.line,
          '30 min to go — finishes tonight.');
    });

    test('an unusable need is no standing', () {
      expect(tierProgress(bankedHours: 3, neededHours: 0, remainingTonightHours: 4), isNull);
    });
  });
}
