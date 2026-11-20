import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/settings/panels/session_filenames_panel.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';
import 'package:openastroara/widgets/settings/editable_field.dart';

import '../../../support/storage_fixture.dart';

/// The rig behind File naming: Storage as [FakeStorageApi] serves it, and the
/// other three sections the panel reads, whose filenames or site GET can be
/// failed. Their PUTs echo what was sent.
class _FakeRig extends FakeStorageApi {
  Object? failFilenames;
  Object? failSite;

  @override
  Future<({FilenamesSettings value, String? etag})>
  getFilenamesSection() async {
    if (failFilenames != null) throw failFilenames!;
    return (value: const FilenamesSettings(), etag: '"f1"');
  }

  @override
  Future<({FilenamesSettings value, String? etag})> putFilenamesSection(
    FilenamesSettings value, {
    String? ifMatch,
  }) async => (value: value, etag: '"f1"');

  /// Every site save that reached the rig.
  final sitePuts = <SiteSettings>[];

  @override
  Future<({SiteSettings value, String? etag})> getSiteSection() async {
    if (failSite != null) throw failSite!;
    return (value: const SiteSettings(latitudeDeg: 51.477833), etag: '"s1"');
  }

  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings value, {
    String? ifMatch,
  }) async {
    sitePuts.add(value);
    return (value: value, etag: '"s2"');
  }

  /// Every optics save that reached the rig.
  final opticsPuts = <OpticsSettings>[];

  @override
  Future<({OpticsSettings value, String? etag})> getOpticsSection() async =>
      (value: const OpticsSettings(focalLengthMm: 382), etag: '"o1"');

  @override
  Future<({OpticsSettings value, String? etag})> putOpticsSection(
    OpticsSettings value, {
    String? ifMatch,
  }) async {
    opticsPuts.add(value);
    return (value: value, etag: '"o2"');
  }
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
      // Storage answers on Retry; the filenames failure is still on screen,
      // and so is Retry, until Filenames answers too.
      api.failGet = null;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.textContaining('Filenames did not answer.'), findsOneWidget);
      api.failFilenames = null;
      await tester.tap(find.byKey(const Key('section-retry')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('section-retry')), findsNothing);
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

  testWidgets('a reset after a failed Storage load loads it again', (
    tester,
  ) async {
    final api = _FakeRig()..failGet = StateError('Storage did not answer.');
    final container = await _pump(tester, api);
    expect(find.textContaining('Not loaded'), findsOneWidget);
    api.failGet = null;
    container.read(storageSettingsProvider.notifier).reset();
    await tester.pumpAndSettle();
    expect(api.gets, 2);
    expect(container.read(storageSettingsProvider).value, kTestStorage);
  });

  testWidgets('the File naming load error is announced to screen readers', (
    tester,
  ) async {
    final api = _FakeRig()..failSite = StateError('Site did not answer.');
    await _pump(tester, api);
    expect(
      tester.getSemantics(find.textContaining('Site did not answer.')),
      isSemantics(isLiveRegion: true),
    );
  });

  testWidgets(
    'File naming: the telescope name is saved with the optics copy from the rig (ADR 0015)',
    (tester) async {
      final api = _FakeRig();
      final container = await _pump(tester, api);
      expect(find.widgetWithText(EditableTextRow, 'Telescope'), findsOneWidget);
      container
          .read(opticsSettingsProvider.notifier)
          .setTelescopeName('RedCat 51');
      await container.read(panelSaveActionProvider)!();
      await tester.pumpAndSettle();
      expect(api.opticsPuts.single.telescopeName, 'RedCat 51');
      expect(
        api.opticsPuts.single.focalLengthMm,
        382,
        reason: 'the rest of the rig\'s copy goes back unchanged',
      );
    },
  );

  testWidgets(
    'File naming: the observer name is saved with the site copy from the rig (ADR 0015)',
    (tester) async {
      final api = _FakeRig();
      final container = await _pump(tester, api);
      expect(find.widgetWithText(EditableTextRow, 'Observer'), findsOneWidget);
      container.read(siteSettingsProvider.notifier).setObserverName('J. Troy');
      await container.read(panelSaveActionProvider)!();
      await tester.pumpAndSettle();
      expect(api.sitePuts.single.observerName, 'J. Troy');
      expect(
        api.sitePuts.single.latitudeDeg,
        51.477833,
        reason: 'the rest of the rig\'s copy goes back unchanged',
      );
    },
  );

  testWidgets(
    'File naming: Save with the site not loaded from the rig writes no site (ADR 0015)',
    (tester) async {
      final api = _FakeRig()..failSite = StateError('Site did not answer.');
      final container = await _pump(tester, api);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      await container.read(panelSaveActionProvider)!();
      await tester.pumpAndSettle();
      expect(api.sitePuts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets('Save that retries the other loads keeps the Storage edits', (
    tester,
  ) async {
    final api = _FakeRig()..failSite = StateError('Site did not answer.');
    final container = await _pump(tester, api);
    const edited = r'$$TARGETNAME$$_$$DATETIME$$';
    container
        .read(storageSettingsProvider.notifier)
        .setFilenameTemplate(edited);
    api.failSite = null;
    await container.read(panelSaveActionProvider)!();
    await tester.pumpAndSettle();
    expect(api.puts.single.filenameTemplate, edited);
  });
}
