import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/run_eta.dart';

Map<String, dynamic> body() => {
      r'$type': 'X.SequentialContainer',
      'Items': {
        r'$type': itemsWrapperType,
        r'$values': [
          {r'$type': 'X.SlewScopeToRaDec'}, // nominal 15 s
          {
            r'$type': 'X.SequentialContainer',
            'Conditions': {
              r'$type': itemsWrapperType,
              r'$values': [
                {r'$type': 'X.LoopCondition', 'Iterations': 10},
              ],
            },
            'Items': {
              r'$type': itemsWrapperType,
              r'$values': [
                {r'$type': 'X.TakeExposure', 'ExposureTime': 120.0},
              ],
            },
          },
        ],
      },
    };

void main() {
  test('sums ExposureTime × loop Iterations + nominal per instruction', () {
    // 15 (slew) + 10 × 120 (loop) = 1215 s.
    expect(estimateRunEta(body()).totalSeconds, 1215);
  });

  test('remaining prefers observed rate once ≥10% and ≥2 leaves are done', () {
    // 4/10 done in 40 min → 10 min/leaf → 60 min left, static model ignored.
    final r = estimateRemainingSeconds(
        staticTotalSeconds: 99999,
        completed: 4,
        total: 10,
        elapsed: const Duration(minutes: 40));
    expect(r, 3600);
  });

  test('remaining falls back to the static model early in the run', () {
    final r = estimateRemainingSeconds(
        staticTotalSeconds: 1000,
        completed: 0,
        total: 10,
        elapsed: const Duration(seconds: 30));
    expect(r, 1000);
  });

  test('done / empty → zero, never negative', () {
    expect(
        estimateRemainingSeconds(
            staticTotalSeconds: 100,
            completed: 10,
            total: 10,
            elapsed: const Duration(hours: 1)),
        0);
    expect(
        estimateRemainingSeconds(
            staticTotalSeconds: 100,
            completed: 0,
            total: 0,
            elapsed: Duration.zero),
        0);
  });
}
