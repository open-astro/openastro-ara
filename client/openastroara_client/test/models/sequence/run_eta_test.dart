import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/sequence/run_eta.dart';

void main() {
  test('the daemon\'s remaining estimate is shown whenever it sent one (#1080)', () {
    final early = estimateRemainingSeconds(
        serverRemainingSeconds: 640,
        completed: 0,
        total: 10,
        elapsed: const Duration(seconds: 30));
    expect(early, 640);
    // …and it outranks the observed rate even once that would be trusted — a
    // 30× exposure loop is ONE leaf, and elapsed includes pauses (reversing the
    // two branches fails this).
    final late = estimateRemainingSeconds(
        serverRemainingSeconds: 640,
        completed: 4,
        total: 10,
        elapsed: const Duration(minutes: 40));
    expect(late, 640);
  });

  test('without a daemon estimate the observed rate is used once ≥10% and ≥2 leaves are done', () {
    // 4/10 done in 40 min → 10 min/leaf → 60 min left.
    final r = estimateRemainingSeconds(
        completed: 4,
        total: 10,
        elapsed: const Duration(minutes: 40));
    expect(r, 3600);
  });

  test('no daemon estimate early in the run → nothing to show (0)', () {
    final r = estimateRemainingSeconds(
        serverRemainingSeconds: null,
        completed: 0,
        total: 10,
        elapsed: const Duration(seconds: 30));
    expect(r, 0);
  });

  test('done / empty → zero, never negative', () {
    expect(
        estimateRemainingSeconds(
            completed: 10,
            total: 10,
            elapsed: const Duration(hours: 1)),
        0);
    expect(
        estimateRemainingSeconds(
            completed: 0,
            total: 0,
            elapsed: Duration.zero),
        0);
  });
}
