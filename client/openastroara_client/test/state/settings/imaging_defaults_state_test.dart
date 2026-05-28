import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';

void main() {
  group('ImagingDefaultsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts with sane defaults', () {
      final d = container.read(imagingDefaultsProvider);
      expect(d.defaultExposure, const Duration(seconds: 5));
      expect(d.defaultGain, 100);
      expect(d.defaultOffset, 50);
      expect(d.defaultBin, 1);
      expect(d.defaultFrameKind, FrameKind.light);
      expect(d.coolerTargetC, -10);
      expect(d.coolerRampRatePerMin, 1.0);
      expect(d.warmupAtSessionEnd, isFalse);
    });

    test('setExposure rejects zero or negative', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setExposure(Duration.zero);
      n.setExposure(const Duration(seconds: -1));
      expect(container.read(imagingDefaultsProvider).defaultExposure,
          const Duration(seconds: 5));
      n.setExposure(const Duration(seconds: 30));
      expect(container.read(imagingDefaultsProvider).defaultExposure,
          const Duration(seconds: 30));
    });

    test('setGain + setOffset reject negative', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setGain(-1);
      n.setOffset(-1);
      expect(container.read(imagingDefaultsProvider).defaultGain, 100);
      expect(container.read(imagingDefaultsProvider).defaultOffset, 50);
      n.setGain(200);
      n.setOffset(100);
      expect(container.read(imagingDefaultsProvider).defaultGain, 200);
      expect(container.read(imagingDefaultsProvider).defaultOffset, 100);
    });

    test('setBin rejects values below 1', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setBin(0);
      n.setBin(-2);
      expect(container.read(imagingDefaultsProvider).defaultBin, 1);
      n.setBin(2);
      expect(container.read(imagingDefaultsProvider).defaultBin, 2);
    });

    test('setCoolerTargetC clamps to physical range', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setCoolerTargetC(-100); // too cold
      expect(container.read(imagingDefaultsProvider).coolerTargetC, -10);
      n.setCoolerTargetC(100); // too hot
      expect(container.read(imagingDefaultsProvider).coolerTargetC, -10);
      n.setCoolerTargetC(-20);
      expect(container.read(imagingDefaultsProvider).coolerTargetC, -20);
    });

    test('setCoolerRampRate rejects non-positive + extreme', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setCoolerRampRate(0);
      n.setCoolerRampRate(-1);
      n.setCoolerRampRate(100);
      expect(container.read(imagingDefaultsProvider).coolerRampRatePerMin,
          1.0);
      n.setCoolerRampRate(2.5);
      expect(container.read(imagingDefaultsProvider).coolerRampRatePerMin,
          2.5);
    });

    test('setFrameKind + setWarmupAtSessionEnd assign directly', () {
      final n = container.read(imagingDefaultsProvider.notifier);
      n.setFrameKind(FrameKind.dark);
      n.setWarmupAtSessionEnd(true);
      expect(container.read(imagingDefaultsProvider).defaultFrameKind,
          FrameKind.dark);
      expect(container.read(imagingDefaultsProvider).warmupAtSessionEnd,
          isTrue);
    });

    test('copyWith preserves unchanged fields', () {
      const d = ImagingDefaults(defaultGain: 200, defaultBin: 2);
      final updated = d.copyWith(defaultGain: 300);
      expect(updated.defaultGain, 300);
      expect(updated.defaultBin, 2);
    });
  });
}
