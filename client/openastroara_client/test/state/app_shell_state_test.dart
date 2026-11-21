import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/app_shell_state.dart';

void main() {
  group('SelectedTabIndexNotifier', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to 0 (Planning)', () {
      expect(container.read(selectedTabIndexProvider), 0);
    });

    test('select accepts valid indices 0..4', () {
      final notifier = container.read(selectedTabIndexProvider.notifier);
      for (var i = 0; i < 5; i++) {
        notifier.select(i);
        expect(container.read(selectedTabIndexProvider), i,
            reason: 'failed for index $i');
      }
    });

    test('select rejects out-of-range indices', () {
      final notifier = container.read(selectedTabIndexProvider.notifier);
      notifier.select(2);
      // Negative.
      notifier.select(-1);
      expect(container.read(selectedTabIndexProvider), 2);
      // Out of upper bound (5 tabs: Planning/Setup/Run/Live/Options — Support
      // folded into the Options settings tree).
      notifier.select(5);
      expect(container.read(selectedTabIndexProvider), 2);
      notifier.select(100);
      expect(container.read(selectedTabIndexProvider), 2);
    });
  });

  group('visitedTabsProvider (#1111 PR C)', () {
    test('starts with the selected tab and grows with select()', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      final sub = container.listen(visitedTabsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(container.read(visitedTabsProvider), {0});

      container.read(selectedTabIndexProvider.notifier).select(3);
      expect(container.read(visitedTabsProvider), {0, 3});
      container.read(selectedTabIndexProvider.notifier).select(1);
      expect(container.read(visitedTabsProvider), {0, 1, 3});
    });

    test('a repeat select is a no-op for the set (same instance)', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      final sub = container.listen(visitedTabsProvider, (_, _) {});
      addTearDown(sub.close);
      container.read(selectedTabIndexProvider.notifier).select(2);
      final before = container.read(visitedTabsProvider);
      container.read(selectedTabIndexProvider.notifier).select(2);
      expect(identical(container.read(visitedTabsProvider), before), isTrue);
    });

    test('an out-of-range select marks nothing', () {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      final sub = container.listen(visitedTabsProvider, (_, _) {});
      addTearDown(sub.close);
      container.read(selectedTabIndexProvider.notifier).select(7);
      expect(container.read(visitedTabsProvider), {0});
    });

    test('resets once nothing listens, seeded from the current tab', () async {
      final container = ProviderContainer();
      addTearDown(container.dispose);
      var sub = container.listen(visitedTabsProvider, (_, _) {});
      container.read(selectedTabIndexProvider.notifier).select(4);
      expect(container.read(visitedTabsProvider), {0, 4});
      sub.close();
      // autoDispose tears the set down on the next event-loop turn.
      await Future<void>.delayed(Duration.zero);
      sub = container.listen(visitedTabsProvider, (_, _) {});
      addTearDown(sub.close);
      expect(container.read(visitedTabsProvider), {4},
          reason: 'a fresh shell starts lazy, on whichever tab is current');
    });
  });
}
