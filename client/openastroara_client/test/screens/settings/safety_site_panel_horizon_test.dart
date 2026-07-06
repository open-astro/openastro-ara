import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

class _NoServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

void main() {
  Future<ProviderContainer> pumpPanel(WidgetTester tester) async {
    // The settings pane is a wide desktop surface; the default 800x600 test
    // viewport overflows the pre-existing editable rows (fixed 280px labels).
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    late ProviderContainer container;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [savedServerServiceProvider.overrideWithValue(_NoServers())],
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
    await tester.pump();
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

    // A second staged vertex at a lower azimuth sorts ahead of the first.
    container.read(customHorizonProvider.notifier).addPoint(120, 35);
    container.read(customHorizonProvider.notifier).updateAt(0, azimuthDeg: 200);
    await tester.pump();
    final points = container.read(customHorizonProvider);
    expect(points.first.azimuthDeg, 120);
    expect(points.last.azimuthDeg, 200);
    expect(find.byKey(const ValueKey('horizon_point_1')), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('remove_horizon_point_0')));
    await tester.pump();
    expect(container.read(customHorizonProvider), hasLength(1));
    expect(container.read(customHorizonProvider).single.azimuthDeg, 200);
  });
}
