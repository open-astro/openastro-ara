import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/settings/panels/safety_site_panel.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/client_gps_prefs_service.dart';
import 'package:openastroara/services/serial_gps_source.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/client_gps_state.dart';
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
String _cs(String body) {
  var c = 0;
  for (final u in body.codeUnits) {
    c ^= u;
  }
  return '\$$body*${c.toRadixString(16).toUpperCase().padLeft(2, '0')}';
}

/// A dongle that answers with one good fix and then stays open, like a port.
class _DongleSource implements SerialGpsSource {
  @override
  bool get supported => true;
  @override
  Future<List<String>> availablePorts() async => const ['/dev/cu.usbserial-1'];
  @override
  Stream<String> lines(String port) {
    final c = StreamController<String>();
    Future<void>.microtask(() {
      c.add(_cs('GPRMC,041926.000,A,3851.2384,N,07702.6101,W,0.09,318.63,220926,,,A'));
      c.add(_cs('GPGGA,041926.000,3851.2384,N,07702.6101,W,1,08,0.9,120.5,M,-33.0,M,,'));
    });
    return c.stream;
  }
}

/// A daemon that already holds a location (e.g. the 2-dp echo of an earlier push).
class _ServerWithLocation implements TimeSyncClient {
  _ServerWithLocation(this.location);
  final TimeSyncLocation location;
  @override
  Future<TimeSyncState> getState() async => TimeSyncState(synced: true, source: 'gps-external', trust: 'high', location: location);
  @override
  Future<void> pushClientTime(DateTime utcNow) async {}
  @override
  Future<TimeSyncPushResult> pushGpsFix({required DateTime timeUtc, double? lat, double? lng, double? alt}) async =>
      const TimeSyncPushResult(locationUpdated: true, clockSet: true);
  @override
  Future<TimeSyncPushResult> pushManual({required DateTime timeUtc, double? lat, double? lng, double? alt}) async =>
      const TimeSyncPushResult(locationUpdated: false, clockSet: false);
  @override
  Future<void> close() async {}
}

/// In-memory prefs: real file I/O never completes inside a widget test's
/// fake-async zone, so the notifier's build() would hang on the file read.
class _MemoryPrefs extends ClientGpsPrefsService {
  _MemoryPrefs(this._prefs) : super(supportDir: () async => throw UnsupportedError('unused'));
  ClientGpsPrefs _prefs;
  @override
  Future<ClientGpsPrefs> load() async => _prefs;
  @override
  Future<void> save(ClientGpsPrefs prefs) async => _prefs = prefs;
}

void main() {
  Future<ProviderContainer> pumpPanel(WidgetTester tester,
      {SerialGpsSource? dongle, ClientGpsPrefsService? donglePrefs, TimeSyncClient? server}) async {
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
          // No server → no dongle fix → the Mac fallback path runs (unless a test passes one).
          timeSyncApiProvider.overrideWithValue(server),
          if (dongle != null) serialGpsSourceProvider.overrideWithValue(dongle),
          // Always in-memory (see _MemoryPrefs); disabled unless a test enables it.
          clientGpsPrefsServiceProvider.overrideWithValue(donglePrefs ?? _MemoryPrefs(const ClientGpsPrefs())),
          if (dongle != null)
            clientGpsListenWindowProvider.overrideWithValue(const Duration(milliseconds: 100)),
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

  testWidgets('a GPS dongle on this computer wins over the device location',
      (tester) async {
    // The device-location seam would answer with Austin; the dongle says Washington.
    debugMacLocationProvider = () async =>
        const (lat: 30.5, lng: -97.75, alt: 240.0);
    addTearDown(() => debugMacLocationProvider = null);
    final prefs = _MemoryPrefs(const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));

    final container = await pumpPanel(tester, dongle: _DongleSource(), donglePrefs: prefs);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    // Not pumpAndSettle: the enabled loop re-arms a 2-minute timer after every
    // read, and settling would keep advancing fake time into it forever.
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }

    final s = container.read(siteSettingsProvider);
    expect(s.latitudeDeg, closeTo(38.85, 1e-9), reason: 'rounded to 2 dp like every fill');
    expect(s.longitudeDeg, closeTo(-77.04, 1e-9));
    expect(s.elevationM, closeTo(120.5, 1e-9));
    expect(find.textContaining('Filled from the GPS dongle on this'), findsOneWidget);
  });

  testWidgets('a fresh client-dongle fix beats the server echo of a fix (step 0 before step 1)',
      (tester) async {
    debugMacLocationProvider = () async => null;
    addTearDown(() => debugMacLocationProvider = null);
    final prefs = _MemoryPrefs(const ClientGpsPrefs(enabled: true, port: '/dev/cu.usbserial-1'));
    // The server reports Austin (its 2-dp echo of an older push); the dongle says Washington.
    final container = await pumpPanel(tester,
        dongle: _DongleSource(),
        donglePrefs: prefs,
        server: _ServerWithLocation(const TimeSyncLocation(lat: 30.27, lng: -97.74)));
    // Let the background loop take its first read so a FRESH fix exists before the tap.
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    for (var i = 0; i < 8; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    final s = container.read(siteSettingsProvider);
    expect(s.latitudeDeg, closeTo(38.85, 1e-9), reason: 'the dongle, not the server echo');
    expect(find.textContaining('Filled from the GPS dongle on this'), findsOneWidget);
  });

  testWidgets('with the setting off, Fill from GPS probes a plausible port, uses the fix and enables the setting',
      (tester) async {
    debugMacLocationProvider = () async =>
        const (lat: 30.5, lng: -97.75, alt: 240.0); // would be the fallback
    addTearDown(() => debugMacLocationProvider = null);
    final prefs = _MemoryPrefs(const ClientGpsPrefs()); // never turned on
    final container = await pumpPanel(tester, dongle: _DongleSource(), donglePrefs: prefs);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    final s = container.read(siteSettingsProvider);
    expect(s.latitudeDeg, closeTo(38.85, 1e-9), reason: 'the probed dongle, not the device location');
    expect(s.elevationM, closeTo(120.5, 1e-9));
    expect(find.textContaining('now enabled in Settings'), findsOneWidget);
    final saved = await prefs.load();
    expect(saved.enabled, isTrue);
    expect(saved.port, '/dev/cu.usbserial-1');
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