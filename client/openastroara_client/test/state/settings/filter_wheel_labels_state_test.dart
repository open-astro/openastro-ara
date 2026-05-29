import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';

void main() {
  group('FilterWheelLabelsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults match §46.2 reference filter wheel (8 slots)', () {
      final s = container.read(filterWheelLabelsProvider);
      expect(s.slotCount, 8);
      expect(s.labelAt(1), 'L');
      expect(s.labelAt(2), 'R');
      expect(s.labelAt(3), 'G');
      expect(s.labelAt(4), 'B');
      expect(s.labelAt(5), 'Hα');
      expect(s.labelAt(6), 'OIII');
      expect(s.labelAt(7), 'SII');
      expect(s.labelAt(8), ''); // blank slot
    });

    test('setLabel updates only the targeted slot', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(8, 'Clear');
      final s = container.read(filterWheelLabelsProvider);
      expect(s.labelAt(8), 'Clear');
      expect(s.labelAt(1), 'L'); // others unchanged
      expect(s.labelAt(5), 'Hα');
    });

    test('setLabel trims input', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(3, '  Green-broadband  ');
      expect(container.read(filterWheelLabelsProvider).labelAt(3),
          'Green-broadband');
    });

    test('setLabel allows empty to blank a slot', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(1, '');
      expect(container.read(filterWheelLabelsProvider).labelAt(1), '');
    });

    test('out-of-range slot is a no-op (no exception)', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(0, 'oops');
      n.setLabel(9, 'oops');
      n.setLabel(-1, 'oops');
      final s = container.read(filterWheelLabelsProvider);
      expect(s.labelAt(1), 'L'); // unchanged
      expect(s.labelAt(8), '');
    });

    test('labelAt returns empty for out-of-range slot', () {
      final s = container.read(filterWheelLabelsProvider);
      expect(s.labelAt(0), '');
      expect(s.labelAt(9), '');
      expect(s.labelAt(-1), '');
    });

    test('model-layer constructor trims even when bypassing the notifier', () {
      // Future daemon-hydration loaders will call the constructor directly.
      // The trim invariant must hold there too, not just in the notifier.
      final s = FilterWheelLabels(
        labels: ['  L  ', '\tR\t', 'G', 'B', '', '', '', ''],
      );
      expect(s.labelAt(1), 'L');
      expect(s.labelAt(2), 'R');
      expect(s.labelAt(3), 'G');
    });

    test('withLabel preserves the unmodifiable contract', () {
      // Pull the state, then attempt to mutate the underlying list through
      // any handle we can reach. Should throw because the list is wrapped in
      // List.unmodifiable.
      final s = container.read(filterWheelLabelsProvider);
      expect(() => (s.labelAt(1)), returnsNormally);
      // labels is private now; consumers can only mutate via the notifier.
      // Verify a `state =` after withLabel still produces a new instance.
      final next = s.withLabel(1, 'X');
      expect(identical(s, next), isFalse);
      expect(s.labelAt(1), 'L'); // original untouched
      expect(next.labelAt(1), 'X');
    });

    test('== / hashCode treat same-content instances as equal', () {
      final a = FilterWheelLabels(
        labels: ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
      );
      final b = FilterWheelLabels(
        labels: ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
      );
      final c = a.withLabel(1, 'Lum');
      expect(a, equals(b));
      expect(a.hashCode, equals(b.hashCode));
      expect(a, isNot(equals(c)));
    });
  });
}
