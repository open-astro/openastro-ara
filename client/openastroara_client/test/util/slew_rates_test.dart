import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/util/slew_rates.dart';

/// Percentage slew-speed ladder: presets of the mount's max (1/5/10/25/50/100%),
/// capped at it. Every option must be <= the mount's max — the UI can never ask
/// a mount to slew faster than it advertises.
void main() {
  test('single mount rate (AM5N 6.016 °/s) yields percentage presets', () {
    final options = buildSlewRateOptions(const [6.016427458257683]);
    expect(options, hasLength(6));
    // Ascending; first is 1% of max, last is exactly max (100%).
    final rates = options.map((o) => o.rateDegPerSec).toList();
    for (var i = 1; i < rates.length; i++) {
      expect(rates[i], greaterThan(rates[i - 1]));
    }
    expect(rates.first, closeTo(6.016427458257683 * 0.01, 1e-9));
    expect(rates.last, closeTo(6.016427458257683, 1e-9));
    // Percentage labels carry the deg/s value.
    expect(options.first.label, startsWith('1%'));
    expect(options.last.label, startsWith('100%'));
    expect(options.last.label, contains('°/s'));
  });

  test('a driver ladder of three or more rates is honored as-is', () {
    final options = buildSlewRateOptions(const [1.0, 4.0, 6.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0, 6.0]);
    expect(options.first.label, '1°/s'); // deg/s labels for a driver ladder
  });

  group('two reported rates are one band [min, max] (#1085)', () {
    test('presets under the minimum are replaced by the minimum itself', () {
      // The issue's mount: one band (2.0, 6.0). 1/5/10/25 % of 6 all sit under
      // 2 °/s — the daemon would refuse or speed them up — so they go, and
      // "min" takes their place as the slowest chip.
      final options = buildSlewRateOptions(const [2.0, 6.0]);
      expect(options.map((o) => o.rateDegPerSec).toList(), [2.0, 3.0, 6.0]);
      expect(options.map((o) => o.label).toList(),
          ['min · 2°/s', '50% · 3°/s', '100% · 6°/s']);
    });

    test('a preset landing exactly on the minimum keeps its percentage label',
        () {
      final options = buildSlewRateOptions(const [1.0, 4.0]);
      expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 2.0, 4.0]);
      expect(options.first.label, '25% · 1°/s');
      expect(options.any((o) => o.label.startsWith('min')), isFalse);
    });

    test('a minimum below every preset adds no chip', () {
      // AM5N-style band (0.001, 6.016): nothing falls under the minimum, so
      // the six-preset ladder is unchanged.
      final options = buildSlewRateOptions(const [0.001, 6.016427458257683]);
      expect(options, hasLength(6));
      expect(options.first.label, startsWith('1%'));
      expect(options.every((o) => o.rateDegPerSec >= 0.001), isTrue);
    });

    test('no option ever sits below the reported minimum', () {
      for (final band in [
        const [0.5, 6.0],
        const [2.0, 6.0],
        const [3.0, 3.5],
        const [5.9, 6.0],
      ]) {
        final options = buildSlewRateOptions(band);
        expect(options, isNotEmpty);
        expect(options.first.rateDegPerSec, band.first,
            reason: '$band: the slowest chip is the reported minimum');
        for (final o in options) {
          expect(o.rateDegPerSec, greaterThanOrEqualTo(band.first));
          expect(o.rateDegPerSec, lessThanOrEqualTo(band.last));
        }
      }
    });
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
    final options = buildSlewRateOptions(const [3.0, 3.0, 6.0, 1.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 3.0, 6.0]);
  });

  test('unsorted mount rates come out ascending', () {
    final options = buildSlewRateOptions(const [6.0, 1.0, 4.0]);
    expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0, 6.0]);
  });
}
