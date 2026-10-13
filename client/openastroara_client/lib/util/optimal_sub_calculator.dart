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
  final problems = <String>[
    if (!(inputs.readNoiseE > 0)) 'read noise',
    if (!(inputs.fullWellE > 0)) 'full well',
    if (!(inputs.pixelSizeUm > 0)) 'pixel size',
    if (!(inputs.apertureMm > 0)) 'aperture',
    if (!(inputs.focalLengthMm > 0)) 'focal length',
    if (!(inputs.reducerFactor > 0)) 'reducer factor',
    if (!(inputs.quantumEfficiency > 0 && inputs.quantumEfficiency <= 1))
      'QE (0–1)',
    if (!(inputs.filterBandwidthNm > 0)) 'filter passband',
    if (!(inputs.noiseTolerancePct > 0 && inputs.noiseTolerancePct <= 100))
      'noise tolerance (0–100 %)',
    if (!(inputs.skyCalibrationK > 0)) 'calibration k',
    if (!(inputs.skyMagPerArcsec2 >= 10 && inputs.skyMagPerArcsec2 <= 23))
      'sky brightness (10–23 mag/arcsec²)',
  ];
  if (problems.isNotEmpty) {
    return CalculatorResult(error: 'Needs a positive ${problems.join(', ')}.');
  }
  final input = inputs.toSubInput();
  final sub = computeOptimalSub(input);
  IntegrationBudget? budget;
  if (inputs.surfaceBrightnessMagArcsec2 case final sb? when sb > 0) {
    budget = computeIntegrationBudget(
      input: input,
      surfaceBrightnessMagArcsec2: sb,
      peakAltitudeDeg: inputs.peakAltitudeDeg.clamp(1.0, 90.0),
    );
  }
  return CalculatorResult(
    sub: sub,
    budget: budget,
    pixelScaleArcsec: pixelScaleArcsec(input),
  );
}

/// Seconds as the advisor prints them.
String formatSubSeconds(double seconds) => seconds < 120
    ? '${seconds.round()} s'
    : '${(seconds / 60).toStringAsFixed(seconds >= 600 ? 0 : 1)} min';
