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

  group('librarySessionsProvider', () {
    test('demo data has 7 sessions, all populated', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      final sessions = container.read(librarySessionsProvider);
      expect(sessions.length, 7);
      for (final s in sessions) {
        expect(s.id.isNotEmpty, isTrue, reason: 'id missing');
        expect(s.targetName.isNotEmpty, isTrue, reason: 'target missing');
        expect(s.frames, isNotEmpty,
            reason: '${s.id} has no frames');
      }
    });
  });
}
