import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/framing_state.dart';

void main() {
  group('FramingController', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults', () {
      final p = container.read(framingControllerProvider);
      expect(p.rotationDeg, 0);
      expect(p.mosaicCols, 1);
      expect(p.mosaicRows, 1);
      expect(p.mosaicOverlapPct, 10);
    });

    test('setRotationDeg updates state', () {
      container.read(framingControllerProvider.notifier).setRotationDeg(42.5);
      expect(container.read(framingControllerProvider).rotationDeg, 42.5);
    });

    test('mosaic params update independently', () {
      final n = container.read(framingControllerProvider.notifier);
      n.setMosaicCols(3);
      n.setMosaicRows(2);
      n.setOverlapPct(15);
      final p = container.read(framingControllerProvider);
      expect(p.mosaicCols, 3);
      expect(p.mosaicRows, 2);
      expect(p.mosaicOverlapPct, 15);
      // Rotation didn't change.
      expect(p.rotationDeg, 0);
    });

    test('copyWith preserves unchanged fields', () {
      const a = FramingParams(rotationDeg: 90, mosaicCols: 4);
      final b = a.copyWith(rotationDeg: 180);
      expect(b.mosaicCols, 4);
      expect(b.rotationDeg, 180);
    });
  });

  group('FrameModeNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to false (Frame toggle off)', () {
      expect(container.read(frameModeEnabledProvider), false);
    });

    test('set updates the flag', () {
      container.read(frameModeEnabledProvider.notifier).set(true);
      expect(container.read(frameModeEnabledProvider), true);
    });

    test('toggle flips the flag', () {
      final n = container.read(frameModeEnabledProvider.notifier);
      n.toggle();
      expect(container.read(frameModeEnabledProvider), true);
      n.toggle();
      expect(container.read(frameModeEnabledProvider), false);
    });
  });
}
