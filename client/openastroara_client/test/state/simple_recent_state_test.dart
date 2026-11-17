import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/state/sky_atlas/custom_targets_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/services/custom_targets_service.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/simple_recent_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart'
    show FilterPlanStep;

void main() {
  PlanningDso? resolve(String n) => n == 'M 31'
      ? const PlanningDso(
          id: 'M31',
          name: 'Andromeda',
          type: 'G',
          magnitude: 3.4,
          raDeg: 10.7,
          decDeg: 41.3,
        )
      : null;
  CustomTarget saved(String n, int day) => CustomTarget(
    name: n,
    raDeg: 1,
    decDeg: 2,
    typedAsJNow: false,
    savedUtc: DateTime.utc(2026, 1, day),
  );

  group('Recent', () {
    test('merges imaged and searched, newest first, the last five', () {
      final r = mergeRecentTargets(
        [
          StatsTarget(
            targetName: 'M 31',
            lastImagedUtc: DateTime.utc(2026, 1, 20),
          ),
        ],
        [for (var d = 1; d <= 6; d++) saved('T$d', d)],
        resolve,
      );
      expect(r.map((t) => t.name), ['M 31', 'T6', 'T5', 'T4', 'T3']);
      expect(r.first.raDeg, 10.7);
    });

    test('one target once (newest wins); an imaged one the catalog cannot place is left out', () {
      final r = mergeRecentTargets(
        [
          StatsTarget(
            targetName: 'M 31',
            lastImagedUtc: DateTime.utc(2026, 1, 2),
          ),
          StatsTarget(
            targetName: 'Mystery',
            lastImagedUtc: DateTime.utc(2026, 1, 9),
          ),
        ],
        [saved('M31', 5)],
        resolve,
      );
      expect(r.map((t) => t.name), ['M31']);
      expect(r.single.when, DateTime.utc(2026, 1, 5));
    });
  });

  test('Recent places a target saved as part of a common name', () async {
    const rosette = PlanningDso(
      id: 'NGC2237',
      name: 'Rosette Nebula',
      type: 'EN',
      magnitude: 9,
      raDeg: 98.0,
      decDeg: 4.9,
    );
    final c = ProviderContainer(
      overrides: [
        bundledCatalogProvider.overrideWith((ref) async => [rosette]),
        statsTargetsProvider.overrideWith(
          () => _Stats([
            StatsTarget(
              targetName: 'Rosette',
              lastImagedUtc: DateTime.utc(2026, 11, 1),
            ),
          ]),
        ),
        customTargetsProvider.overrideWith(() => _Saved()),
      ],
    );
    addTearDown(c.dispose);
    final sub = c.listen(simpleRecentProvider, (_, _) {});
    await c.read(bundledCatalogProvider.future);
    await c.read(statsTargetsProvider.future);
    await c.read(customTargetsProvider.future);
    final r = c.read(simpleRecentProvider);
    sub.close();
    expect(r.map((t) => t.name), ['Rosette']);
    expect(r.single.raDeg, 98.0);
  });

  test('a stats row five newer entries already beat is never resolved', () {
    final asked = <String>[];
    final r = mergeRecentTargets(
      [
        for (var d = 1; d <= 10; d++)
          StatsTarget(
            targetName: 'Old $d',
            lastImagedUtc: DateTime.utc(2025, 1, d),
          ),
      ],
      [for (var d = 1; d <= 5; d++) saved('T$d', d)],
      (n) {
        asked.add(n);
        return null;
      },
    );
    expect(asked, isEmpty);
    expect(r.map((t) => t.name), ['T5', 'T4', 'T3', 'T2', 'T1']);
  });

  test('Recent places imaged targets without a catalog scan each: the Plan window opens without a freeze', () async {
    // The real 17k-row bundle; a scan per stats row (most miss: "M 31 2",
    // custom names) took ~190 ms each on the UI thread.
    final catalog = parseBundledCatalogs([
      for (final a in bundledCatalogAssets) File(a).readAsStringSync(),
    ]);
    final stats = [
      for (var i = 0; i < 40; i++)
        StatsTarget(
          targetName: i == 0 ? 'NGC 7000' : 'Target $i',
          lastImagedUtc: DateTime.utc(2026, 11, 1).add(Duration(hours: i)),
        ),
    ];
    final c = ProviderContainer(
      overrides: [
        bundledCatalogProvider.overrideWith((ref) async => catalog),
        statsTargetsProvider.overrideWith(() => _Stats(stats)),
        customTargetsProvider.overrideWith(() => _Saved()),
      ],
    );
    addTearDown(c.dispose);
    final sub = c.listen(simpleRecentProvider, (_, _) {});
    await c.read(bundledCatalogProvider.future);
    await c.read(statsTargetsProvider.future);
    await c.read(customTargetsProvider.future);
    final sw = Stopwatch()..start();
    final r = c.read(simpleRecentProvider);
    sw.stop();
    sub.close();
    expect(r.map((t) => t.name), ['NGC 7000']);
    expect(sw.elapsedMilliseconds, lessThan(1500));
  });

  group('frames that fit before 30°/dawn', () {
    const rows = [
      FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 20),
      FilterPlanStep(filterName: 'R', exposureSeconds: 300, frameCount: 20),
    ];
    test('each row gets an equal share of the hours, as the plan model splits them', () {
      // 2 h = 7200 s; 3600 s a row = 12 subs of 300 s.
      expect(simpleFramesThatFit(rows, 2), 24);
      expect(simpleFramesThatFit(rows, 4), 40, reason: 'never more than typed');
      expect(simpleFramesThatFit(rows, 0), 0);
    });
  });
}

class _Stats extends StatsTargetsNotifier {
  _Stats(this.rows);
  final List<StatsTarget> rows;
  @override
  Future<List<StatsTarget>?> build() async => rows;
}

class _Saved extends CustomTargetsNotifier {
  @override
  Future<List<CustomTarget>> build() async => const [];
}
