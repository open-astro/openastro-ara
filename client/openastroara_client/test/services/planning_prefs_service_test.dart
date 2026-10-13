import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/planning_prefs_service.dart';

/// #1140 — the client-local planning knobs: round trip, defaults, clamping.
void main() {
  late Directory tmp;
  setUp(
    () async => tmp = await Directory.systemTemp.createTemp('planning_prefs'),
  );
  tearDown(() => tmp.delete(recursive: true));

  test('no file → defaults; save then load round-trips', () async {
    final svc = PlanningPrefsService(supportDir: () async => tmp);
    expect((await svc.load()).noiseTolerancePct, 5.0);
    await svc.save(const PlanningPrefs(noiseTolerancePct: 2.5));
    expect((await svc.load()).noiseTolerancePct, 2.5);
  });

  test(
    'a hand-edited file is clamped or defaulted, never trusted raw',
    () async {
      final svc = PlanningPrefsService(supportDir: () async => tmp);
      await File('${tmp.path}/planning_prefs.json')
          .writeAsString('{"noise_tolerance_pct": 95}');
      expect(
        (await svc.load()).noiseTolerancePct,
        PlanningPrefs.maxNoiseTolerancePct,
      );
      await File('${tmp.path}/planning_prefs.json')
          .writeAsString('{"noise_tolerance_pct": "lots"}');
      expect((await svc.load()).noiseTolerancePct, 5.0);
      await File('${tmp.path}/planning_prefs.json').writeAsString('not json');
      expect((await svc.load()).noiseTolerancePct, 5.0);
    },
  );
}
