import 'integration_budget.dart';
import 'optimal_sub.dart';

/// #1140 / NEXTGEN §5 — the standalone Optimal Sub calculator's model: every
/// input the Glover window and the Integration Budget rest on, editable, seeded
/// from the profile. Pure, so the dialog is a thin skin over [calculate].
class CalculatorInputs {
  final double readNoiseE;
  final double fullWellE;
  final double electronsPerAdu; // 0 = unknown
  final double pixelSizeUm;
  final double apertureMm;
  final double focalLengthMm;
  final double reducerFactor;
  final double quantumEfficiency;
  final double skyMagPerArcsec2;
  final double filterBandwidthNm;
  final double noiseTolerancePct;

  /// The rig's fitted sky-model factor for the chosen filter kind (1 = none).
  final double skyCalibrationK;

  /// Optional target for the budget tiers: catalog surface brightness
  /// (mag/arcsec²) and the altitude it peaks at tonight.
  final double? surfaceBrightnessMagArcsec2;
  final double peakAltitudeDeg;

  const CalculatorInputs({
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
    this.skyCalibrationK = 1.0,
    this.surfaceBrightnessMagArcsec2,
    this.peakAltitudeDeg = 60,
  });

  CalculatorInputs copyWith({
    double? readNoiseE,
    double? fullWellE,
    double? electronsPerAdu,
    double? pixelSizeUm,
    double? apertureMm,
    double? focalLengthMm,
    double? reducerFactor,
    double? quantumEfficiency,
    double? skyMagPerArcsec2,
    double? filterBandwidthNm,
    double? noiseTolerancePct,
    double? skyCalibrationK,
    double? surfaceBrightnessMagArcsec2,
    bool clearSurfaceBrightness = false,
    double? peakAltitudeDeg,
  }) => CalculatorInputs(
    readNoiseE: readNoiseE ?? this.readNoiseE,
    fullWellE: fullWellE ?? this.fullWellE,
    electronsPerAdu: electronsPerAdu ?? this.electronsPerAdu,
    pixelSizeUm: pixelSizeUm ?? this.pixelSizeUm,
    apertureMm: apertureMm ?? this.apertureMm,
    focalLengthMm: focalLengthMm ?? this.focalLengthMm,
    reducerFactor: reducerFactor ?? this.reducerFactor,
    quantumEfficiency: quantumEfficiency ?? this.quantumEfficiency,
    skyMagPerArcsec2: skyMagPerArcsec2 ?? this.skyMagPerArcsec2,
    filterBandwidthNm: filterBandwidthNm ?? this.filterBandwidthNm,
    noiseTolerancePct: noiseTolerancePct ?? this.noiseTolerancePct,
    skyCalibrationK: skyCalibrationK ?? this.skyCalibrationK,
    surfaceBrightnessMagArcsec2: clearSurfaceBrightness
        ? null
        : surfaceBrightnessMagArcsec2 ?? this.surfaceBrightnessMagArcsec2,
    peakAltitudeDeg: peakAltitudeDeg ?? this.peakAltitudeDeg,
  );

  OptimalSubInput toSubInput() => OptimalSubInput(
    readNoiseE: readNoiseE,
    fullWellE: fullWellE,
    electronsPerAdu: electronsPerAdu,
    pixelSizeUm: pixelSizeUm,
    apertureMm: apertureMm,
    focalLengthMm: focalLengthMm,
    reducerFactor: reducerFactor,
    quantumEfficiency: quantumEfficiency,
    skyMagPerArcsec2: skyMagPerArcsec2,
    filterBandwidthNm: filterBandwidthNm,
    noiseTolerancePct: noiseTolerancePct,
    skyCalibrationK: skyCalibrationK,
  );
}

/// What the calculator shows: the Glover window (or why it could not be
/// computed) and, with a target surface brightness, the budget tiers.
class CalculatorResult {
  final OptimalSubResult? sub;
  final IntegrationBudget? budget;
  final double? pixelScaleArcsec;
  final String? error;
  const CalculatorResult({
    this.sub,
    this.budget,
    this.pixelScaleArcsec,
    this.error,
  });
}

/// The one place the dialog computes. Invalid inputs become a sentence, not a
/// throw: the user is typing.
CalculatorResult calculate(CalculatorInputs inputs) {
  // Every figure computeOptimalSub would reject (it throws ArgumentError on a
  // non-positive, negative or non-finite input) is named here first, so a
  // typed "-0.8" or "1e999" is a sentence, never an error widget.
  bool pos(double v) => v.isFinite && v > 0;
  bool inRange(double v, double lo, double hi) =>
      v.isFinite && v >= lo && v <= hi;
  final problems = <String>[
    if (!pos(inputs.readNoiseE)) 'a positive read noise',
    if (!pos(inputs.fullWellE)) 'a positive full well',
    if (!(inputs.electronsPerAdu.isFinite && inputs.electronsPerAdu >= 0))
      'e⁻/ADU of 0 or more',
    if (!pos(inputs.pixelSizeUm)) 'a positive pixel size',
    if (!pos(inputs.apertureMm)) 'a positive aperture',
    if (!pos(inputs.focalLengthMm)) 'a positive focal length',
    if (!pos(inputs.reducerFactor)) 'a positive reducer factor',
    if (!(inputs.quantumEfficiency.isFinite &&
        inputs.quantumEfficiency > 0 &&
        inputs.quantumEfficiency <= 1))
      'a QE between 0 and 1',
    if (!pos(inputs.filterBandwidthNm)) 'a positive filter passband',
    if (!(inputs.noiseTolerancePct.isFinite &&
        inputs.noiseTolerancePct > 0 &&
        inputs.noiseTolerancePct <= 100))
      'a noise tolerance between 0 and 100 %',
    if (!pos(inputs.skyCalibrationK)) 'a positive calibration k',
    if (!inRange(inputs.skyMagPerArcsec2, 10, 23))
      'a sky brightness between 10 and 23 mag/arcsec²',
    if (!inRange(inputs.peakAltitudeDeg, 0.001, 90))
      'a peak altitude between 0 and 90°',
    if (inputs.surfaceBrightnessMagArcsec2 case final sb?
        when !(sb.isFinite && sb >= 0))
      'a surface brightness of 0 or more',
  ];
  if (problems.isNotEmpty) {
    return CalculatorResult(error: 'Needs ${problems.join(', ')}.');
  }
  final input = inputs.toSubInput();
  final sub = computeOptimalSub(input);
  // Extreme finite inputs (a huge aperture, a vanishing pixel) can push the
  // flux to 0, Inf or NaN; the result is then a sentence, not "Infinity s".
  if (!(sub.skyFluxEPerSecPerPx.isFinite &&
      sub.skyFluxEPerSecPerPx > 0 &&
      sub.floorSec.isFinite &&
      sub.ceilingSec.isFinite)) {
    return const CalculatorResult(
      error: 'These figures overflow the sky model — check the aperture, focal length, pixel size and sky brightness.',
    );
  }
  IntegrationBudget? budget;
  if (inputs.surfaceBrightnessMagArcsec2 case final sb? when sb > 0) {
    budget = computeIntegrationBudget(
      input: input,
      surfaceBrightnessMagArcsec2: sb,
      peakAltitudeDeg: inputs.peakAltitudeDeg,
    );
  }
  return CalculatorResult(
    sub: sub,
    budget: budget,
    pixelScaleArcsec: pixelScaleArcsec(input),
  );
}

/// A row's number: fixed decimals with the fractional tail trimmed; an integer
/// (digits 0) is left whole — its own zeros are not a tail.
String formatCalculatorNumber(double v, int digits) {
  if (!v.isFinite) return v.toString();
  final fixed = v.toStringAsFixed(digits);
  return digits == 0 ? fixed : fixed.replaceFirst(RegExp(r'\.?0+$'), '');
}

/// Seconds as the advisor prints them.
String formatSubSeconds(double seconds) => seconds < 120
    ? '${seconds.round()} s'
    : '${(seconds / 60).toStringAsFixed(seconds >= 600 ? 0 : 1)} min';
