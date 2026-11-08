import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/equipment/dome_shutter_state.dart';

void main() {
  final start = DateTime(2026, 11, 8, 21);
  final move = DomeShutterMove(
    opening: true,
    startedAt: start,
    expected: const Duration(seconds: 60),
  );
  double at(double seconds) => move.progressAt(
    start.add(Duration(milliseconds: (seconds * 1000).round())),
  );

  test(
    'the shells slide at the expected pace, then slow, never stopping dead',
    () {
      expect(at(30), closeTo(0.5, 0.01));
      // A dome slower than last time keeps easing toward the end instead of
      // freezing at a cap, so the landing has no hard stop and no big jump.
      expect(at(66), greaterThan(at(60)));
      expect(at(90), greaterThan(at(66)));
      expect(at(600), lessThan(1));
      expect(at(600), greaterThan(0.95));
    },
  );

  test('the slow-down joins the steady slide without a kink', () {
    // The pace just before and just after the join (80 %) is the same.
    final before = (at(47.9) - at(47.3)) / 0.6;
    final after = (at(48.7) - at(48.1)) / 0.6;
    expect(after, closeTo(before, before * 0.05));
  });
}
