import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

/// #1141 — the ±12 h / 288-sample window scan against the REAL bundled
/// catalogs (all eight packages, ~16k rows), timed. The ranking runs in an
/// isolate on the client, so this is the budget the Tonight's Sky tab pays on
/// every recompute (site, optics, filter or clock change). The assertion is a
/// loose ceiling that only catches a pathological regression (an O(n²) merge,
/// a per-object sun/moon recompute); the measured number is printed for the
/// issue record. Dart VM JIT here; AOT on a phone or the Pi-driven desktop is
/// in the same ballpark for this kind of straight-line arithmetic.
void main() {
  List<PlanningDso> load(String file) =>
      parseOpenNgcCsv(File('assets/catalogs/$file').readAsStringSync());

  const site = SiteSettings(
    siteName: 'profile',
    latitudeDeg: 34.0,
    longitudeDeg: -84.0,
    bortleClass: 6,
    defaultHorizonAltitudeDeg: 20,
    twilightDefinition: TwilightDefinition.astronomical,
    softWarningAltitudeDeg: 30,
  );
  const optics = OpticsSettings(
    focalLengthMm: 250,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 51,
  );

  test('the window scan over the full bundled catalog stays well inside a frame budget', () {
    final catalog = <PlanningDso>[
      for (final f in const [
        'NGC.csv',
        'sh2.csv',
        'ldn.csv',
        'barnard.csv',
        'vdb.csv',
        'abell-pn.csv',
        'arp.csv',
        'wr.csv',
      ])
        ...load(f),
    ];
    expect(
      catalog.length,
      greaterThan(15000),
      reason: 'the real installed set, not a fixture',
    );

    List<TonightSkyObject> run(DateTime at) => computeTonightSkyLocal(
      site: site,
      optics: optics,
      atUtc: at,
      catalog: catalog,
      limit: 30,
    );

    // Warm the JIT once, then time five nights across the year (different
    // dark windows, different fraction of the catalog above the horizon).
    run(DateTime.utc(2026, 1, 15, 3));
    final nights = [
      DateTime.utc(2026, 1, 15, 3),
      DateTime.utc(2026, 4, 15, 3),
      DateTime.utc(2026, 7, 15, 3),
      DateTime.utc(2026, 10, 15, 3),
      DateTime.utc(2026, 12, 15, 3),
    ];
    final ms = <int>[];
    for (final night in nights) {
      final sw = Stopwatch()..start();
      final list = run(night);
      sw.stop();
      ms.add(sw.elapsedMilliseconds);
      expect(list, isNotEmpty);
    }
    ms.sort();
    // ignore: avoid_print
    print(
      '[#1141] window scan over ${catalog.length} rows: '
      'min ${ms.first} ms, median ${ms[ms.length ~/ 2]} ms, max ${ms.last} ms (Dart VM JIT)',
    );
    expect(
      ms[ms.length ~/ 2],
      lessThan(2000),
      reason: 'a loose ceiling: catches an algorithmic regression, not jitter',
    );
  });
}
