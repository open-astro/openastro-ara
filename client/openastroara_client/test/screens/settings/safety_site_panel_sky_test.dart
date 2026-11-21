import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/site_section.dart';
import '../../support/settings_values.dart';

class _NoServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _File extends SimpleModePrefsService {
  @override
  Future<bool> load() async => false;
  @override
  Future<void> save(bool enabled) async {}
}

/// The rig's site (ADR 0015: served with its tag, no HTTP).
FakeSiteSection _rig(SiteSettings site) => FakeSiteSection(initial: site);

Future<ProviderContainer> _pumpPanel(
  WidgetTester tester,
  FakeSiteSection rig,
) async {
  tester.view.physicalSize = const Size(1600, 1000);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  late ProviderContainer container;
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_NoServers()),
        simplePermanentPierPrefsProvider.overrideWithValue(_File()),
        profileApiProvider.overrideWithValue(
          FakeSectionRig({
            'custom-horizon': {'points': []},
          }).api(),
        ),
        siteSettingsProvider.overrideWith(() => rig),
      ],
      child: Consumer(
        builder: (context, ref, _) {
          container = ProviderScope.containerOf(context);
          return MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            builder: (context, child) => MediaQuery(
              data: MediaQuery.of(context)
                  .copyWith(textScaler: const TextScaler.linear(0.5)),
              child: child!,
            ),
            home: const Scaffold(body: SafetySitePanel()),
          );
        },
      ),
    ),
  );
  await tester.pumpAndSettle();
  return container;
}

void main() {
  const sydney = TestSiteSettings(
    latitudeDeg: -33.856784,
    longitudeDeg: 151.215297,
  );

  Future<void> pick(WidgetTester tester, String label) async {
    final f = find.descendant(
      of: find.byKey(const Key('site-sky-mode')),
      matching: find.text(label),
    );
    await tester.ensureVisible(f);
    await tester.tap(f);
    await tester.pump(const Duration(milliseconds: 50));
  }

  bool sqmSelected(WidgetTester tester) => tester
      .widget<SegmentedButton<bool>>(find.byKey(const Key('site-sky-mode')))
      .selected
      .single;

  testWidgets('a profile with SQM 0 and Bortle 5 loads in Bortle mode', (
    tester,
  ) async {
    await _pumpPanel(tester, _rig(sydney.copyWith(bortleClass: 5)));
    expect(sqmSelected(tester), isFalse);
    expect(find.byKey(const ValueKey('site-bortle')), findsOneWidget);
    expect(find.byKey(const ValueKey('site-sqm')), findsNothing);
    expect(find.text('5'), findsOneWidget);
  });

  // hitTestable: a TextField hint stays in the tree while the field holds a
  // value, hidden, so only painted text may match.
  Future<void> expectDerived(WidgetTester tester, int bortle) async {
    final line = find.text('Derived Bortle class: $bortle');
    await tester.ensureVisible(line);
    expect(line.hitTestable(), findsOneWidget);
  }

  testWidgets(
    'a profile with SQM 20.3 and Bortle 6 loads in SQM mode; the painted derived class follows a committed edit',
    (tester) async {
      final container = await _pumpPanel(
        tester,
        _rig(sydney.copyWith(bortleClass: 6, sqmMagPerArcsec2: 20.3)),
      );
      expect(sqmSelected(tester), isTrue);
      expect(find.byKey(const ValueKey('site-bortle')), findsNothing);
      expect(find.text('20.3'), findsOneWidget);
      await expectDerived(tester, 4);

      await tester.enterText(
        find.descendant(
          of: find.byKey(const ValueKey('site-sqm')),
          matching: find.byType(TextField),
        ),
        '21.9',
      );
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pump();
      expect(container.read(siteSettingsProvider).value!.sqmMagPerArcsec2, 21.9);
      await expectDerived(tester, 1);
    },
  );

  testWidgets('switching mode leaves no hidden value', (tester) async {
    final rig = _rig(sydney.copyWith(bortleClass: 6));
    final container = await _pumpPanel(tester, rig);

    await pick(tester, 'SQM reading');
    var s = container.read(siteSettingsProvider).value!;
    expect(s.sqmMagPerArcsec2, 19.5, reason: 'Bortle 6 sky carried over');
    expect(s.bortleClass, 6);
    expect(sqmSelected(tester), isTrue);

    container.read(siteSettingsProvider.notifier).setSkySqm(21.4);
    await tester.pump();
    expect(container.read(siteSettingsProvider).value!.bortleClass, 2);

    await pick(tester, 'Bortle class');
    s = container.read(siteSettingsProvider).value!;
    expect(
      s.sqmMagPerArcsec2,
      0,
      reason: 'the reading no longer affects anything',
    );
    expect(s.bortleClass, 2);
    expect(sqmSelected(tester), isFalse);
  });
}
