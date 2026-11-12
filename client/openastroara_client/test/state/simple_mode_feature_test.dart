import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/experimental_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
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

/// A rig that holds the permanent pier on and refuses to change it.
class _RefusingRig extends ProfileApi {
  _RefusingRig() : super(const AraServer(hostname: '192.0.2.10', port: 5000));
  @override
  Future<SiteSettings> getSiteSettings() async =>
      const SiteSettings(permanentPier: true);
  @override
  Future<SiteSettings> putSiteSettings(SiteSettings value) async {
    final at = RequestOptions(path: '/api/v1/profile/site');
    throw DioException(
      requestOptions: at,
      response: Response(requestOptions: at, statusCode: 409),
      type: DioExceptionType.badResponse,
    );
  }
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

  testWidgets(
    'Experimental: a refused pier switch-off says so and shows the rig again',
    (tester) async {
      final c = ProviderContainer(
        overrides: [
          simpleModeFeaturePrefsProvider.overrideWithValue(_MemPrefs(true)),
          simpleModePrefsProvider.overrideWithValue(_MemPrefs(false)),
          simplePermanentPierPrefsProvider.overrideWithValue(_MemPrefs(false)),
          profileApiProvider.overrideWithValue(_RefusingRig()),
        ],
      );
      addTearDown(c.dispose);
      await _settle(c);
      await c.read(simplePermanentPierProvider.future);
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: c,
          child: const MaterialApp(home: Scaffold(body: ExperimentalPanel())),
        ),
      );
      final pier = find.byKey(const Key('experimental-permanent-pier'));
      expect(tester.widget<SwitchListTile>(pier).value, isTrue);

      await tester.tap(pier);
      await tester.pump();

      expect(tester.widget<SwitchListTile>(pier).value, isTrue);
      expect(
        find.text("Couldn't save that — something else is using it right now."),
        findsOneWidget,
      );
    },
  );
}
