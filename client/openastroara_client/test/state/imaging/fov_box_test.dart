import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/imaging/fov_box.dart';
import 'package:openastroara/state/imaging/framing_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

void main() {
  group('frameFovBoxProvider', () {
    late ProviderContainer c;
    setUp(() => c = ProviderContainer());
    tearDown(() => c.dispose());

    // ASI2600 @ 1000mm: FOV ≈ 80.76 × 53.98 arcmin → ≈ 1.346 × 0.900 deg.
    void configureOptics() {
      final n = c.read(opticsSettingsProvider.notifier);
      n.setFocalLengthMm(1000);
      n.setSensorWidthPx(6248);
      n.setSensorHeightPx(4176);
      n.setPixelSizeUm(3.76);
    }

    test('null when Frame mode is off, even with optics configured', () {
      configureOptics();
      expect(c.read(frameFovBoxProvider), isNull);
    });

    test('null when Frame mode is on but optics are unset', () {
      c.read(frameModeEnabledProvider.notifier).set(true);
      expect(c.read(frameFovBoxProvider), isNull);
    });

    test('a configured rig in Frame mode yields the FOV box in degrees', () {
      c.read(frameModeEnabledProvider.notifier).set(true);
      configureOptics();
      final box = c.read(frameFovBoxProvider);
      expect(box, isNotNull);
      expect(box!.widthDeg, closeTo(1.346, 1e-3));
      expect(box.heightDeg, closeTo(0.900, 1e-3));
      expect(box.rotationDeg, 0);
    });

    test('rotation flows from the framing controller', () {
      c.read(frameModeEnabledProvider.notifier).set(true);
      configureOptics();
      c.read(framingControllerProvider.notifier).setRotationDeg(45);
      expect(c.read(frameFovBoxProvider)!.rotationDeg, 45);
    });

    test('turning Frame mode back off clears the box', () {
      final notifier = c.read(frameModeEnabledProvider.notifier);
      configureOptics();
      notifier.set(true);
      expect(c.read(frameFovBoxProvider), isNotNull);
      notifier.set(false);
      expect(c.read(frameFovBoxProvider), isNull);
    });
  });

  group('FovBox', () {
    test('value-equality (drives the redundant-redraw guard)', () {
      const a = FovBox(widthDeg: 1, heightDeg: 2, rotationDeg: 3);
      const b = FovBox(widthDeg: 1, heightDeg: 2, rotationDeg: 3);
      const different = FovBox(widthDeg: 1, heightDeg: 2, rotationDeg: 4);
      expect(a, b);
      expect(a.hashCode, b.hashCode);
      expect(a == different, isFalse);
    });
  });
}
