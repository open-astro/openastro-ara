import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/widgets/settings/time_sync_section.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async =>
      const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> s) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeTimeSyncClient implements TimeSyncClient {
  _FakeTimeSyncClient(this.state);
  TimeSyncState state;
  int clientPushes = 0;
  bool manualLocationApplies = true;
  ({DateTime timeUtc, double? lat, double? lng, double? alt})? lastManual;

  @override
  Future<TimeSyncState> getState() async => state;

  @override
  Future<void> pushClientTime(DateTime utcNow) async {
    clientPushes++;
  }

  @override
  Future<TimeSyncPushResult> pushManual({
    required DateTime timeUtc,
    double? lat,
    double? lng,
    double? alt,
  }) async {
    lastManual = (timeUtc: timeUtc, lat: lat, lng: lng, alt: alt);
    return TimeSyncPushResult(
      locationUpdated: manualLocationApplies,
      clockSet: true,
    );
  }

  @override
  void close() {}
}

Future<void> _pump(WidgetTester tester, _FakeTimeSyncClient api) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        timeSyncApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(
        home: Scaffold(body: SingleChildScrollView(child: TimeSyncSection())),
      ),
    ),
  );
  // Let savedServersProvider resolve so the API binds, then the status load.
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('an unsynced server renders the plug-a-GPS guidance', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    await _pump(tester, api);

    expect(find.text('Not synced'), findsOneWidget);
    expect(
      find.textContaining('Plug a USB GPS into the Pi'),
      findsOneWidget,
      reason: 'the §31.1 step-4 prompt shows only while unsynced',
    );
  });

  testWidgets('a synced server shows source, trust, and position', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(
      TimeSyncState(
        synced: true,
        source: 'gps-internal',
        trust: 'high',
        systemTimeOffsetSeconds: -0.4,
        location: const TimeSyncLocation(lat: 30.27, lng: -97.74, alt: 165.0),
        internalGpsAvailable: true,
        syncedAtUtc: DateTime.utc(2026, 7, 11, 7, 15, 30),
      ),
    );
    await _pump(tester, api);

    expect(find.text('Synced (gps-internal, trust high)'), findsOneWidget);
    expect(find.text('-0.4 s'), findsOneWidget);
    expect(find.text('30.2700°, -97.7400°, 165 m'), findsOneWidget);
    expect(find.text('detected'), findsOneWidget);
    expect(find.text('2026-07-11T07:15:30'), findsOneWidget);
    expect(find.textContaining('Plug a USB GPS'), findsNothing);
  });

  testWidgets('push device time calls the client-sync endpoint', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    await _pump(tester, api);

    await tester.tap(find.byKey(const ValueKey('time_sync_push_device')));
    await tester.pumpAndSettle();

    expect(api.clientPushes, 1);
  });

  testWidgets('the manual modal pushes UTC time with a full position', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    await _pump(tester, api);

    await tester.tap(find.byKey(const ValueKey('time_sync_manual_open')));
    await tester.pumpAndSettle();

    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_time')),
      '2026-07-11T04:30:00',
    );
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_lat')),
      '30.27',
    );
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_lng')),
      '-97.74',
    );
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_alt')),
      '165',
    );
    await tester.tap(find.byKey(const ValueKey('time_sync_manual_apply')));
    await tester.pumpAndSettle();

    final call = api.lastManual!;
    expect(call.timeUtc, DateTime.utc(2026, 7, 11, 4, 30));
    expect(call.lat, 30.27);
    expect(call.lng, -97.74);
    expect(call.alt, 165);
    expect(
      find.byKey(const ValueKey('time_sync_manual_apply')),
      findsNothing,
      reason: 'a successful apply closes the dialog',
    );
  });

  testWidgets('a position the server did not apply is called out', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false))
      ..manualLocationApplies = false;
    await _pump(tester, api);

    await tester.tap(find.byKey(const ValueKey('time_sync_manual_open')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_lat')),
      '30.27',
    );
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_lng')),
      '-97.74',
    );
    await tester.tap(find.byKey(const ValueKey('time_sync_manual_apply')));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('The position was NOT applied'),
      findsOneWidget,
      reason: 'a silently dropped position must not look like success',
    );
  });

  testWidgets('the manual modal refuses latitude without longitude', (
    tester,
  ) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    await _pump(tester, api);

    await tester.tap(find.byKey(const ValueKey('time_sync_manual_open')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_lat')),
      '30.27',
    );
    await tester.tap(find.byKey(const ValueKey('time_sync_manual_apply')));
    await tester.pumpAndSettle();

    expect(api.lastManual, isNull, reason: 'half a position must not be sent');
    expect(
      find.textContaining('Latitude and longitude go together'),
      findsOneWidget,
    );
  });

  testWidgets('the manual modal refuses an unparseable time', (tester) async {
    final api = _FakeTimeSyncClient(const TimeSyncState(synced: false));
    await _pump(tester, api);

    await tester.tap(find.byKey(const ValueKey('time_sync_manual_open')));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('time_sync_manual_time')),
      'last tuesday',
    );
    await tester.tap(find.byKey(const ValueKey('time_sync_manual_apply')));
    await tester.pumpAndSettle();

    expect(api.lastManual, isNull);
    expect(find.textContaining('Time must be UTC'), findsOneWidget);
  });
}
