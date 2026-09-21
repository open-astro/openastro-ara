import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/run_eta.dart';

void main() {
  test('early in the run the daemon\'s remaining estimate wins over the '
      'static scale (#1068)', () {
    final r = estimateRemainingSeconds(
        staticTotalSeconds: 1000,
        serverRemainingSeconds: 640,
        completed: 0,
        total: 10,
        elapsed: const Duration(seconds: 30));
    expect(r, 640);
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
