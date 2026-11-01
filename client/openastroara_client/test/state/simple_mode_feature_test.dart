import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/experimental_panel.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/simple_mode_switch_button.dart';

class _MemPrefs extends SimpleModePrefsService {
  _MemPrefs(this.value);
  bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

ProviderContainer _container({required bool feature, required bool simple}) {
  final c = ProviderContainer(overrides: [
    simpleModeFeaturePrefsProvider.overrideWithValue(_MemPrefs(feature)),
    simpleModePrefsProvider.overrideWithValue(_MemPrefs(simple)),
  ]);
  addTearDown(c.dispose);
  return c;
}

Future<void> _settle(ProviderContainer c) async {
  await c.read(simpleModeFeatureProvider.future);
  await c.read(simpleModeProvider.future);
}

void main() {
  test('Simple Mode shows only while its experimental toggle is on', () async {
    final off = _container(feature: false, simple: true);
    await _settle(off);
    expect(off.read(simpleModeActiveProvider), isFalse,
        reason: 'a saved Simple Mode choice does nothing with the feature off');

    final on = _container(feature: true, simple: true);
    await _settle(on);
    expect(on.read(simpleModeActiveProvider), isTrue);

    await on.read(simpleModeFeatureProvider.notifier).set(false);
    expect(on.read(simpleModeActiveProvider), isFalse);
    expect(on.read(simpleModeProvider).value, isTrue,
        reason: 'turning the feature off keeps the mode choice for later');
  });

  test('the toggle is saved in its own file, off by default', () async {
    final dir = await Directory.systemTemp.createTemp('simple_feature');
    addTearDown(() => dir.delete(recursive: true));
    final feature = SimpleModePrefsService(
        supportDir: () async => dir, fileName: 'simple_mode_feature.json');
    final mode = SimpleModePrefsService(supportDir: () async => dir);
    expect(await feature.load(), isFalse);
    await feature.save(true);
    expect(await feature.load(), isTrue);
    expect(await mode.load(), isFalse, reason: 'separate from the mode pref');
  });

  testWidgets('the top-bar button appears only once the Experimental switch is on',
      (tester) async {
    final c = _container(feature: false, simple: false);
    await _settle(c);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(
        home: Scaffold(
          body: Column(children: [
            SimpleModeSwitchButton(),
            Expanded(child: ExperimentalPanel()),
          ]),
        ),
      ),
    ));
    expect(find.byTooltip('Simple Mode'), findsNothing);

    await tester.tap(find.byType(Switch));
    await tester.pump();
    expect(c.read(simpleModeFeatureProvider).value, isTrue);
    expect(find.byTooltip('Simple Mode'), findsOneWidget);
  });
}
