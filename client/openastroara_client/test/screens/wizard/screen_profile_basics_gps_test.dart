import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_profile_basics.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/state/wizard_state.dart';

/// getState() returns a configurable §31.3 state — the wizard's "Fill from
/// GPS" reads the server's last fix (the USB dongle plugged into the SERVER
/// machine; no mount involvement).
class _FakeTimeSync implements TimeSyncClient {
  _FakeTimeSync(this.state);
  final TimeSyncState state;

  @override
  Future<TimeSyncState> getState() async => state;

  @override
  void noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

void main() {
  Future<ProviderContainer> pump(WidgetTester tester,
      {TimeSyncClient? api}) async {
    final container = ProviderContainer(overrides: [
      timeSyncApiProvider.overrideWithValue(api),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(
          home: Scaffold(body: ScreenProfileBasics())),
    ));
    return container;
  }

  testWidgets('Fill from GPS fills the fields and the draft from the fix',
      (tester) async {
    final container = await pump(tester,
        api: _FakeTimeSync(const TimeSyncState(
          synced: true,
          source: 'gps-internal',
          location: TimeSyncLocation(lat: 30.5, lng: -97.75, alt: 240.0),
        )));

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.latitudeDeg, 30.5);
    expect(draft.longitudeDeg, -97.75);
    expect(draft.altitudeMeters, 240.0);
    // The remounted fields show the fix (a fill the user can't see is a bug).
    expect(find.text('30.5'), findsOneWidget);
    expect(find.text('-97.75'), findsOneWidget);
    expect(find.text('240.0'), findsOneWidget);
    expect(find.textContaining('Filled from the server\'s GPS fix'),
        findsOneWidget);
  });

  testWidgets('no fix yet → dongle-on-the-server hint, fields untouched',
      (tester) async {
    final container = await pump(tester,
        api: _FakeTimeSync(const TimeSyncState(synced: false)));

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    expect(find.textContaining('Plug a USB GPS dongle into the computer'),
        findsOneWidget);
    expect(container.read(wizardControllerProvider).draft.latitudeDeg, isNull);
  });

  testWidgets('offline → explains fixes come from the server machine',
      (tester) async {
    await pump(tester, api: null);
    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Not connected to a server'), findsOneWidget);
  });

  testWidgets('an RMC-only fix (no altitude) leaves altitude alone',
      (tester) async {
    final container = await pump(tester,
        api: _FakeTimeSync(const TimeSyncState(
          synced: true,
          source: 'gps-internal',
          location: TimeSyncLocation(lat: 1.0, lng: 2.0), // alt null
        )));
    container.read(wizardControllerProvider).draft.altitudeMeters = 123.0;

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.latitudeDeg, 1.0);
    expect(draft.altitudeMeters, 123.0,
        reason: 'null altitude means unknown, not zero (#834 r1 semantics)');
  });
}
