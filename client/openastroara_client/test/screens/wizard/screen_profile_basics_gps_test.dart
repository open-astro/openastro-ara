import 'dart:async';

import 'package:flutter/material.dart';
import 'package:lat_lng_to_timezone/lat_lng_to_timezone.dart' as tz_map;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/wizard/screens/screen_profile_basics.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/state/wizard_state.dart';
import 'package:openastroara/util/gps_site_fill.dart';

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
  Future<ProviderContainer> pump(
    WidgetTester tester, {
    TimeSyncClient? api,
    void Function(ProviderContainer)? setup,
  }) async {
    final container = ProviderContainer(
      overrides: [timeSyncApiProvider.overrideWithValue(api)],
    );
    addTearDown(container.dispose);
    setup?.call(container);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: const MaterialApp(home: Scaffold(body: ScreenProfileBasics())),
      ),
    );
    return container;
  }

  testWidgets(
    'sky brightness is one input: Bortle by default, SQM on switch, no hidden value',
    (tester) async {
      final container = await pump(tester);
      final site = container.read(wizardControllerProvider).draft.site;
      Future<void> type(String key, String v) async {
        await tester.ensureVisible(find.byKey(ValueKey(key)));
        await tester.enterText(find.byKey(ValueKey(key)), v);
        await tester.pump();
      }

      Future<void> pick(String label) async {
        final f = find.descendant(
          of: find.byKey(const ValueKey('wiz-sky-mode')),
          matching: find.text(label),
        );
        await tester.ensureVisible(f);
        await tester.tap(f);
        await tester.pump();
      }

      // Bortle mode with the default 6 shown, no SQM field.
      expect(
        tester
            .widget<SegmentedButton<bool>>(
              find.byKey(const ValueKey('wiz-sky-mode')),
            )
            .selected,
        {false},
      );
      expect(find.text('6'), findsOneWidget);
      expect(find.byKey(const ValueKey('wiz-sqm-sqm')), findsNothing);
      expect(site.bortleClass, isNull);

      await type('wiz-bortle-bortle', '10');
      expect(find.text('Enter a whole number from 1 to 9.'), findsOneWidget);
      expect(site.bortleClass, isNull);
      await type('wiz-bortle-bortle', '3');
      expect(find.text('Enter a whole number from 1 to 9.'), findsNothing);
      expect(site.bortleClass, 3);
      expect(site.sqmMagPerArcsec2, 0, reason: 'Bortle mode writes SQM 0');

      // Switch to SQM: the reading starts at Bortle 3's sky, the Bortle stays derived.
      await pick('SQM reading');
      expect(find.byKey(const ValueKey('wiz-bortle-bortle')), findsNothing);
      expect(site.sqmMagPerArcsec2, 21.0);
      expect(site.bortleClass, 3);
      await type('wiz-sqm-sqm', '15');
      expect(find.text('Enter a value from 16 to 22.5.'), findsOneWidget);
      expect(site.sqmMagPerArcsec2, isNull);
      expect(site.bortleClass, isNull);
      await type('wiz-sqm-sqm', '21.4');
      expect(site.sqmMagPerArcsec2, 21.4);
      expect(site.bortleClass, 2, reason: 'derived from the reading');
      await type('wiz-sqm-sqm', '');
      expect(site.sqmMagPerArcsec2, isNull, reason: 'blank is never saved');

      // Back to Bortle: the class is kept and the reading is cleared.
      await type('wiz-sqm-sqm', '20.3');
      await pick('Bortle class');
      expect(site.bortleClass, 4);
      expect(site.sqmMagPerArcsec2, 0);
    },
  );

  // A remount (Next then Back) shows the draft's mode; an entry that passes
  // through an invalid value ("2" on the way to "21.5") nulls the draft, which
  // must not flip the field to the saved settings' mode.
  testWidgets('an SQM draft over saved Bortle keeps SQM mode while typing', (
    tester,
  ) async {
    await pump(
      tester,
      setup: (c) => c.read(wizardControllerProvider).draft.site
        ..sqmMagPerArcsec2 = 20.3
        ..bortleClass = 4,
    );
    expect(find.byKey(const ValueKey('wiz-sqm-sqm')), findsOneWidget);
    await tester.ensureVisible(find.byKey(const ValueKey('wiz-sqm-sqm')));
    await tester.enterText(find.byKey(const ValueKey('wiz-sqm-sqm')), '2');
    await tester.pump();
    expect(find.byKey(const ValueKey('wiz-sqm-sqm')), findsOneWidget);
    expect(find.byKey(const ValueKey('wiz-bortle-bortle')), findsNothing);
  });

  testWidgets('a Bortle draft over saved SQM keeps Bortle mode while typing', (
    tester,
  ) async {
    await pump(
      tester,
      setup: (c) {
        c.read(siteSettingsProvider.notifier).setSkySqm(21.0);
        c.read(wizardControllerProvider).draft.site
          ..bortleClass = 5
          ..sqmMagPerArcsec2 = 0;
      },
    );
    expect(find.byKey(const ValueKey('wiz-bortle-bortle')), findsOneWidget);
    await tester.ensureVisible(find.byKey(const ValueKey('wiz-bortle-bortle')));
    await tester.enterText(find.byKey(const ValueKey('wiz-bortle-bortle')), '');
    await tester.pump();
    expect(find.byKey(const ValueKey('wiz-bortle-bortle')), findsOneWidget);
    expect(find.byKey(const ValueKey('wiz-sqm-sqm')), findsNothing);
  });

  testWidgets('Fill from GPS fills the fields and the draft from the fix', (
    tester,
  ) async {
    final container = await pump(
      tester,
      api: _FakeTimeSync(
        const TimeSyncState(
          synced: true,
          source: 'gps-external',
          location: TimeSyncLocation(lat: 30.5, lng: -97.75, alt: 240.0),
        ),
      ),
    );

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.latitudeDeg, 30.5);
    expect(draft.longitudeDeg, -97.75);
    expect(draft.altitudeMeters, 240.0);
    // GPS carries no timezone — it's derived worldwide from the coordinates
    // (30.5, -97.75 is central Texas). If DST legislation ever redraws zone
    // BOUNDARIES, the mapping package needs a bump (the P1–P5 issue backlog).
    expect(draft.timezone, 'America/Chicago');
    expect(find.text('America/Chicago'), findsOneWidget);
    // The remounted fields show the fix (a fill the user can't see is a bug).
    expect(find.text('30.5'), findsOneWidget);
    expect(find.text('-97.75'), findsOneWidget);
    expect(find.text('240.0'), findsOneWidget);
    expect(
      find.textContaining("Filled from the rig's last known position"),
      findsOneWidget,
    );
  });

  testWidgets('no fix yet → Mac fallback unavailable → fields untouched', (
    tester,
  ) async {
    debugMacLocationProvider = () async => null; // Mac locator returns no fix
    addTearDown(() => debugMacLocationProvider = null);
    final container = await pump(
      tester,
      api: _FakeTimeSync(const TimeSyncState(synced: false)),
    );

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    expect(find.textContaining('The rig has no position yet'), findsOneWidget);
    expect(container.read(wizardControllerProvider).draft.latitudeDeg, isNull);
  });

  testWidgets(
    'no server + no device location → says so, naming this platform',
    (tester) async {
      debugMacLocationProvider = () async => null;
      addTearDown(() => debugMacLocationProvider = null);
      await pump(tester, api: null);
      await tester.ensureVisible(find.text('Fill from GPS'));
      await tester.tap(find.text('Fill from GPS'));
      await tester.pumpAndSettle();
      expect(find.textContaining('No server connected'), findsOneWidget);
      // The copy names the machine the user is actually on — the app ships on
      // five platforms (three desktops, Android, iOS), so it must not hardcode
      // "this Mac". The expected label comes from the same helper the screen
      // uses, so a new platform arm cannot drift from this test.
      expect(
        find.textContaining(thisDeviceLabel(clientPlatform)),
        findsOneWidget,
      );
    },
  );

  testWidgets('a fix that lands after a manual edit does not overwrite it', (
    tester,
  ) async {
    // Hold the fix open, type a latitude while it's in flight, then release.
    final gate = Completer<void>();
    debugMacLocationProvider = () async {
      await gate.future;
      return (lat: 30.5, lng: -97.75, alt: 240.0);
    };
    addTearDown(() => debugMacLocationProvider = null);
    final container = await pump(tester, api: null);

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pump();

    final draft = container.read(wizardControllerProvider).draft;
    draft.latitudeDeg = 12.34; // the user types while the fetch is running
    gate.complete();
    await tester.pumpAndSettle();

    expect(draft.latitudeDeg, 12.34, reason: 'the typed value must survive');
    expect(find.textContaining('not overwritten'), findsOneWidget);
  });

  test('the coordinate→timezone mapping is worldwide, not US-only', () {
    // One spot check per continent-ish region — the site box must work for
    // any user on Earth (and stays IANA-name-correct through DST-policy
    // changes; boundary redraws are the PORT_TODO caveat).
    expect(tz_map.latLngToTimezoneString(-33.87, 151.21), 'Australia/Sydney');
    expect(tz_map.latLngToTimezoneString(52.52, 13.40), 'Europe/Berlin');
    expect(tz_map.latLngToTimezoneString(35.68, 139.69), 'Asia/Tokyo');
    expect(tz_map.latLngToTimezoneString(-33.92, 18.42), 'Africa/Johannesburg');
    expect(tz_map.latLngToTimezoneString(-23.55, -46.63), 'America/Sao_Paulo');
    expect(
      tz_map.latLngToTimezoneString(35.11, -106.61),
      'America/Denver',
      reason: 'Albuquerque — home turf',
    );
  });

  testWidgets('an RMC-only fix (no altitude) leaves altitude alone', (
    tester,
  ) async {
    final container = await pump(
      tester,
      api: _FakeTimeSync(
        const TimeSyncState(
          synced: true,
          source: 'gps-external',
          location: TimeSyncLocation(lat: 1.0, lng: 2.0), // alt null
        ),
      ),
    );
    container.read(wizardControllerProvider).draft.altitudeMeters = 123.0;

    await tester.ensureVisible(find.text('Fill from GPS'));
    await tester.tap(find.text('Fill from GPS'));
    await tester.pumpAndSettle();

    final draft = container.read(wizardControllerProvider).draft;
    expect(draft.latitudeDeg, 1.0);
    expect(
      draft.altitudeMeters,
      123.0,
      reason: 'null altitude means unknown, not zero (#834 r1 semantics)',
    );
  });
}
