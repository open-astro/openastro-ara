import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/slew_rates.dart';

/// ASIAir-style slew-speed ladder: x-multipliers of sidereal rate (1x =
/// tracking speed) capped at the mount's max, with MAX exactly the max. Every
/// option must be <= the mount's max — the UI can never ask a mount to slew
/// faster than it advertises.
void main() {
  test('single mount rate (AM5N 6.016 °/s) yields the ASIAir x ladder', () {
    final options = buildSlewRateOptions(const [6.016427458257683]);
    // 1x..512x sidereal are all < 6.016 °/s, plus MAX -> 10 options.
    expect(options, hasLength(10));
    expect(options.first.rateDegPerSec, closeTo(kSiderealRateDegPerSec, 1e-12));
    expect(options.first.label, '1x');
    expect(options[8].label, '512x');
    expect(options.last.label, 'MAX');
    expect(options.last.rateDegPerSec, closeTo(6.016427458257683, 1e-9));
    // Ascending.
    for (var i = 1; i < options.length; i++) {
      expect(options[i].rateDegPerSec, greaterThan(options[i - 1].rateDegPerSec));
    }
    // x presets carry the deg/s detail line.
    expect(options[1].detail, isNotNull);
  });

  test('ladder stops below the max and never exceeds it', () {
    // Max 2.0 °/s: 1x..512x (2.14) — 512x is dropped, MAX = 2.0.
    final options = buildSlewRateOptions(const [2.0]);
    expect(options.last.rateDegPerSec, closeTo(2.0, 1e-9));
    expect(options.map((o) => o.rateDegPerSec).any((r) => r > 2.0), isFalse);
    expect(options.any((o) => o.label == '512x'), isFalse);
  });

  test('multiple mount rates are honored as-is (no presets injected)', () {
    final options = buildSlewRateOptions(const [1.0, 4.0, 6.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0, 6.0]);
    expect(options.first.label, '1°/s'); // deg/s labels for a driver ladder
  });

  test('no rate ever exceeds the mount max', () {
    for (final mountRates in [
      const [6.016427458257683],
      const [2.0, 8.0, 12.5],
      const [0.5],
      const [10.0, 0.0, -3.0],
    ]) {
      final options = buildSlewRateOptions(mountRates);
      final maxRate =
          mountRates.where((r) => r > 0).reduce((a, b) => a > b ? a : b);
      for (final o in options) {
        expect(o.rateDegPerSec, lessThanOrEqualTo(maxRate),
            reason: '$o must not exceed max $maxRate');
      }
    }
  });

  test('empty / zero / negative inputs produce no options', () {
    expect(buildSlewRateOptions(const []), isEmpty);
    expect(buildSlewRateOptions(const [0.0]), isEmpty);
    expect(buildSlewRateOptions(const [-1.0, 0.0]), isEmpty);
  });

  test('duplicate mount rates are deduped', () {
    final options = buildSlewRateOptions(const [3.0, 3.0, 6.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [3.0, 6.0]);
  });

  test('unsorted mount rates come out ascending', () {
    final options = buildSlewRateOptions(const [6.0, 1.0, 4.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0, 6.0]);
  });
}
