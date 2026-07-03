import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/library/library_state.dart';

void main() {
  group('LibraryGroupingNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to bySession', () {
      expect(container.read(libraryGroupingProvider), LibraryGrouping.bySession);
    });

    test('set switches grouping', () {
      final n = container.read(libraryGroupingProvider.notifier);
      n.set(LibraryGrouping.byTarget);
      expect(container.read(libraryGroupingProvider), LibraryGrouping.byTarget);
      n.set(LibraryGrouping.byDate);
      expect(container.read(libraryGroupingProvider), LibraryGrouping.byDate);
      n.set(LibraryGrouping.bySession);
      expect(container.read(libraryGroupingProvider), LibraryGrouping.bySession);
    });
  });

}
