import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/section_load.dart';

void main() {
  group('FilterWheelLabelsNotifier', () {
    late ProviderContainer container;
    setUp(() async {
      container = ProviderContainer();
      await loadSection(
        container,
        filterWheelLabelsProvider,
        'filter-wheel/labels',
        {
          'labels': ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
        },
      );
    });
    tearDown(() => container.dispose());

    test('a reference filter wheel from the rig (§46.2) loads as 8 slots', () {
      final s = container.read(filterWheelLabelsProvider).value!;
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
      final s = container.read(filterWheelLabelsProvider).value!;
      expect(s.labelAt(8), 'Clear');
      expect(s.labelAt(1), 'L'); // others unchanged
      expect(s.labelAt(5), 'Hα');
    });

    test('setLabel trims input', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(3, '  Green-broadband  ');
      expect(
        container.read(filterWheelLabelsProvider).value!.labelAt(3),
        'Green-broadband',
      );
    });

    test('setLabel allows empty to blank a slot', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(1, '');
      expect(container.read(filterWheelLabelsProvider).value!.labelAt(1), '');
    });

    test('out-of-range slot is a no-op (no exception)', () {
      final n = container.read(filterWheelLabelsProvider.notifier);
      n.setLabel(0, 'oops');
      n.setLabel(9, 'oops');
      n.setLabel(-1, 'oops');
      final s = container.read(filterWheelLabelsProvider).value!;
      expect(s.labelAt(1), 'L'); // unchanged
      expect(s.labelAt(8), '');
    });

    test('labelAt returns empty for out-of-range slot', () {
      final s = container.read(filterWheelLabelsProvider).value!;
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
      final s = container.read(filterWheelLabelsProvider).value!;
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

  group('daemon round-trip (12h.2b)', () {
    ProviderContainer containerFor(FakeSectionRig? rig) {
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig?.api())],
      );
      addTearDown(c.dispose);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      return c;
    }

    test('self-hydrates from the daemon when a server is active', () async {
      final c = containerFor(
        FakeSectionRig({
          'filter-wheel/labels': {
            'labels': ['Ha', 'OIII', 'SII', ''],
          },
        }),
      );
      // Nothing to show until the rig answers: no reference defaults.
      expect(c.read(filterWheelLabelsProvider).value, isNull);
      await settle(() => c.read(filterWheelLabelsProvider).canSave);
      final labels = c.read(filterWheelLabelsProvider).value!;
      expect(labels.slotCount, 4);
      expect(labels.labelAt(1), 'Ha');
    });

    test(
      'a failed hydration stays not loaded, with the failure kept',
      () async {
        final rig = FakeSectionRig({
          'filter-wheel/labels': {'labels': []},
        })..failGet.add('filter-wheel/labels');
        final c = containerFor(rig);
        await settle(
          () => c.read(filterWheelLabelsProvider).lastFailure != null,
        );
        expect(c.read(filterWheelLabelsProvider).value, isNull);
        expect(c.read(filterWheelLabelsProvider).canSave, isFalse);
      },
    );

    test('no active server -> nothing loads', () async {
      final c = containerFor(null);
      await Future<void>.delayed(Duration.zero);
      expect(c.read(filterWheelLabelsProvider).value, isNull);
    });

    test(
      'persistToServer sends the edited labels and adopts the echo',
      () async {
        final rig = FakeSectionRig({
          'filter-wheel/labels': {
            'labels': ['L', 'R'],
          },
        });
        final c = containerFor(rig);
        await settle(() => c.read(filterWheelLabelsProvider).canSave);
        final n = c.read(filterWheelLabelsProvider.notifier);

        n.setLabel(2, 'R2');
        await n.persistToServer();

        expect(rig.puts.single.body, {
          'labels': ['L', 'R2'],
        }, reason: 'the edited state is what goes up');
        expect(c.read(filterWheelLabelsProvider).value!.labelAt(2), 'R2');
      },
    );

    test(
      'persistToServer without a server throws for the panel to surface',
      () {
        final c = containerFor(null);
        expect(
          () => c.read(filterWheelLabelsProvider.notifier).persistToServer(),
          throwsStateError,
        );
      },
    );
  });
}
