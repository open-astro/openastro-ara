import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/library/library_selection.dart';

void main() {
  group('LibrarySelectionNotifier', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts empty', () {
      expect(container.read(librarySelectionProvider), isEmpty);
      expect(container.read(librarySelectionActiveProvider), isFalse);
    });

    test('toggle adds then removes', () {
      final notifier = container.read(librarySelectionProvider.notifier);
      notifier.toggle('frame-1');
      expect(container.read(librarySelectionProvider), {'frame-1'});
      expect(container.read(librarySelectionActiveProvider), isTrue);

      notifier.toggle('frame-1');
      expect(container.read(librarySelectionProvider), isEmpty);
      expect(container.read(librarySelectionActiveProvider), isFalse);
    });

    test('toggle of multiple ids accumulates', () {
      final notifier = container.read(librarySelectionProvider.notifier);
      notifier.toggle('a');
      notifier.toggle('b');
      notifier.toggle('c');
      expect(container.read(librarySelectionProvider), {'a', 'b', 'c'});
    });

    test('clear drops everything', () {
      final notifier = container.read(librarySelectionProvider.notifier);
      notifier.toggle('a');
      notifier.toggle('b');
      notifier.clear();
      expect(container.read(librarySelectionProvider), isEmpty);
    });

    test('clear on empty is a no-op (does not produce a new state)', () {
      final notifier = container.read(librarySelectionProvider.notifier);
      final initial = container.read(librarySelectionProvider);
      notifier.clear();
      // identity-equal because we guarded the no-op path
      expect(identical(container.read(librarySelectionProvider), initial),
          isTrue);
    });

    test('contains reports membership correctly', () {
      final notifier = container.read(librarySelectionProvider.notifier);
      notifier.toggle('only-this');
      expect(notifier.contains('only-this'), isTrue);
      expect(notifier.contains('other'), isFalse);
    });
  });
}
