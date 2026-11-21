import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/time_sync_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/site_section.dart';
import '../../support/settings_values.dart';

// ADR 0015: the Location & sky panel edits the rig's copy of the site section.

class _File extends SimpleModePrefsService {
  @override
  Future<bool> load() async => false;
  @override
  Future<void> save(bool enabled) async {}
}

void main() {
  Future<FakeSiteSection> pumpOnRig(
    WidgetTester tester, {
    bool loaded = true,
  }) async {
    tester.view.physicalSize = const Size(1600, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final section = FakeSiteSection(
      initial: loaded
          ? const TestSiteSettings(
              siteName: 'Sydney',
              latitudeDeg: -33.856784,
              longitudeDeg: 151.215297,
            )
          : null,
    );
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(null),
          profileApiProvider.overrideWithValue(
            FakeSectionRig({
              'custom-horizon': {'points': []},
            }).api(),
          ),
          siteSettingsProvider.overrideWith(() => section),
          simplePermanentPierPrefsProvider.overrideWithValue(_File()),
          timeSyncApiProvider.overrideWithValue(null),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          builder: (context, child) => MediaQuery(
            data: MediaQuery.of(
              context,
            ).copyWith(textScaler: const TextScaler.linear(0.5)),
            child: child!,
          ),
          home: const Scaffold(body: SafetySitePanel()),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return section;
  }

  Future<void> save(WidgetTester tester) async {
    final panel =
        tester.state(find.byType(SafetySitePanel)) as PanelSaveRegistration;
    // The skyline saves over HTTP: let the fake clock run it, not an await.
    panel.panelSave();
    await tester.pumpAndSettle();
  }

  testWidgets(
    'Location & sky: Save with the site not loaded from the rig writes nothing and says so; the pier still shows (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(tester, loaded: false);
      expect(find.textContaining('Not loaded'), findsOneWidget);
      expect(find.text('Latitude (°)'), findsNothing, reason: 'no default shows');
      expect(find.text('Bortle class (1..9)'), findsNothing);
      expect(find.text('Permanent pier'), findsOneWidget);
      await save(tester);
      expect(section.puts, isEmpty);
      expect(
        find.textContaining('Settings not loaded from the rig'),
        findsWidgets,
      );
    },
  );

  testWidgets(
    'Location & sky: Save sends the edit made on the rig copy (ADR 0015)',
    (tester) async {
      final section = await pumpOnRig(tester);
      expect(find.text('Sydney'), findsOneWidget, reason: 'the rig\'s site');
      final container = ProviderScope.containerOf(
        tester.element(find.byType(SafetySitePanel)),
      );
      container.read(siteSettingsProvider.notifier).setTypicalSeeingArcsec(1.8);
      await save(tester);
      expect(section.puts.single.typicalSeeingArcsec, 1.8);
      expect(
        section.puts.single.latitudeDeg,
        -33.856784,
        reason: 'the rest of the rig\'s copy goes back unchanged',
      );
      expect(find.text('Saved.'), findsOneWidget);
    },
  );
}
