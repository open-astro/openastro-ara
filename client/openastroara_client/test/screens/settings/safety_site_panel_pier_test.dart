import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/help_icon.dart';

import '../../support/fake_section_rig.dart';
import '../../support/site_section.dart';

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
FakeSiteSection _rig({bool pier = false}) => FakeSiteSection(
  initial: SiteSettings(
    latitudeDeg: -33.856784,
    longitudeDeg: 151.215297,
    permanentPier: pier,
  ),
);

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
        timeSyncApiProvider.overrideWithValue(null),
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
  testWidgets('the Full UI Permanent pier switch writes the profile field', (
    tester,
  ) async {
    final rig = _rig();
    final container = await _pumpPanel(tester, rig);

    expect(find.text('Permanent pier'), findsOneWidget);
    expect(
      find.byWidgetPredicate(
        (w) => w is HelpIcon && w.helpKey == 'safety.site.permanent_pier',
      ),
      findsOneWidget,
      reason: 'an info button like every other row of the panel',
    );
    expect(rig.initial!.permanentPier, isFalse);

    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();

    expect(rig.puts.single.permanentPier, isTrue);
    expect(
      container.read(simplePermanentPierProvider).value,
      isTrue,
      reason: 'the Simple Mode switch reads the same state',
    );
    expect(rig.puts.single.latitudeDeg, -33.856784);
  });

  testWidgets(
    'Location & sky: a refused pier switch-off says so and shows the rig again',
    (tester) async {
      final at = RequestOptions(path: '/api/v1/profile/site');
      final rig = _rig(pier: true)
        ..refuse = DioException(
          requestOptions: at,
          response: Response(requestOptions: at, statusCode: 409),
          type: DioExceptionType.badResponse,
        );
      await _pumpPanel(tester, rig);
      final pier = find.byType(Switch).first;
      expect(tester.widget<Switch>(pier).value, isTrue);

      await tester.tap(pier);
      await tester.pumpAndSettle();

      expect(tester.widget<Switch>(pier).value, isTrue);
      expect(
        find.text("Couldn't save that — something else is using it right now."),
        findsOneWidget,
      );
    },
  );
}
