import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/observing_night.dart';

void main() {
  test('the small hours belong to the evening before', () {
    expect(observingNight(DateTime(2026, 11, 12, 1, 30)), '2026-11-11');
    expect(observingNight(DateTime(2026, 11, 11, 23, 0)), '2026-11-11');
    expect(observingNight(DateTime(2026, 11, 11, 11, 59)), '2026-11-10');
    expect(observingNight(DateTime(2026, 11, 11, 12, 0)), '2026-11-11');
  });

  test('isTonight: same night yes, the night before no, nothing no', () {
    final now = DateTime(2026, 11, 12, 0, 15);
    expect(isTonight(DateTime(2026, 11, 11, 22, 0), now), isTrue);
    expect(isTonight(DateTime(2026, 11, 10, 22, 0), now), isFalse);
    expect(isTonight(null, now), isFalse);
  });

  test('a UTC instant is placed by the local clock', () {
    final local = DateTime(2026, 11, 12, 1, 0);
    expect(observingNight(local.toUtc()), '2026-11-11');
  });
}
