import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

void main() {
  group('OpticsSettings geometry', () {
    test('an unset profile is not configured and yields null geometry', () {
      const o = OpticsSettings();
      expect(o.isConfigured, isFalse);
      expect(o.pixelScaleArcsecPerPx, isNull);
      expect(o.fovWidthArcmin, isNull);
      expect(o.fovHeightArcmin, isNull);
    });

    test('pixel scale + FOV match the 206.265 formula (ASI2600 @ 1000mm)', () {
      // 3.76µm pixels, 6248×4176 sensor, 1000mm f/l, no reducer.
      const o = OpticsSettings(
        focalLengthMm: 1000,
        reducerFactor: 1.0,
        sensorWidthPx: 6248,
        sensorHeightPx: 4176,
        pixelSizeUm: 3.76,
      );
      expect(o.isConfigured, isTrue);
      expect(o.pixelScaleArcsecPerPx, closeTo(0.77556, 1e-4));
      expect(o.fovWidthArcmin, closeTo(80.764, 1e-2));
      expect(o.fovHeightArcmin, closeTo(53.984, 1e-2));
    });

    test('a reducer shortens the effective focal length and widens the FOV', () {
      const base = OpticsSettings(
        focalLengthMm: 1000,
        sensorWidthPx: 6248,
        sensorHeightPx: 4176,
        pixelSizeUm: 3.76,
      );
      final reduced = base.copyWith(reducerFactor: 0.8);
      expect(reduced.effectiveFocalLengthMm, 800);
      // Scale and FOV grow by exactly 1/0.8 = 1.25×.
      expect(reduced.pixelScaleArcsecPerPx! / base.pixelScaleArcsecPerPx!,
          closeTo(1.25, 1e-9));
      expect(reduced.fovWidthArcmin! / base.fovWidthArcmin!, closeTo(1.25, 1e-9));
    });

    test('any zero input (except the 1.0 reducer default) leaves it unconfigured', () {
      const noFocal = OpticsSettings(sensorWidthPx: 100, sensorHeightPx: 100, pixelSizeUm: 3);
      const noPixel = OpticsSettings(focalLengthMm: 500, sensorWidthPx: 100, sensorHeightPx: 100);
      const noSensor = OpticsSettings(focalLengthMm: 500, pixelSizeUm: 3);
      expect(noFocal.isConfigured, isFalse);
      expect(noPixel.isConfigured, isFalse);
      expect(noSensor.isConfigured, isFalse);
    });
  });

  group('OpticsSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('starts unset (reducer defaults to 1.0)', () {
      final o = container.read(opticsSettingsProvider);
      expect(o.focalLengthMm, 0);
      expect(o.reducerFactor, 1.0);
      expect(o.sensorWidthPx, 0);
      expect(o.isConfigured, isFalse);
    });

    test('setReducerFactor rejects zero/negative (it divides the scale)', () {
      final n = container.read(opticsSettingsProvider.notifier);
      n.setReducerFactor(0);
      n.setReducerFactor(-1);
      expect(container.read(opticsSettingsProvider).reducerFactor, 1.0);
      n.setReducerFactor(0.8);
      expect(container.read(opticsSettingsProvider).reducerFactor, 0.8);
    });

    test('numeric setters reject negatives but accept zero (unset)', () {
      final n = container.read(opticsSettingsProvider.notifier);
      n.setFocalLengthMm(-5);
      n.setPixelSizeUm(-1);
      n.setSensorWidthPx(-10);
      final after = container.read(opticsSettingsProvider);
      expect(after.focalLengthMm, 0);
      expect(after.pixelSizeUm, 0);
      expect(after.sensorWidthPx, 0);
      n.setFocalLengthMm(530);
      expect(container.read(opticsSettingsProvider).focalLengthMm, 530);
    });
  });
}
