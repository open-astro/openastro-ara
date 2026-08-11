import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/slew_rates.dart';

/// ZWO-ASIAir-style slew-rate ladder: the mount's own rates when it reports
/// several, else percentage presets of the max (1/5/10/25/50/100%). Every
/// option must be <= the mount's max — the UI can never ask a mount to slew
/// faster than it advertises.
void main() {
  test('single mount rate (AM5N 6.016 °/s) yields ZWO-style presets', () {
    final options = buildSlewRateOptions(const [6.016427458257683]);
    expect(options, hasLength(6));
    // Ascending, first is 1% of max, last is exactly max.
    final rates = options.map((o) => o.rateDegPerSec).toList();
    for (var i = 1; i < rates.length; i++) {
      expect(rates[i], greaterThan(rates[i - 1]));
    }
    expect(rates.first, closeTo(6.016427458257683 * 0.01, 1e-9));
    expect(rates.last, closeTo(6.016427458257683, 1e-9));
    // Preset labels carry the percentage.
    expect(options.last.label, startsWith('100%'));
    expect(options[3].label, startsWith('25%'));
    // Labels also carry the deg/s value.
    expect(options.last.label, contains('°/s'));
  });

  test('multiple mount rates are honored as-is (no presets injected)', () {
    final options = buildSlewRateOptions(const [1.0, 4.0, 6.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0, 6.0]);
  });

  test('no rate ever exceeds the mount max', () {
    for (final mountRates in [
      const [6.016427458257683],
      const [2.0, 8.0, 12.5],
      const [0.5],
      const [10.0, 0.0, -3.0],
    ]) {
      final options = buildSlewRateOptions(mountRates);
      final maxRate = mountRates.where((r) => r > 0).reduce((a, b) => a > b ? a : b);
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
