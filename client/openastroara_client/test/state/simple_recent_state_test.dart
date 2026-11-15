import 'package:flutter_test/flutter_test.dart';
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
