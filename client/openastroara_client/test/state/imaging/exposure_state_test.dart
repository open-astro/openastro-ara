import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';

void main() {
  group('ExposureController', () {
    late ProviderContainer container;

    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts with sane defaults', () {
      final params = container.read(exposureControllerProvider);
      expect(params.exposure, const Duration(seconds: 5));
      expect(params.gain, 100);
      expect(params.bin, 1);
      expect(params.filterSlot, 'L');
      expect(params.frameKind, FrameKind.light);
    });

    test('setExposure rejects zero or negative durations', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setExposure(Duration.zero);
      notifier.setExposure(const Duration(seconds: -1));
      expect(container.read(exposureControllerProvider).exposure,
          const Duration(seconds: 5));
    });

    test('setExposure accepts a positive duration', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setExposure(const Duration(seconds: 30));
      expect(container.read(exposureControllerProvider).exposure,
          const Duration(seconds: 30));
    });

    test('setGain + setOffset reject negative', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setGain(-1);
      notifier.setOffset(-100);
      expect(container.read(exposureControllerProvider).gain, 100);
      expect(container.read(exposureControllerProvider).offset, 10);
    });

    test('setBin rejects values below 1', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setBin(0);
      notifier.setBin(-1);
      expect(container.read(exposureControllerProvider).bin, 1);
      notifier.setBin(2);
      expect(container.read(exposureControllerProvider).bin, 2);
    });

    test('setFilterSlot rejects empty string', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setFilterSlot('');
      expect(container.read(exposureControllerProvider).filterSlot, 'L');
      notifier.setFilterSlot('Hα');
      expect(container.read(exposureControllerProvider).filterSlot, 'Hα');
    });

    test('setFrameKind accepts any enum value', () {
      final notifier = container.read(exposureControllerProvider.notifier);
      notifier.setFrameKind(FrameKind.dark);
      expect(container.read(exposureControllerProvider).frameKind,
          FrameKind.dark);
    });
  });
}
