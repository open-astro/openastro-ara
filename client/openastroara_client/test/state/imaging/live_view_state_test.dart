import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/live_view_state.dart';

void main() {
  group('LiveViewController', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to false', () {
      expect(container.read(liveViewControllerProvider), isFalse);
    });

    test('toggle flips the bit', () {
      final n = container.read(liveViewControllerProvider.notifier);
      n.toggle();
      expect(container.read(liveViewControllerProvider), isTrue);
      n.toggle();
      expect(container.read(liveViewControllerProvider), isFalse);
    });

    test('set assigns directly', () {
      final n = container.read(liveViewControllerProvider.notifier);
      n.set(true);
      expect(container.read(liveViewControllerProvider), isTrue);
      n.set(false);
      expect(container.read(liveViewControllerProvider), isFalse);
    });
  });
}
