import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/screens/settings/settings_shell.dart';
import 'package:openastroara/services/advanced_settings_prefs_service.dart';
import 'package:openastroara/state/settings/advanced_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';

// The real toggle over a prefs file that cannot be found (no disk I/O in a
// widget test): it reads the default and saves nowhere.
final _noPrefsFile = advancedSettingsPrefsProvider.overrideWithValue(
  AdvancedSettingsPrefsService(
    supportDir: () async => throw const FileSystemException('no support dir'),
  ),
);

void main() {
  testWidgets(
    'the Settings header has one Advanced toggle, on to start (#1544)',
    (tester) async {
      tester.view.physicalSize = const Size(1600, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final c = ProviderContainer(
        overrides: [_noPrefsFile, simpleModeActiveProvider.overrideWithValue(true)],
      );
      addTearDown(c.dispose);
      // The About panel needs no rig.
      c.read(selectedSettingsPanelProvider.notifier).select('app.changelog');
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: c,
          child: const MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(body: SettingsShell()),
          ),
        ),
      );
      await tester.pump();
      final toggle = find.byKey(const Key('settings-advanced-toggle'));
      expect(find.text('Advanced'), findsOneWidget);
      expect(tester.widget<Switch>(toggle).value, isTrue);
      await tester.tap(toggle);
      await tester.pump();
      expect(c.read(advancedSettingsProvider).value, isFalse);
      expect(tester.widget<Switch>(toggle).value, isFalse);
    },
  );
  testWidgets(
    'the Settings header fits a narrow window at large text (#1544)',
    (tester) async {
      tester.view.physicalSize = const Size(820, 1200);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final c = ProviderContainer(
        overrides: [_noPrefsFile, simpleModeActiveProvider.overrideWithValue(true)],
      );
      addTearDown(c.dispose);
      c.read(selectedSettingsPanelProvider.notifier).select('app.changelog');
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: c,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            builder: (context, child) => MediaQuery(
              data: MediaQuery.of(context)
                  .copyWith(textScaler: const TextScaler.linear(1.3)),
              child: child!,
            ),
            home: const Scaffold(body: SettingsShell()),
          ),
        ),
      );
      await tester.pump();
      // A header row too wide for the window throws a RenderFlex overflow.
      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('settings-advanced-toggle')), findsOneWidget);
    },
  );
}
