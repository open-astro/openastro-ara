import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/optimal_sub_local.dart';

void main() {
  const optics = OpticsSettings(
    focalLengthMm: 447,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 91,
  );
  const electronics = CameraElectronics(
    readNoiseE: 1.5,
    fullWellE: 50000,
    electronsPerAdu: 0.8,
    quantumEfficiencyPeak: 0.8,
  );
  const site = SiteSettings(bortleClass: 6);
  const filters = FilterSetSettings(
    filters: [
      PlanningFilter(name: 'L', kind: FilterKind.l),
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
    ],
  );

  test(
    "#1140 P2 the matched filter's calibration k reaches the sky flux (dimmer sky = longer sub)",
    () {
      LocalOptimalSubOutcome run(Map<String, double> k) =>
          resolveOptimalSubLocal(
            optics: optics,
            electronics: electronics,
            site: site,
            filterSet: filters,
            filterName: 'L',
            skyCalibrationK: k,
          );
      final spec = (run(const {}) as LocalOptimalSubSuccess).result;
      final fitted = (run(const {'l': 0.5}) as LocalOptimalSubSuccess).result;
      final other = (run(const {'ha': 0.5}) as LocalOptimalSubSuccess).result;
      expect(
        fitted.skyFluxEPerSecPerPx,
        closeTo(spec.skyFluxEPerSecPerPx * 0.5, 1e-9),
      );
      expect(fitted.recommendedSec, greaterThan(spec.recommendedSec));
      expect(
        other.skyFluxEPerSecPerPx,
        closeTo(spec.skyFluxEPerSecPerPx, 1e-9),
        reason: "another kind's k is not applied to L",
      );
    },
  );

  test("a wheel label of a recognised kind finds the set's entry ('Hα' → 'Ha')",
      () {
    LocalOptimalSubOutcome run(String name, FilterSetSettings set) =>
        resolveOptimalSubLocal(
          optics: optics,
          electronics: electronics,
          site: site,
          filterSet: set,
          filterName: name,
        );
    final greek = run('Hα', filters);
    final plain = run('Ha', filters);
    expect(greek, isA<LocalOptimalSubSuccess>());
    expect(
      (greek as LocalOptimalSubSuccess).result.recommendedSec,
      (plain as LocalOptimalSubSuccess).result.recommendedSec,
    );
    expect(run('R', const FilterSetSettings(filters: [
      PlanningFilter(name: 'Red', kind: FilterKind.r),
    ])), isA<LocalOptimalSubSuccess>());
    // Two Hα entries: ambiguous, so the unknown-filter story stays.
    expect(run('Hα', const FilterSetSettings(filters: [
      PlanningFilter(name: 'Ha 3nm', kind: FilterKind.ha),
      PlanningFilter(name: 'Ha 7nm', kind: FilterKind.ha),
    ])), isA<LocalOptimalSubUnavailable>());
    // An unrecognised label never falls back to the broadband entry.
    expect(run('Clear', filters), isA<LocalOptimalSubUnavailable>());
  });

  test("a wheel name of the set entry's kind resolves to it ('Hα' → 'Ha')",
      () {
    LocalOptimalSubOutcome run(String name) => resolveOptimalSubLocal(
          optics: optics,
          electronics: electronics,
          site: site,
          filterSet: filters,
          filterName: name,
        );
    final greek = run('Hα') as LocalOptimalSubSuccess;
    final plain = run('Ha') as LocalOptimalSubSuccess;
    expect(greek.result.recommendedSec, plain.result.recommendedSec);
    expect(run('Filter 3'), isA<LocalOptimalSubUnavailable>());
  });
}
