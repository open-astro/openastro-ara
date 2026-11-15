import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

/// Pins savedServersProvider to an empty list = genuinely no server.
class _NoServers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [];
}

class _SavedPrefs extends PlanningPrefsService {
  _SavedPrefs(this.prefs);
  final PlanningPrefs prefs;
  @override
  Future<PlanningPrefs> load() async => prefs;
  @override
  Future<void> save(PlanningPrefs prefs) async {}
}

class _SeededOptics extends OpticsSettingsNotifier {
  _SeededOptics(this._v);
  final OpticsSettings _v;
  @override
  OpticsSettings build() => _v;
}

class _SeededElectronics extends CameraElectronicsNotifier {
  _SeededElectronics(this._v);
  final CameraElectronics _v;
  @override
  CameraElectronics build() => _v;
}

class _SeededFilters extends FilterSetNotifier {
  _SeededFilters(this._v);
  final FilterSetSettings _v;
  @override
  FilterSetSettings build() => _v;
}

class _SeededSite extends SiteSettingsNotifier {
  _SeededSite(this._site);
  final SiteSettings _site;
  @override
  SiteSettings build() => _site;
}

class _SeededCalibration extends SkyModelCalibrationNotifier {
  _SeededCalibration(this._v);
  final SkyModelCalibration? _v;
  @override
  Future<SkyModelCalibration?> build() async => _v;
}

void main() {
  test('no server + cached site → ranks locally', () async {
    final c = ProviderContainer(overrides: [
      savedServersProvider.overrideWith(_NoServers.new),
      siteSettingsProvider.overrideWith(() => _SeededSite(const SiteSettings(
            siteName: 'cached',
            latitudeDeg: 34.0,
            longitudeDeg: -84.0,
            bortleClass: 6,
            defaultHorizonAltitudeDeg: 20,
            twilightDefinition: TwilightDefinition.astronomical,
          ))),
    ]);
    addTearDown(c.dispose);
    // Hold the autoDispose ranker alive across its awaits (the planning
    // catalog now loads the bundled CSVs first, which takes real time) —
    // a bare read lets it dispose mid-build, exactly as the plan dialog
    // learned. This is what the panel does by watching it.
    final keep = c.listen(tonightSkyProvider, (_, _) {});
    addTearDown(keep.close);
    final list = await c.read(tonightSkyProvider.future);
    expect(list, isNotEmpty);
    expect(list.first.score, isNotNull);
    expect(list.first.scoreReasons!.join(' '), contains('offline ranking'));
  });

  test('no server + unset site (0,0) → empty, never a Gulf-of-Guinea ranking',
      () async {
    final c = ProviderContainer(overrides: [
      savedServersProvider.overrideWith(_NoServers.new),
    ]);
    addTearDown(c.dispose);
    expect(await c.read(tonightSkyProvider.future), isEmpty);
  });


  test('#1140 the saved noise-tolerance knob reaches the ranker (a tighter knob = longer subs)', () async {
    Future<double> subAt(double pct) async {
      final c = ProviderContainer(overrides: [
        savedServersProvider.overrideWith(_NoServers.new),
        planningPrefsServiceProvider
            .overrideWithValue(_SavedPrefs(PlanningPrefs(noiseTolerancePct: pct))),
        siteSettingsProvider.overrideWith(() => _SeededSite(const SiteSettings(
              siteName: 'cached',
              latitudeDeg: 34.0,
              longitudeDeg: -84.0,
              bortleClass: 6,
              defaultHorizonAltitudeDeg: 20,
              twilightDefinition: TwilightDefinition.astronomical,
            ))),
        opticsSettingsProvider.overrideWith(() => _SeededOptics(const OpticsSettings(
              focalLengthMm: 447, reducerFactor: 1.0, sensorWidthPx: 6248,
              sensorHeightPx: 4176, pixelSizeUm: 3.76, apertureMm: 91))),
        cameraElectronicsProvider.overrideWith(() => _SeededElectronics(const CameraElectronics(
              readNoiseE: 1.5, fullWellE: 50000, electronsPerAdu: 0.8, quantumEfficiencyPeak: 0.8))),
        filterSetProvider.overrideWith(() => _SeededFilters(const FilterSetSettings(
              filters: [PlanningFilter(name: 'L', kind: FilterKind.l)]))),
      ]);
      addTearDown(c.dispose);
      final keep = c.listen(tonightSkyProvider, (_, _) {});
      addTearDown(keep.close);
      final list = await c.read(tonightSkyProvider.future);
      final withSub = list.where((o) => o.optimalSubS != null).toList();
      expect(withSub, isNotEmpty, reason: 'a configured rig yields an optimal sub');
      return withSub.first.optimalSubS!;
    }

    final loose = await subAt(5);
    final tight = await subAt(2);
    expect(tight, greaterThan(loose), reason: 'the knob was read from the saved prefs, not the default');
  });

  test('#1140 P2 the rig\'s fitted sky-model k reaches the ranker (k 0.5 = longer subs, longer hours)', () async {
    Future<TonightSkyObject> firstAt(SkyModelCalibration? record) async {
      final c = ProviderContainer(overrides: [
        savedServersProvider.overrideWith(_NoServers.new),
        skyModelCalibrationProvider.overrideWith(() => _SeededCalibration(record)),
        siteSettingsProvider.overrideWith(() => _SeededSite(const SiteSettings(
              siteName: 'cached', latitudeDeg: 34.0, longitudeDeg: -84.0, bortleClass: 6,
              defaultHorizonAltitudeDeg: 20, twilightDefinition: TwilightDefinition.astronomical))),
        opticsSettingsProvider.overrideWith(() => _SeededOptics(const OpticsSettings(
              focalLengthMm: 447, reducerFactor: 1.0, sensorWidthPx: 6248,
              sensorHeightPx: 4176, pixelSizeUm: 3.76, apertureMm: 91))),
        cameraElectronicsProvider.overrideWith(() => _SeededElectronics(const CameraElectronics(
              readNoiseE: 1.5, fullWellE: 50000, electronsPerAdu: 0.8, quantumEfficiencyPeak: 0.8))),
        filterSetProvider.overrideWith(() => _SeededFilters(const FilterSetSettings(
              filters: [PlanningFilter(name: 'L', kind: FilterKind.l)]))),
      ]);
      addTearDown(c.dispose);
      final keep = c.listen(tonightSkyProvider, (_, _) {});
      addTearDown(keep.close);
      final list = await c.read(tonightSkyProvider.future);
      return list.firstWhere((o) => o.optimalSubS != null);
    }

    final spec = await firstAt(null);
    final fitted = await firstAt(SkyModelCalibration(
      fittedUtc: DateTime.utc(2026, 10, 13),
      framesConsidered: 30,
      filters: const [SkyModelFilterCalibration(kind: 'l', bin: 1, k: 0.5, frameCount: 30, passed: true)],
    ));
    // The sub length is the ranker output every object carries; the hours' 1/k
    // scaling is pinned in integration_budget_test (the same input feeds both).
    expect(fitted.optimalSubS!, greaterThan(spec.optimalSubS!), reason: 'half the sky rate: the Glover floor moves out');
  });
}
