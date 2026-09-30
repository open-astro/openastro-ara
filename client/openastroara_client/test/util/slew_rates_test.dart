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

    test('a preset within rounding of the minimum counts as landing on it', () {
      // 0.1 * max rounds a ulp away from min when min == max / 10 (either
      // side): no dropped preset, no near-duplicate "min" chip.
      const max = 3.3;
      final options = buildSlewRateOptions(const [max / 10, max]);
      expect(options.where((o) => o.label.startsWith('min')), isEmpty);
      expect(options.first.label, startsWith('10%'));
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

  group('bands on the wire (#1126)', () {
    // The daemon now publishes each pad band as {min, max}; a discrete rate is
    // min == max, "any speed up to max" is min 0. The presets are built from
    // the bands, so the default always lies inside one and the daemon's 4×
    // snap-up bound (409) can never refuse it.
    bool insideABand(double rate, List<SlewRateBand> bands) =>
        bands.any((b) => rate >= b.min - 1e-9 && rate <= b.max + 1e-9);

    test('a single discrete rate (6, 6) is one chip and the default', () {
      // The issue's mount: the legacy list [6.0] read as "up to 6" and the
      // middle preset (0.6 °/s) was refused by the daemon — a dead pad.
      const bands = [SlewRateBand(6.0, 6.0)];
      final options = buildSlewRateOptionsFromBands(bands);
      expect(options.map((o) => o.rateDegPerSec).toList(), [6.0]);
      expect(options.single.label, '6°/s');
      expect(defaultSlewRate(options), 6.0);
    });

    test('a (0, 6) band yields the percentage ladder with a mid default', () {
      const bands = [SlewRateBand(0.0, 6.0)];
      final options = buildSlewRateOptionsFromBands(bands);
      expect(options, hasLength(6));
      expect(options.first.label, '1% · 0.060°/s');
      expect(options.last.label, '100% · 6°/s');
      final def = defaultSlewRate(options)!;
      expect(def, closeTo(0.6, 1e-9));
      expect(insideABand(def, bands), isTrue);
    });

    test('a (2, 6) band drops presets under the floor and offers the floor', () {
      final options = buildSlewRateOptionsFromBands(const [SlewRateBand(2.0, 6.0)]);
      expect(options.map((o) => o.label).toList(),
          ['min · 2°/s', '50% · 3°/s', '100% · 6°/s']);
      expect(defaultSlewRate(options), 3.0);
    });

    test('two discrete rates are two chips, not a band between them', () {
      // The legacy list [0.004, 2.0] read as one band; the wire now says
      // otherwise, and no preset between the steps is offered.
      const bands = [SlewRateBand(0.004, 0.004), SlewRateBand(2.0, 2.0)];
      final options = buildSlewRateOptionsFromBands(bands);
      expect(options.map((o) => o.rateDegPerSec).toList(), [0.004, 2.0]);
      expect(options.map((o) => o.label).toList(), ['0.004°/s', '2°/s']);
      expect(insideABand(defaultSlewRate(options)!, bands), isTrue);
    });

    test('a normal multi-band mount keeps its ladder and defaults inside a band',
        () {
      // Three discrete steps and a slew band: the endpoint ladder as before.
      const bands = [
        SlewRateBand(0.002, 0.002),
        SlewRateBand(0.008, 0.008),
        SlewRateBand(0.033, 0.033),
        SlewRateBand(1.0, 4.0),
      ];
      final options = buildSlewRateOptionsFromBands(bands);
      expect(options.map((o) => o.rateDegPerSec).toList(),
          [0.002, 0.008, 0.033, 1.0, 4.0]);
      final def = defaultSlewRate(options)!;
      expect(def, 0.033);
      expect(insideABand(def, bands), isTrue);
      for (final o in options) {
        expect(insideABand(o.rateDegPerSec, bands), isTrue, reason: '$o');
      }
    });

    test('bands arrive unsorted or degenerate and are sanitised', () {
      final options = buildSlewRateOptionsFromBands(const [
        SlewRateBand(4.0, 4.0),
        SlewRateBand(1.0, 1.0),
        SlewRateBand(-1.0, 0.0), // dropped: non-positive max
        SlewRateBand(4.0, 4.0), // duplicate
      ]);
      expect(options.map((o) => o.rateDegPerSec).toList(), [1.0, 4.0]);
      expect(buildSlewRateOptionsFromBands(const []), isEmpty);
      expect(defaultSlewRate(const []), isNull);
    });

    test('the legacy endpoint list converts to bands the old way', () {
      // An older daemon sends only endpoints: one rate is "up to max", two are
      // one band, three or more are discrete steps — exactly what the list
      // reader assumed before the bands existed.
      expect(slewRateBandsFromLegacyRates(const [6.0]),
          [const SlewRateBand(0.0, 6.0)]);
      expect(slewRateBandsFromLegacyRates(const [2.0, 6.0]),
          [const SlewRateBand(2.0, 6.0)]);
      expect(slewRateBandsFromLegacyRates(const [6.0, 1.0, 4.0]), [
        const SlewRateBand(1.0, 1.0),
        const SlewRateBand(4.0, 4.0),
        const SlewRateBand(6.0, 6.0),
      ]);
      expect(slewRateBandsFromLegacyRates(const [0.0, -1.0]), isEmpty);
    });
  });
}
