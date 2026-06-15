import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/app_shell_state.dart';

void main() {
  group('SelectedTabIndexNotifier', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to 0 (Imaging)', () {
      expect(container.read(selectedTabIndexProvider), 0);
    });

    test('select accepts valid indices 0..3', () {
      final notifier = container.read(selectedTabIndexProvider.notifier);
      for (var i = 0; i < 4; i++) {
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
      // Out of upper bound (only 4 tabs after the Planning merge — §36/§25.5).
      notifier.select(4);
      expect(container.read(selectedTabIndexProvider), 2);
      notifier.select(100);
      expect(container.read(selectedTabIndexProvider), 2);
    });
  });
}
