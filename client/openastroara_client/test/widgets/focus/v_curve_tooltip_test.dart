import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/widgets/focus/v_curve_chart.dart';

/// #1139 — the V-curve tooltip names a probe's rim-vs-core asymmetry, the
/// side-of-focus signal Smart Focus reads; fit-curve points stay position + HFR.
void main() {
  const probes = [
    AutofocusProbe(
      index: 1,
      phase: 'fine',
      position: 10100,
      hfr: 2.4,
      stars: 80,
      kept: true,
      asymmetry: -0.42,
    ),
    AutofocusProbe(
      index: 2,
      phase: 'fine',
      position: 10200,
      hfr: 3.1,
      stars: 70,
      kept: true,
      asymmetry: 0.37,
    ),
    AutofocusProbe(
      index: 3,
      phase: 'fine',
      position: 10300,
      hfr: 4.0,
      stars: 60,
      kept: true,
    ),
  ];

  test('a probe with an asymmetry shows it, signed', () {
    expect(
      VCurveChart.tooltipText(10100, 2.4, probes),
      '10100 · HFR 2.40 · rim/core -0.42',
    );
    expect(
      VCurveChart.tooltipText(10200, 3.1, probes),
      '10200 · HFR 3.10 · rim/core +0.37',
    );
  });

  test('a probe without one, or a point on the fitted curve, shows position and HFR only', () {
    expect(VCurveChart.tooltipText(10300, 4.0, probes), '10300 · HFR 4.00');
    expect(VCurveChart.tooltipText(10150, 2.6, probes), '10150 · HFR 2.60');
  });
}
