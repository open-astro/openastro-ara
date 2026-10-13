import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/optimal_sub_calculator.dart';

const _rig = CalculatorInputs(
  readNoiseE: 1.5,
  fullWellE: 50000,
  electronsPerAdu: 0.8,
  pixelSizeUm: 3.76,
  apertureMm: 91,
  focalLengthMm: 447,
  quantumEfficiency: 0.8,
  skyMagPerArcsec2: 20.5,
  filterBandwidthNm: 100,
);

void main() {
  test('#1140 NEXTGEN §5 a complete rig yields the Glover window, no budget without a target', () {
    final r = calculate(_rig);
    expect(r.error, isNull);
    expect(r.sub!.viable, isTrue);
    expect(r.sub!.recommendedSec, greaterThan(0));
    expect(r.pixelScaleArcsec, closeTo(206.265 * 3.76 / 447, 1e-6));
    expect(r.budget, isNull);
  });

  test('#1140 a target surface brightness adds the budget tiers; k scales the fluxes', () {
    final r = calculate(
      _rig.copyWith(surfaceBrightnessMagArcsec2: 22, peakAltitudeDeg: 60),
    );
    expect(r.budget, isNotNull);
    expect(r.budget!.full.hours, greaterThan(0));
    final k = calculate(
      _rig.copyWith(surfaceBrightnessMagArcsec2: 22, skyCalibrationK: 0.5),
    );
    expect(
      k.sub!.skyFluxEPerSecPerPx,
      closeTo(r.sub!.skyFluxEPerSecPerPx * 0.5, 1e-9),
    );
    expect(k.budget!.full.hours, closeTo(r.budget!.full.hours * 2, 1e-6));
    expect(k.budget!.skyCalibrationK, 0.5);
    expect(
      calculate(
        _rig
            .copyWith(surfaceBrightnessMagArcsec2: 22)
            .copyWith(clearSurfaceBrightness: true),
      ).budget,
      isNull,
    );
  });

  test('#1140 an impossible input is a sentence naming it, never a throw', () {
    expect(
      calculate(_rig.copyWith(apertureMm: 0)).error,
      'Needs a positive aperture.',
    );
    expect(
      calculate(_rig.copyWith(quantumEfficiency: 1.4, skyMagPerArcsec2: 30))
          .error,
      'Needs a QE between 0 and 1, a sky brightness between 10 and 23 mag/arcsec².',
    );
    expect(
      calculate(_rig.copyWith(skyCalibrationK: 0)).error,
      contains('calibration k'),
    );
    // What computeOptimalSub would throw on: a negative e⁻/ADU, an infinite or NaN figure.
    expect(
      calculate(_rig.copyWith(electronsPerAdu: -0.8)).error,
      'Needs e⁻/ADU of 0 or more.',
    );
    expect(
      calculate(_rig.copyWith(electronsPerAdu: double.infinity)).error,
      contains('e⁻/ADU'),
    );
    expect(
      calculate(_rig.copyWith(fullWellE: double.infinity)).error,
      'Needs a positive full well.',
    );
    expect(
      calculate(_rig.copyWith(readNoiseE: double.nan)).error,
      'Needs a positive read noise.',
    );
    expect(
      calculate(_rig.copyWith(peakAltitudeDeg: 0)).error,
      contains('peak altitude'),
    );
    expect(
      calculate(_rig.copyWith(surfaceBrightnessMagArcsec2: -3)).error,
      contains('surface brightness'),
    );
    for (final v in [-1.0, double.infinity, double.nan, 1e999]) {
      expect(() => calculate(_rig.copyWith(pixelSizeUm: v)), returnsNormally);
    }
  });

  test('#1140 seconds format like the advisor', () {
    expect(formatSubSeconds(45), '45 s');
    expect(formatSubSeconds(150), '2.5 min');
    expect(formatSubSeconds(600), '10 min');
  });

  test('#1140 an overflowing sky model is a sentence, not Infinity', () {
    final r = calculate(_rig.copyWith(apertureMm: 1e200, pixelSizeUm: 1e200));
    expect(r.sub, isNull);
    expect(r.error, contains('overflow'));
    expect(
      calculate(_rig.copyWith(apertureMm: 1e-200)).error,
      contains('overflow'),
      reason: 'a vanishing flux is Infinity seconds',
    );
  });

  test('#1140 a row\'s number keeps its integer zeros', () {
    expect(formatCalculatorNumber(50000, 0), '50000');
    expect(formatCalculatorNumber(60, 0), '60');
    expect(formatCalculatorNumber(20.5, 2), '20.5');
    expect(formatCalculatorNumber(1.0, 3), '1');
    expect(formatCalculatorNumber(0.8, 3), '0.8');
  });
}
