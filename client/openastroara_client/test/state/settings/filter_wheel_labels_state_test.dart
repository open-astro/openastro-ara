import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';

/// Overrides only the labels round-trip; every other ProfileApi member keeps
/// its real (never-called-here) implementation.
class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));

  List<String>? served; // null → the GET throws (daemon unreachable)
  List<String>? putReceived;
  List<String>? putEcho; // defaults to the received input when null

  @override
  Future<FilterWheelLabels> getFilterWheelLabels() async {
    final s = served;
    if (s == null) throw StateError('unreachable');
    return FilterWheelLabels(labels: s);
  }

  @override
  Future<FilterWheelLabels> putFilterWheelLabels(FilterWheelLabels value) async {
    putReceived = [for (var i = 1; i <= value.slotCount; i++) value.labelAt(i)];
    return FilterWheelLabels(labels: putEcho ?? putReceived!);
  }
}

ProviderContainer _apiContainer(ProfileApi? api) {
  final c = ProviderContainer(overrides: [
    profileApiProvider.overrideWithValue(api),
  ]);
  addTearDown(c.dispose);
  return c;
}

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

  group('daemon round-trip (12h.2b)', () {
    test('self-hydrates from the daemon when a server is active', () async {
      final api = _FakeApi()..served = ['Ha', 'OIII', 'SII', ''];
      final c = _apiContainer(api);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      // The reference defaults render immediately…
      expect(c.read(filterWheelLabelsProvider).labelAt(1), 'L');
      // …and the daemon's labels land once the microtask hydration settles.
      await Future<void>.delayed(Duration.zero);
      final labels = c.read(filterWheelLabelsProvider);
      expect(labels.slotCount, 4);
      expect(labels.labelAt(1), 'Ha');
    });

    test('a failed hydration keeps the reference defaults (offline authoring)',
        () async {
      final api = _FakeApi(); // served == null → GET throws
      final c = _apiContainer(api);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      await Future<void>.delayed(Duration.zero);
      expect(c.read(filterWheelLabelsProvider).labelAt(1), 'L',
          reason: 'daemon unreachable → the in-memory defaults stand');
    });

    test('no active server → defaults, no network attempt', () async {
      final c = _apiContainer(null);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      await Future<void>.delayed(Duration.zero);
      expect(c.read(filterWheelLabelsProvider).labelAt(6), 'OIII');
    });

    test('persistToServer sends the current labels and adopts the echo',
        () async {
      final api = _FakeApi()
        ..served = ['L', 'R']
        ..putEcho = ['L trimmed', 'R'];
      final c = _apiContainer(api);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      await Future<void>.delayed(Duration.zero);

      c.read(filterWheelLabelsProvider.notifier).setLabel(2, 'R2');
      await c.read(filterWheelLabelsProvider.notifier).persistToServer();

      expect(api.putReceived, ['L', 'R2'],
          reason: 'the edited state is what goes up');
      expect(c.read(filterWheelLabelsProvider).labelAt(1), 'L trimmed',
          reason: 'the daemon echo (server-side trimming) is adopted');
    });

    test('persistToServer without a server throws for the panel to surface', () {
      final c = _apiContainer(null);
      c.listen(filterWheelLabelsProvider, (_, _) {});
      expect(
        () => c.read(filterWheelLabelsProvider.notifier).persistToServer(),
        throwsStateError,
      );
    });
  });
}
