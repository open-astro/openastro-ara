import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/storage_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

import '../../support/storage_fixture.dart';

Future<ProviderContainer> _pump(WidgetTester tester, FakeStorageApi api) async {
  tester.view.physicalSize = const Size(1400, 4000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final prior = FlutterError.onError;
  FlutterError.onError = (d) {
    if (!d.exceptionAsString().contains('overflowed')) prior?.call(d);
  };
  addTearDown(() => FlutterError.onError = prior);
  final container = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(api)],
  );
  addTearDown(container.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: StoragePanel())),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

void main() {
  testWidgets(
    'unreachable rig: Storage says not loaded, offers Retry, shows no fields',
    (tester) async {
      final api = FakeStorageApi()..failGet = Exception('rig unreachable');
      await _pump(tester, api);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(find.text('File format'), findsNothing);
      api.failGet = null;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('section-retry')), findsNothing);
      expect(find.text('Advanced'), findsOneWidget);
    },
  );

  testWidgets('412 on Save keeps the edits and offers Reload', (tester) async {
    final api = FakeStorageApi();
    final container = await _pump(tester, api);
    container
        .read(storageSettingsProvider.notifier)
        .setSaveDirectory('/mnt/usb');
    api.etag = '"newer"';
    await container.read(panelSaveActionProvider)!();
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsOneWidget);
    expect(
      container.read(storageSettingsProvider).value!.saveDirectory,
      '/mnt/usb',
    );
    expect(api.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('section-reload')), findsNothing);
    expect(container.read(storageSettingsProvider).value, kTestStorage);
  });

  testWidgets('Save with the Storage load failed writes nothing (#1807)', (
    tester,
  ) async {
    final api = FakeStorageApi()..failGet = Exception('rig unreachable');
    final container = await _pump(tester, api);
    await container.read(panelSaveActionProvider)!();
    await tester.pumpAndSettle();
    expect(api.puts, isEmpty);
  });
}
