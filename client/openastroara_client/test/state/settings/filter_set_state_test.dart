import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';

void main() {
  group('FilterSetNotifier', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    FilterSetNotifier notifier() =>
        container.read(filterSetProvider.notifier);
    List<PlanningFilter> filters() =>
        container.read(filterSetProvider).filters;

    test('starts empty', () {
      expect(filters(), isEmpty);
    });

    test('addFilter trims the name and appends', () {
      notifier().addFilter(
          const PlanningFilter(name: '  Ha 3nm ', kind: FilterKind.ha));
      expect(filters().single.name, 'Ha 3nm');
      expect(filters().single.kind, FilterKind.ha);
    });

    test('addFilter rejects empty and duplicate names (case-insensitive)', () {
      notifier().addFilter(const PlanningFilter(name: 'L', kind: FilterKind.l));
      notifier().addFilter(const PlanningFilter(name: '  ', kind: FilterKind.l));
      notifier()
          .addFilter(const PlanningFilter(name: 'l', kind: FilterKind.osc));
      expect(filters(), hasLength(1),
          reason: 'the daemon rejects duplicates, so the client must too');
    });

    test('updateAt renames but refuses a collision with another entry', () {
      notifier().addFilter(const PlanningFilter(name: 'L', kind: FilterKind.l));
      notifier()
          .addFilter(const PlanningFilter(name: 'Ha', kind: FilterKind.ha));
      notifier().updateAt(
          1, const PlanningFilter(name: 'l', kind: FilterKind.ha));
      expect(filters()[1].name, 'Ha', reason: 'collision with entry 0');
      notifier().updateAt(
          1, const PlanningFilter(name: 'Ha 3nm', kind: FilterKind.ha, bandwidthNm: 3));
      expect(filters()[1].name, 'Ha 3nm');
      expect(filters()[1].bandwidthNm, 3);
      // Renaming an entry to (a case variant of) itself is fine.
      notifier().updateAt(
          0, const PlanningFilter(name: 'l', kind: FilterKind.l));
      expect(filters()[0].name, 'l');
    });

    test('removeAt drops the entry and ignores bad indices', () {
      notifier().addFilter(const PlanningFilter(name: 'L', kind: FilterKind.l));
      notifier().removeAt(5); // out of range → no-op
      expect(filters(), hasLength(1));
      notifier().removeAt(0);
      expect(filters(), isEmpty);
    });

    test('seedFromWheelLabels appends only new labels with guessed kinds', () {
      notifier().addFilter(const PlanningFilter(name: 'L', kind: FilterKind.l));
      notifier().seedFromWheelLabels(
          ['L', 'Red', 'Ha 3nm', 'OIII', 'L-eXtreme', '', 'Ha 3nm']);
      final names = filters().map((f) => f.name).toList();
      expect(names, ['L', 'Red', 'Ha 3nm', 'OIII', 'L-eXtreme']);
      expect(filters()[1].kind, FilterKind.r);
      expect(filters()[2].kind, FilterKind.ha);
      expect(filters()[3].kind, FilterKind.oiii);
      expect(filters()[4].kind, FilterKind.duo);
    });
  });

  group('FilterKind', () {
    test('wire tokens round-trip and unknown falls back to L', () {
      for (final k in FilterKind.values) {
        expect(FilterKind.fromWire(k.wire), k);
      }
      expect(FilterKind.fromWire('nonsense'), FilterKind.l);
      expect(FilterKind.fromWire(null), FilterKind.l);
    });

    test('default bandwidths mirror the daemon table', () {
      // Same values as OptimalSubCalculator.DefaultBandwidthNm — the doc'd
      // 3nm-Ha-vs-100nm-L ≈ 30× sky-flux ratio depends on these.
      expect(FilterKind.l.defaultBandwidthNm, 100);
      expect(FilterKind.osc.defaultBandwidthNm, 100);
      expect(FilterKind.r.defaultBandwidthNm, 80);
      expect(FilterKind.g.defaultBandwidthNm, 80);
      expect(FilterKind.b.defaultBandwidthNm, 80);
      expect(FilterKind.ha.defaultBandwidthNm, 7);
      expect(FilterKind.oiii.defaultBandwidthNm, 7);
      expect(FilterKind.sii.defaultBandwidthNm, 7);
      expect(FilterKind.duo.defaultBandwidthNm, 7);
      expect(FilterKind.tri.defaultBandwidthNm, 8);
    });

    test('guessKind maps common wheel labels', () {
      expect(FilterSetNotifier.guessKind('Ha 3nm'), FilterKind.ha);
      expect(FilterSetNotifier.guessKind('Hα'), FilterKind.ha);
      expect(FilterSetNotifier.guessKind('OIII'), FilterKind.oiii);
      expect(FilterSetNotifier.guessKind('SII'), FilterKind.sii);
      expect(FilterSetNotifier.guessKind('L-eXtreme'), FilterKind.duo);
      // 'enHAnce' contains "ha" — the multi-band check must win over Hα.
      expect(FilterSetNotifier.guessKind('L-eNhance'), FilterKind.tri);
      expect(FilterSetNotifier.guessKind('Red'), FilterKind.r);
      expect(FilterSetNotifier.guessKind('Green'), FilterKind.g);
      expect(FilterSetNotifier.guessKind('Blue'), FilterKind.b);
      expect(FilterSetNotifier.guessKind('Luminance'), FilterKind.l);
      // Token matching: substrings inside ordinary words don't trigger lines.
      expect(FilterSetNotifier.guessKind('Chandra'), FilterKind.l);
    });

    test('recognizedKind covers the AlpacaBridge presets and their aliases',
        () {
      FilterKind? k(String s) => FilterSetNotifier.recognizedKind(s);
      for (final s in ['Luminance', 'L', 'lum']) {
        expect(k(s), FilterKind.l, reason: s);
      }
      expect(k('R'), FilterKind.r);
      expect(k('G'), FilterKind.g);
      expect(k('B'), FilterKind.b);
      for (final s in ['Ha', 'H', 'Hα', 'halpha', 'H-alpha', 'Ha7nm',
          'Antlia Ha 3nm']) {
        expect(k(s), FilterKind.ha, reason: s);
      }
      for (final s in ['OIII', 'O', 'o3', 'O-III']) {
        expect(k(s), FilterKind.oiii, reason: s);
      }
      for (final s in ['SII', 'S', 's2', 'S-II']) {
        expect(k(s), FilterKind.sii, reason: s);
      }
      // No planning kind: Sloan, clear, dark, UV/IR and the bridge's default
      // "Filter N" names. 'UHC-S' must not read its lone S as SII.
      for (final s in ["U'", "G'", "R'", "I'", "Z'", 'Clear', 'CLR', 'Dark',
          'UV', 'IR', 'Filter 1', 'UHC-S', '']) {
        expect(k(s), isNull, reason: s);
      }
    });

    group('FilterSetSettings.lookup', () {
      const set = FilterSetSettings(filters: [
        PlanningFilter(name: 'Luminance', kind: FilterKind.l),
        PlanningFilter(name: 'Red', kind: FilterKind.r),
        PlanningFilter(name: 'Ha', kind: FilterKind.ha),
        PlanningFilter(name: 'Baader OIII 6.5nm', kind: FilterKind.oiii),
        PlanningFilter(name: 'My Filter', kind: FilterKind.l),
      ]);

      test('exact name first, case-insensitive', () {
        expect(set.lookup('ha')?.name, 'Ha');
        expect(set.lookup(' My Filter ')?.name, 'My Filter');
      });

      test('a recognised wheel name finds the one entry of its kind', () {
        expect(set.lookup('Hα')?.name, 'Ha');
        expect(set.lookup('R')?.name, 'Red');
        expect(set.lookup('O3')?.name, 'Baader OIII 6.5nm');
      });

      test('ambiguous or unrecognised names stay unknown', () {
        // Two L-kind entries: 'Lum' could be either.
        expect(set.lookup('Lum'), isNull);
        expect(set.lookup('Filter 7'), isNull);
        expect(set.lookup('SII'), isNull);
      });
    });
  });
}
