import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/session_filenames_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

import '../../../support/storage_fixture.dart';

/// The rig behind File naming: Storage as [FakeStorageApi] serves it, and the
/// other three sections the panel reads, whose filenames GET can be failed.
class _FakeRig extends FakeStorageApi {
  Object? failFilenames;

  @override
  Future<FilenamesSettings> getFilenamesSettings() async {
    if (failFilenames != null) throw failFilenames!;
    return const FilenamesSettings();
  }

  @override
  Future<SiteSettings> getSiteSettings() async => const SiteSettings();

  @override
  Future<OpticsSettings> getOptics() async => const OpticsSettings();
}

Future<ProviderContainer> _pump(WidgetTester tester, _FakeRig api) async {
  tester.view.physicalSize = const Size(1400, 4000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  // Fixed-width rows overflow under the test font; layout is not the subject.
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
      child: const MaterialApp(home: Scaffold(body: SessionFilenamesPanel())),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

void main() {
  testWidgets(
    'unreachable rig: File naming says not loaded, not loading, and keeps the load error',
    (tester) async {
      final api = _FakeRig()
        ..failFilenames = StateError('Filenames did not answer.')
        ..failGet = StateError('Storage did not answer.');
      await _pump(tester, api);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(find.textContaining('Loading'), findsNothing);
      // Storage answers on Retry; the filenames failure is still on screen.
      api.failGet = null;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('section-retry')), findsNothing);
      expect(find.text('Filenames did not answer.'), findsOneWidget);
    },
  );

  testWidgets('a reset of Storage (new rig or profile) loads it again', (
    tester,
  ) async {
    final api = _FakeRig();
    final container = await _pump(tester, api);
    expect(api.gets, 1);
    container.read(storageSettingsProvider.notifier).reset();
    await tester.pumpAndSettle();
    expect(api.gets, 2);
    expect(find.textContaining('Loading'), findsNothing);
    expect(container.read(storageSettingsProvider).value, kTestStorage);
  });
}
