import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';
import 'package:openastroara/state/settings/panel_save_registry.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/settings_values.dart';

class _NoServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

void main() {
  Future<ProviderContainer> pumpPanel(
    WidgetTester tester, {
    FakeSectionRig? rig,
    bool offline = false,
  }) async {
    // The settings pane is a wide desktop surface; the default 800x600 test
    // viewport overflows the pre-existing editable rows (fixed 280px labels).
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    late ProviderContainer container;
    final api =
        (rig ??
                FakeSectionRig({
                  'custom-horizon': {'points': []},
                  'site': rigDefaults['site'],
                }))
            .api();
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServerServiceProvider.overrideWithValue(_NoServers()),
          profileApiProvider.overrideWithValue(offline ? null : api),
        ],
        child: Consumer(
          builder: (context, ref, _) {
            container = ProviderScope.containerOf(context);
            return MaterialApp(
              // The test's Ahem font renders every glyph at full point size, so
              // the panel's long fixed-width labels overflow their 280px column
              // in ways real fonts don't — scale text down instead of asserting
              // around cosmetic overflow exceptions.
              builder: (context, child) => MediaQuery(
                data: MediaQuery.of(
                  context,
                ).copyWith(textScaler: const TextScaler.linear(0.5)),
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

  testWidgets(
    'the skyline editor appears only with the custom-horizon toggle on',
    (tester) async {
      final container = await pumpPanel(tester);

      expect(
        find.byKey(const ValueKey('add_horizon_point')),
        findsNothing,
        reason: 'flat default altitude row shows instead',
      );
      expect(find.text('Default horizon altitude (°)'), findsOneWidget);

      container.read(siteSettingsProvider.notifier).setUseCustomHorizon(true);
      await tester.pump();

      expect(find.byKey(const ValueKey('add_horizon_point')), findsOneWidget);
      expect(
        find.text('Default horizon altitude (°)'),
        findsNothing,
        reason: 'the flat floor is superseded while the skyline is in charge',
      );
      expect(find.textContaining('No skyline entered yet'), findsOneWidget);
    },
  );

  testWidgets('vertices add, render sorted, and remove through the editor', (
    tester,
  ) async {
    final container = await pumpPanel(tester);
    container.read(siteSettingsProvider.notifier).setUseCustomHorizon(true);
    await tester.pump();

    await tester.tap(find.byKey(const ValueKey('add_horizon_point')));
    await tester.pump();
    expect(find.byKey(const ValueKey('horizon_point_0')), findsOneWidget);

    // A second staged vertex appends; editing the first row edits THAT row
    // (indices stay stable while editing — sorting waits for the Save echo).
    container.read(customHorizonProvider.notifier).addPoint(120, 35);
    container.read(customHorizonProvider.notifier).updateAt(0, azimuthDeg: 200);
    await tester.pump();
    final points = container.read(customHorizonProvider).value!;
    expect(points.first.azimuthDeg, 200);
    expect(points.last.azimuthDeg, 120);
    expect(find.byKey(const ValueKey('horizon_point_1')), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('remove_horizon_point_0')));
    await tester.pump();
    expect(container.read(customHorizonProvider).value!, hasLength(1));
    expect(container.read(customHorizonProvider).value!.single.azimuthDeg, 120);
  });

  testWidgets('Save writes the skyline once and says so when the site fails', (
    tester,
  ) async {
    final rig = FakeSectionRig({
      'custom-horizon': {'points': []},
      'site': rigDefaults['site'],
    });
    final container = await pumpPanel(tester, rig: rig);
    int skylinePuts() =>
        rig.puts.where((p) => p.section == 'custom-horizon').length;

    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(skylinePuts(), 1);
    await tester.pumpAndSettle(const Duration(seconds: 5)); // "Saved." leaves

    rig.failPut.add('site');
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(skylinePuts(), 2);
    expect(
      find.descendant(
        of: find.byType(SnackBar),
        matching: find.textContaining('skyline was saved, but the site'),
      ),
      findsOneWidget,
    );
  });

  testWidgets('no rig: a cached skyline says not loaded, not the editor', (
    tester,
  ) async {
    final container = await pumpPanel(tester, offline: true);
    container.read(customHorizonProvider.notifier).seed(const [
      CustomHorizonPoint(azimuthDeg: 90, altitudeDeg: 20),
    ]);
    container
        .read(siteSettingsProvider.notifier)
        .seed(const TestSiteSettings(useCustomHorizon: true));
    await tester.pump();
    expect(find.text('Rig not connected'), findsOneWidget);
    expect(find.byKey(const ValueKey('horizon_point_0')), findsNothing);
  });

  testWidgets('a skyline refused with the toggle off offers Reload; Save then '
      'lands the site', (tester) async {
    final rig = FakeSectionRig({
      'custom-horizon': {'points': []},
      'site': rigDefaults['site'],
    });
    final container = await pumpPanel(tester, rig: rig);
    rig.savedElsewhere('custom-horizon');
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(rig.puts, isEmpty);
    await tester.tap(find.byKey(const Key('section-reload')));
    await tester.pumpAndSettle();
    await tester.runAsync(container.read(panelSaveActionProvider)!);
    await tester.pumpAndSettle();
    expect(rig.puts.map((p) => p.section), ['custom-horizon', 'site']);
  });
}
