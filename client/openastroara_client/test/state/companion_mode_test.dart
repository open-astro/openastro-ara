import 'dart:ui' show Size;

import 'package:flutter/foundation.dart' show TargetPlatform;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';

class _RecordingPrefs extends SimpleModePrefsService {
  _RecordingPrefs(this.value);
  bool value;
  final saves = <bool>[];
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async {
    saves.add(enabled);
    value = enabled;
  }
}

ProviderContainer _container(
  TargetPlatform p,
  double shortest, {
  bool feature = false,
  bool simple = false,
  _RecordingPrefs? featurePrefs,
  _RecordingPrefs? simplePrefs,
}) {
  final c = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(p),
      companionShortestSideProvider.overrideWithValue(shortest),
      simpleModeFeaturePrefsProvider.overrideWithValue(
        featurePrefs ?? _RecordingPrefs(feature),
      ),
      simpleModePrefsProvider.overrideWithValue(
        simplePrefs ?? _RecordingPrefs(simple),
      ),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('companionModeProvider', () {
    // shortest side: 390 phone, 599.9 just under, 600 the tablet edge, 768 tablet.
    const expected = {
      TargetPlatform.iOS: true,
      TargetPlatform.android: true,
      TargetPlatform.macOS: false,
      TargetPlatform.windows: false,
      TargetPlatform.linux: false,
      TargetPlatform.fuchsia: false,
    };
    for (final e in expected.entries) {
      for (final side in [390.0, 599.9, 600.0, 768.0]) {
        test('${e.key.name} at shortest side $side', () {
          final c = _container(e.key, side);
          expect(c.read(companionModeProvider), e.value && side < 600);
        });
      }
    }
  });

  test(
    'Companion Mode forces Simple Mode on and writes neither preference',
    () async {
      final feature = _RecordingPrefs(false);
      final simple = _RecordingPrefs(false);
      final c = _container(
        TargetPlatform.iOS,
        390,
        featurePrefs: feature,
        simplePrefs: simple,
      );
      await c.read(simpleModeFeatureProvider.future);
      await c.read(simpleModeProvider.future);
      expect(c.read(simpleModeActiveProvider), isTrue);
      expect(feature.saves, isEmpty);
      expect(simple.saves, isEmpty);
      expect(c.read(simpleModeProvider).value, isFalse);
      expect(c.read(simpleModeFeatureProvider).value, isFalse);
    },
  );

  test('outside Companion Mode the saved prefs decide', () async {
    final c = _container(TargetPlatform.macOS, 390);
    await c.read(simpleModeFeatureProvider.future);
    await c.read(simpleModeProvider.future);
    expect(c.read(simpleModeActiveProvider), isFalse);
  });

  group('companionShortestSideProvider reads the live view', () {
    Future<ProviderContainer> pump(WidgetTester t, Size physical) async {
      t.view.devicePixelRatio = 1;
      t.view.physicalSize = physical;
      addTearDown(t.view.reset);
      final c = ProviderContainer(
        overrides: [
          companionPlatformProvider.overrideWithValue(TargetPlatform.android),
        ],
      );
      addTearDown(c.dispose);
      return c;
    }

    testWidgets('a zero size is not a phone', (t) async {
      final c = await pump(t, Size.zero);
      expect(c.read(companionShortestSideProvider), double.infinity);
      expect(c.read(companionModeProvider), isFalse);
    });

    testWidgets('a size change flips the provider', (t) async {
      final c = await pump(t, const Size(390, 844));
      expect(c.read(companionModeProvider), isTrue);
      t.view.physicalSize = const Size(1200, 800);
      await t.pump();
      expect(c.read(companionModeProvider), isFalse);
      t.view.physicalSize = const Size(0, 0);
      await t.pump();
      expect(c.read(companionModeProvider), isFalse);
      t.view.physicalSize = const Size(800, 390);
      await t.pump();
      expect(c.read(companionModeProvider), isTrue);
    });
  });
}
