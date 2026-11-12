import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/planning_prefs_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/planning/planning_prefs_state.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

/// #1622 — the mosaic size: prefs parse, and the ranking follows it.
class _Seeded extends SiteSettingsNotifier {
  @override
  SiteSettings build() =>
      const SiteSettings(latitudeDeg: 51.477833, longitudeDeg: -0.0015);
}

class _Optics extends OpticsSettingsNotifier {
  @override
  OpticsSettings build() => const OpticsSettings(
    focalLengthMm: 1000,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 100,
  );
}

class _MemPrefs extends PlanningPrefsService {
  PlanningPrefs stored = const PlanningPrefs();
  @override
  Future<PlanningPrefs> load() async => stored;
  @override
  Future<void> save(PlanningPrefs prefs) async => stored = prefs;
}

void main() {
  test('mosaic tiles: default 1, round-trip, missing, clamp, non-numeric', () {
    expect(const PlanningPrefs().mosaicTilesX, 1);
    expect(const PlanningPrefs().mosaicTilesY, 1);
    final blank = PlanningPrefs.fromJson(const {});
    expect((blank.mosaicTilesX, blank.mosaicTilesY), (1, 1));
    final p = const PlanningPrefs().withMosaicTiles(2, 3);
    final back = PlanningPrefs.fromJson(p.toJson());
    expect((back.mosaicTilesX, back.mosaicTilesY), (2, 3));
    final big = PlanningPrefs.fromJson(const {
      'mosaic_tiles_x': 9,
      'mosaic_tiles_y': -2,
    });
    expect((big.mosaicTilesX, big.mosaicTilesY), (4, 1));
    final bad = PlanningPrefs.fromJson(const {
      'mosaic_tiles_x': 'x',
      'mosaic_tiles_y': double.nan,
    });
    expect((bad.mosaicTilesX, bad.mosaicTilesY), (1, 1));
    expect(p.copyWith(noiseTolerancePct: 7).mosaicTilesY, 3);
    expect(p.withMagnitudeFloor(9).mosaicTilesX, 2);
  });

  test('the setter clamps and persists', () async {
    final mem = _MemPrefs();
    final c = ProviderContainer(
      overrides: [planningPrefsServiceProvider.overrideWithValue(mem)],
    );
    addTearDown(c.dispose);
    c.read(planningPrefsProvider.notifier).setMosaicTiles(7, 0);
    expect(c.read(planningPrefsProvider).mosaicTilesX, 4);
    expect(c.read(planningPrefsProvider).mosaicTilesY, 1);
    expect(mem.stored.mosaicTilesX, 4);
  });

  test(
    'a target too big at 1x1 is not too big at 2x2 for the same optics',
    () async {
      final mem = _MemPrefs();
      final c = ProviderContainer(
        overrides: [
          planningPrefsServiceProvider.overrideWithValue(mem),
          activeServerFutureProvider.overrideWith((ref) async => null),
          planningSettingsBootstrapProvider.overrideWith((ref) async {}),
          siteSettingsProvider.overrideWith(_Seeded.new),
          opticsSettingsProvider.overrideWith(_Optics.new),
          skyModelCalibrationProvider.overrideWith(() => _NoCal()),
          dsoCatalogProvider.overrideWith(
            (ref) async => [
              const PlanningDso(
                id: 'NGC7000',
                name: 'North America Nebula',
                type: 'HII',
                magnitude: 4.0,
                raDeg: 314.75,
                decDeg: 44.33,
                sizeMajArcmin: 70,
                sizeMinArcmin: 60,
              ),
            ],
          ),
        ],
      );
      addTearDown(c.dispose);
      final keep = c.listen(tonightSkyProvider, (_, _) {});
      addTearDown(keep.close);
      TonightFraming? framing(List<TonightSkyObject> l) =>
          l.where((o) => o.id == 'NGC7000').firstOrNull?.framing;
      final single = await c.read(tonightSkyProvider.future);
      expect(framing(single), TonightFraming.tooBig);
      c.read(planningPrefsProvider.notifier).setMosaicTiles(2, 2);
      final mosaic = await c.read(tonightSkyProvider.future);
      expect(framing(mosaic), isNot(TonightFraming.tooBig));
    },
  );
}

class _NoCal extends SkyModelCalibrationNotifier {
  @override
  Future<SkyModelCalibration?> build() async => null;
}
