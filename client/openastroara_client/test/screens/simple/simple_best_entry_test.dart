import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/state/sky_atlas/tonights_best_list_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/services/monitor_device_service.dart';
import 'package:openastroara/state/simple_monitor_state.dart';

class _Prefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _Clock extends MonitorClock {
  @override
  DateTime build() => ref.watch(clockProvider)();
}

class _Stats extends StatsTargetsNotifier {
  @override
  Future<List<StatsTarget>?> build() async => const [];
}

class _Site extends SiteSettingsNotifier {
  @override
  LoadedSection<SiteSettings> build() => LoadedSection(
    value: const SiteSettings(
      latitudeDeg: -33.856784,
      longitudeDeg: 151.215297,
    ),
    source: SectionSource.rig,
  );
}

const _m42 = TonightSkyObject(
  id: 'M 42',
  name: 'Orion Nebula',
  type: 'HII',
  magnitude: 4,
  raDeg: 83.8,
  decDeg: -5.4,
  altitudeDeg: 40,
  maxAltitudeDeg: 60,
  score: 80,
);

Future<void> _pump(
  WidgetTester tester,
  Size size, {
  required bool phone,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(
        phone ? TargetPlatform.iOS : TargetPlatform.linux,
      ),
      simpleModePrefsProvider.overrideWithValue(_Prefs()),
      serverLinkUpProvider.overrideWithValue(true),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      rigSetupHydratedProvider.overrideWith((ref) async => false),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      monitorClockProvider.overrideWith(_Clock.new),
      deviceBatteryProvider.overrideWith(
        (ref) => Stream.value((level: 64, charging: false)),
      ),
      siteSettingsProvider.overrideWith(_Site.new),
      skyClockProvider.overrideWith((ref) => const Stream<int>.empty()),
      tonightsBestProvider.overrideWith((ref) async => const [_m42]),
      statsTargetsProvider.overrideWith(_Stats.new),
      tonightsBestPrefsServiceProvider.overrideWithValue(
        TonightsBestPrefsService(
          supportDir: () async => throw StateError('no disk'),
        ),
      ),
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
}

void main() {
  testWidgets(
    "the left rail opens Tonight's Best and Back returns to the shell",
    (tester) async {
      await _pump(tester, const Size(1180, 820), phone: false);
      await tester.tap(find.byKey(const Key('simple-best-open')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('best-screen')), findsOneWidget);
      expect(find.byKey(const Key('best-row-M 42')), findsOneWidget);
      await tester.tap(find.byKey(const Key('best-back')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('best-screen')), findsNothing);
      expect(find.byKey(const Key('simple-best-open')), findsOneWidget);
    },
  );

  testWidgets('the rail button is there at a width where Monitor is not', (
    tester,
  ) async {
    await _pump(tester, const Size(820, 700), phone: false);
    expect(find.byKey(const Key('simple-monitor-open')), findsNothing);
    expect(find.byKey(const Key('simple-best-open')), findsOneWidget);
  });

  testWidgets(
    "the phone deck opens Tonight's Best from its seventh word and Back returns",
    (tester) async {
      await _pump(tester, const Size(390, 844), phone: true);
      final monitor = tester.getTopLeft(
        find.byKey(const Key('companion-section-monitor')),
      );
      final best = tester.getTopLeft(
        find.byKey(const Key('companion-section-best')),
      );
      expect(
        best.dx,
        greaterThan(monitor.dx),
        reason: 'Best sits right of Monitor',
      );
      await tester.drag(
        find.byKey(const Key('companion-sections')),
        const Offset(-400, 0),
      );
      await tester.pump(const Duration(milliseconds: 500));
      await tester.tap(find.byKey(const Key('companion-section-best')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('best-screen')), findsOneWidget);
      await tester.tap(find.byKey(const Key('best-back')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('best-screen')), findsNothing);
      expect(find.byKey(const Key('companion-deck')), findsOneWidget);
    },
  );
}
