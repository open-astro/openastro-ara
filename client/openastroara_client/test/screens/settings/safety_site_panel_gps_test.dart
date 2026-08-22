import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/gps_site_fill.dart';

class _NoServers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// GPS fallback for the Safety → Site panel: no dongle (server API null), so
/// [fillSiteFromGps] uses the `debugMacLocationProvider` seam for the Mac
/// location — deterministic in widget tests. Also covers the new "edited while
/// looking up" guard and the in-place remount of the fetched values.
void main() {
  Future<ProviderContainer> pumpPanel(WidgetTester tester) async {
    // The settings pane is a wide desktop surface; the default 800x600 test
    // viewport overflows the pre-existing editable rows.
    tester.view.physicalSize = const Size(1600, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    late ProviderContainer container;
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          savedServerServiceProvider.overrideWithValue(_NoServers()),
          // No server → no dongle fix → the Mac fallback path runs.
          timeSyncApiProvider.overrideWithValue(null),
        ],
        child: Consumer(
          builder: (context, ref, _) {
            container = ProviderScope.containerOf(context);
            return MaterialApp(
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

  testWidgets('Mac fallback fills the fields in place and reports the source',
      (tester) async {
    debugMacLocationProvider = () async =>
        const (lat: 30.5, lng: -97.75, alt: 240.0);
    addTearDown(() => debugMacLocationProvider = null);

    final container = await pumpPanel(tester);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    final s = container.read(siteSettingsProvider);
    expect(s.latitudeDeg, 30.5);
    expect(s.longitudeDeg, -97.75);
    expect(s.elevationM, 240.0);
    // In-place refresh: the remounted rows show the fetched values and the IANA
    // timezone derived from the coordinates.
    expect(find.text('30.5'), findsOneWidget);
    expect(find.text('-97.75'), findsOneWidget);
    expect(find.text('America/Chicago'), findsOneWidget);
    expect(find.textContaining('Filled from'), findsOneWidget);
  });

  testWidgets('Mac fallback unavailable → clear message, fields untouched',
      (tester) async {
    debugMacLocationProvider = () async => null; // no location
    addTearDown(() => debugMacLocationProvider = null);

    final container = await pumpPanel(tester);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    // No server → base note "No server connected", then the Mac location
    // fails, so the message explains the fallback is unavailable.
    expect(find.textContaining("couldn't provide a location"), findsOneWidget);
    expect(container.read(siteSettingsProvider).latitudeDeg, 0.0);
  });

  testWidgets(
      'an edit made while the lookup is in flight is never overwritten',
      (tester) async {
    final gate = Completer<DeviceLocationResult?>();
    debugMacLocationProvider = () => gate.future;
    addTearDown(() => debugMacLocationProvider = null);

    final container = await pumpPanel(tester);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    // Let onPressed start _fillFromGps; it snapshots the site fields then
    // suspends on the gate's future.
    await tester.pump();
    await tester.pump();
    expect(container.read(siteSettingsProvider).latitudeDeg, 0.0,
        reason: 'fill is in flight; nothing applied yet');

    // "User" edits latitude while the lookup is pending (a valid value so
    // the notifier accepts it).
    container.read(siteSettingsProvider.notifier).setLatitudeDeg(12.5);
    gate.complete(const (lat: 30.5, lng: -97.75, alt: null));
    await tester.pumpAndSettle();

    // The fetched fix is NOT applied; the manual value stays.
    expect(container.read(siteSettingsProvider).latitudeDeg, 12.5);
    expect(
      find.textContaining('not overwritten'),
      findsOneWidget,
      reason: 'the guard reports that the manual edit won the race',
    );
  });
}