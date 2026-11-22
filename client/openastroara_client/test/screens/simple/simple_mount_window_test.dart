import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/state/profile_management_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/safety_section.dart';
import '../../support/settings_values.dart';

/// A mount that records what it was asked to do.
class _Mount extends MountNotifier {
  _Mount(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(json);

  void set(Map<String, dynamic> next) => state = AsyncData(MountStatus.fromJson(next));

  @override
  Future<bool> setTracking(bool enabled) async {
    calls.add('tracking $enabled');
    if (refuseTracking > 0) {
      refuseTracking--;
      throw StateError('Mount is slewing');
    }
    return !busy;
  }

  /// Another command still in flight: asks are dropped (the notifier's re-entrancy guard).
  bool busy = false;

  /// How many tracking asks to refuse first (a mount still settling).
  int refuseTracking = 0;

  @override
  Future<bool> park() async {
    calls.add('park');
    return true;
  }

  @override
  Future<bool> unpark() async {
    calls.add('unpark');
    return true;
  }

  @override
  Future<bool> confirmHome() async {
    calls.add('confirm-home');
    return true;
  }
}

Map<String, dynamic> _wire({bool tracking = true, bool parked = false, String state = 'connected', String runtime = 'tracking'}) => {
      'device_id': 'm',
      'name': 'EQM-35 Pro',
      'state': state,
      'capabilities': {'can_park': true, 'can_unpark': true, 'can_set_tracking': true, 'can_slew': true},
      'runtime': {'state': runtime, 'tracking': tracking, 'parked': parked},
    };

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async =>
      const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running);
}

Future<(ProviderContainer, _Mount)> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {bool inSettings = false,
    DeviceDriver? driver,
    bool planRunning = false,
    double? width,
    FakeSafetySection? safety}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final mount = _Mount(wire);
  final c = ProviderContainer(overrides: [
    mountProvider.overrideWith(() => mount),
    deviceDriverProvider.overrideWith((ref, type) async => type == 'telescope' ? driver : null),
    if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
    if (safety != null) ...[
      profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
      safetyPoliciesProvider.overrideWith(() => safety),
    ],
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(width: width, child: SimpleMountWindow(inSettings: inSettings)),
        ),
      ),
    ),
  ));
  await tester.pump();
  return (c, mount);
}

/// #2070 — a mount at its false zero after a power loss: the rig does not trust its position.
Map<String, dynamic> _unverified({required bool atHome, String reason = 'at_home_on_connect'}) {
  final w = _wire(tracking: false, runtime: 'idle');
  w['runtime'] = {
    ...w['runtime'] as Map<String, dynamic>,
    'at_home': atHome,
    'position_unverified': true,
    'position_unverified_reason': reason,
  };
  return w;
}

void main() {
  testWidgets('an unverified position is said, Park is off, and Home is confirmed only after asking (#2070)', (tester) async {
    final (_, mount) = await _pump(tester, _unverified(atHome: true));
    expect(find.byKey(const Key('simple-mount-position-unverified')), findsOneWidget);
    expect(find.text('Mount position unverified'), findsOneWidget);
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-mount-park-parked'))).onTap, isNull,
        reason: 'the rig refuses a park from an unverified position');
    await tester.tap(find.byKey(const Key('simple-mount-confirm-home')));
    await tester.pump();
    expect(mount.calls, isEmpty, reason: 'the window asks first');
    await tester.tap(find.text('Yes, it is at Home'));
    await tester.pump();
    expect(mount.calls, ['confirm-home']);
  });

  // #2066 polar alignment's stop was not confirmed: the window says so, not
  // "the mount may have lost power".
  testWidgets('Simple: a position left unverified by an unconfirmed stop says why', (tester) async {
    await _pump(tester, _unverified(atHome: false, reason: 'stop_unconfirmed'));
    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.mountPositionStopUnconfirmedBody), findsOneWidget);
    expect(find.text(en.mountPositionUnverifiedBody), findsNothing);
  });

  // #2173 review — on a mount that can find Home, Home is the way out. After a
  // power loss the user confirms Home by eye after it; after an unconfirmed
  // stop a Home that ends at Home trusts the position by itself.
  testWidgets('Simple: Home is followed by an eye check after a power loss, not after an unconfirmed stop (#2173)', (tester) async {
    final en = lookupAppLocalizations(const Locale('en'));
    Map<String, dynamic> canHome(Map<String, dynamic> w) {
      w['capabilities'] = {...w['capabilities'] as Map<String, dynamic>, 'can_find_home': true};
      return w;
    }

    await _pump(tester, canHome(_unverified(atHome: false)));
    expect(find.text(en.mountPositionUnverifiedPressHome), findsOneWidget);
    expect(find.textContaining('confirm by eye'), findsOneWidget);
    await _pump(tester, canHome(_unverified(atHome: false, reason: 'stop_unconfirmed')));
    expect(find.text(en.mountPositionStopUnconfirmedPressHome), findsOneWidget);
    expect(find.textContaining('confirm by eye'), findsNothing);
  });

  // #2173 — a mount whose driver says its Find Home is a real sensor search:
  // Home clears the warning by itself, after a power loss too.
  testWidgets('Simple: a sensor-search mount says Home clears the warning by itself (#2173)', (tester) async {
    final en = lookupAppLocalizations(const Locale('en'));
    final w = _unverified(atHome: false);
    w['capabilities'] = {
      ...w['capabilities'] as Map<String, dynamic>,
      'can_find_home': true,
      'home_is_sensor_search': true,
    };
    await _pump(tester, w);
    expect(find.text(en.mountPositionSensorSearchPressHome), findsOneWidget);
    expect(find.textContaining('confirm by eye'), findsNothing);
  });

  testWidgets('an unverified mount off Home is told to run its zero search, with no confirm (#2070)', (tester) async {
    await _pump(tester, _unverified(atHome: false));
    expect(find.byKey(const Key('simple-mount-run-zero-search')), findsOneWidget);
    expect(find.byKey(const Key('simple-mount-confirm-home')), findsNothing);
  });

  testWidgets('a tap dropped while the last command runs says so in a pill, once (ALP-4982)', (tester) async {
    // #1486 review: Tracking Off then Parked before the first answer dropped the second with no word.
    final (c, mount) = await _pump(tester, _wire());
    mount.busy = true;
    await tester.tap(find.byKey(const Key('simple-mount-tracking-off')));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, 'Still busy'));
    await tester.tap(find.byKey(const Key('simple-mount-tracking-off')));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), hasLength(1), reason: 'one pill, not one per tap');
    await tester.pump(const Duration(seconds: 11));
    expect(c.read(simpleMessagesProvider), isEmpty, reason: 'it fades like a result');
  });

  testWidgets('the mount window: tracking, park and meridian flip, rows of one height', (tester) async {
    final safety = FakeSafetySection(initial: const TestSafetyPolicies(meridianPauseMin: 12));
    final (c, mount) = await _pump(tester, _wire(), safety: safety);
    expect(find.text('Mount · EQM-35 Pro'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-mount-tracking-off')));
    await tester.pump();
    expect(mount.calls, ['tracking false']);
    await tester.tap(find.byKey(const Key('simple-mount-flip-off')));
    await tester.pump();
    expect(c.read(safetyPoliciesProvider).value!.meridianFlipAuto, isFalse, reason: 'kept in the profile');
    expect(safety.puts.single.meridianPauseMin, 12, reason: 'over the rig\'s copy');
    double row(String label) => tester.getSize(find.byKey(Key('simple-window-row-$label'))).height;
    expect(row('Position'), row('Tracking'));
    expect(row('Meridian flip'), row('Tracking'));
  });

  testWidgets("the mount window shows the rig's clearance limit: Off, or the altitude it stops at (#1589)", (tester) async {
    final off = FakeSafetySection(initial: const TestSafetyPolicies());
    await _pump(tester, _wire(), safety: off);
    await tester.pump();
    expect(tester.widget<Text>(find.byKey(const Key('simple-mount-clearance'))).data, 'Off');
    final on = FakeSafetySection(initial: const TestSafetyPolicies(clearanceLimitEnabled: true, clearanceMaxAltitudeDeg: 78));
    await _pump(tester, _wire(), safety: on);
    await tester.pump();
    expect(tester.widget<Text>(find.byKey(const Key('simple-mount-clearance'))).data, 'Stops at 78°');
    expect(on.puts, isEmpty, reason: 'read-only here');
  });

  testWidgets('a half-degree clearance limit shows its decimal, never the higher whole degree (#1589)', (tester) async {
    final on = FakeSafetySection(initial: const TestSafetyPolicies(clearanceLimitEnabled: true, clearanceMaxAltitudeDeg: 79.5));
    await _pump(tester, _wire(), safety: on);
    await tester.pump();
    expect(tester.widget<Text>(find.byKey(const Key('simple-mount-clearance'))).data, 'Stops at 79.5°');
  });

  testWidgets('the meridian flip waits for the profile from the rig: no switch shows, and nothing is sent (ADR 0015)',
      (tester) async {
    final safety = FakeSafetySection(initial: null);
    await _pump(tester, _wire(), safety: safety);
    await tester.pump();
    expect(find.byKey(const Key('simple-mount-flip-off')), findsNothing, reason: 'no default shows');
    expect(find.byKey(const Key('section-retry')), findsOneWidget);
    safety.initial = const TestSafetyPolicies(meridianFlipAuto: true);
    await tester.tap(find.byKey(const Key('section-retry')));
    await tester.pump();
    await tester.pump();
    expect(find.byKey(const Key('simple-mount-flip-off')), findsOneWidget, reason: 'Retry loads it');
    expect(safety.puts, isEmpty);
  });

  testWidgets("a meridian flip change the rig refuses says why and shows the rig's setting again (ADR 0015)",
      (tester) async {
    final safety = FakeSafetySection(initial: const TestSafetyPolicies(meridianFlipAuto: true))
      ..refuse = StateError('rig error');
    final (c, _) = await _pump(tester, _wire(), safety: safety);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mount-flip-off')));
    await tester.pump();
    await tester.pump();
    expect(c.read(safetyPoliciesProvider).value!.meridianFlipAuto, isTrue, reason: 'the rig still flips');
    expect(find.textContaining("Couldn't save the meridian flip"), findsOneWidget, reason: 'it says why');
  });

  testWidgets('Park is Parked / Unparked segments; while it moves it reads Parking…, then the new state', (tester) async {
    final (_, mount) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    expect(mount.calls, ['park']);
    expect(find.text('Parking…'), findsOneWidget);
    expect(find.byKey(const Key('simple-mount-park-parked')), findsNothing, reason: 'no second tap while it moves');
    mount.set(_wire(tracking: false, parked: true, runtime: 'parked'));
    await tester.pump();
    expect(find.text('Parking…'), findsNothing);
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-mount-park-parked'))).selected, isTrue);
  });

  testWidgets("Tracking locked while the mount moves keeps its look (only the Switches window dims)", (tester) async {
    await _pump(tester, _wire(runtime: 'unparking', tracking: false));
    final seg = find.byKey(const Key('simple-mount-tracking-on'));
    expect(tester.widget<SimpleSegment>(seg).onTap, isNull, reason: 'locked while it moves');
    final dims = find.ancestor(of: seg, matching: find.byType(Opacity));
    expect(dims.evaluate().map((e) => (e.widget as Opacity).opacity).where((o) => o < 1), isEmpty);
  });

  testWidgets('a park that stops short clears Parking… and says so', (tester) async {
    final (c, mount) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    mount.set(_wire(runtime: 'slewing'));
    await tester.pump();
    expect(find.text('Parking…'), findsOneWidget);
    mount.set(_wire(tracking: false, runtime: 'idle')); // aborted: stopped, not parked
    await tester.pump();
    expect(find.text('Parking…'), findsOneWidget, reason: 'a moment of grace: AtPark can lag Slewing');
    await tester.pump(const Duration(seconds: 3));
    expect(find.text('Parking…'), findsNothing);
    expect(find.byKey(const Key('simple-mount-park-parked')), findsOneWidget, reason: 'the control is back');
    expect(c.read(simpleMessagesProvider).single.title, "The mount didn't finish parking");
  });

  testWidgets('a park whose AtPark lags its Slewing by a poll still finishes, with no "didn\'t finish"', (tester) async {
    // Review of #1486: ASCOM drivers often drop Slewing a moment before AtPark goes true.
    final (c, mount) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    mount.set(_wire(runtime: 'slewing'));
    await tester.pump();
    mount.set(_wire(tracking: false, runtime: 'idle')); // stopped, not parked yet
    await tester.pump();
    mount.set(_wire(tracking: false, parked: true, runtime: 'parked'));
    await tester.pump();
    expect(find.text('Parking…'), findsNothing);
    expect(c.read(simpleMessagesProvider), isEmpty);
  });

  testWidgets('a mount that disconnects mid-park clears Parking… and says so', (tester) async {
    final (c, mount) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    mount.set(_wire(state: 'disconnected'));
    await tester.pump();
    expect(c.read(simpleMountMotionProvider), isNull);
    expect(c.read(simpleMessagesProvider).single.title, "The mount didn't finish parking");
  });

  testWidgets('Tracking On on a parked mount unparks it first, then tracks once it is unparked', (tester) async {
    final (_, mount) = await _pump(tester, _wire(tracking: false, parked: true, runtime: 'parked'));
    await tester.tap(find.byKey(const Key('simple-mount-tracking-on')));
    await tester.pump();
    expect(mount.calls, ['unpark'], reason: 'not tracking yet: it is still parked');
    expect(find.text('Unparking…'), findsOneWidget);
    mount.set(_wire(tracking: false, parked: true, runtime: 'unparking'));
    await tester.pump();
    expect(mount.calls, ['unpark']);
    mount.set(_wire(tracking: false, parked: false, runtime: 'idle'));
    await tester.pump();
    await tester.pump();
    expect(mount.calls, ['unpark', 'tracking true']);
  });

  testWidgets('after an unpark the mount may still be settling: tracking is asked again until it takes',
      (tester) async {
    final (_, mount) = await _pump(tester, _wire(tracking: false, parked: true, runtime: 'parked'));
    mount.refuseTracking = 2;
    await tester.tap(find.byKey(const Key('simple-mount-tracking-on')));
    await tester.pump();
    mount.set(_wire(tracking: false, parked: false, runtime: 'slewing'));
    await tester.pump();
    expect(mount.calls, ['unpark'], reason: 'still moving out of park');
    mount.set(_wire(tracking: false, parked: false, runtime: 'idle'));
    await tester.pump(const Duration(seconds: 5));
    expect(mount.calls, ['unpark', 'tracking true', 'tracking true', 'tracking true'], reason: 'one tap is enough');
  });

  testWidgets('a parked mount unparks from the segment and reads Unparking… while it does', (tester) async {
    final (_, mount) = await _pump(tester, _wire(tracking: false, parked: true, runtime: 'parked'));
    await tester.tap(find.byKey(const Key('simple-mount-park-unparked')));
    await tester.pump();
    expect(mount.calls, ['unpark']);
    mount.set(_wire(tracking: false, parked: true, runtime: 'unparking'));
    await tester.pump();
    expect(find.text('Unparking…'), findsOneWidget);
  });

  testWidgets('while a plan runs, Parked asks first, in the window', (tester) async {
    final (_, mount) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump(); // the run state arrives
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    expect(mount.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(mount.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsNothing);
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    await tester.tap(find.text('Park anyway'));
    await tester.pump();
    expect(mount.calls, ['park']);
  });

  testWidgets('on a narrow window the Park question sits over its buttons', (tester) async {
    // #1666 audit P2: the same row as the Filter wheel window's.
    await _pump(tester, _wire(), planRunning: true, width: 280);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    final question = tester.getRect(find.textContaining('A plan is running'));
    expect(tester.getTopLeft(find.text('Park anyway')).dy, greaterThanOrEqualTo(question.bottom));
  });

  testWidgets('in Settings it ends with the mount\'s versions (firmware here); from the dot, none', (tester) async {
    const driver = DeviceDriver(driverVersion: '6.0.0~nightly', firmware: '3.47');
    await _pump(tester, _wire(), driver: driver);
    expect(find.text('Versions'), findsNothing);
    await _pump(tester, _wire(), driver: driver, inSettings: true);
    await tester.pump();
    expect(find.text('Firmware'), findsOneWidget);
    expect(find.text('3.47'), findsOneWidget);
  });

  testWidgets('a mount that isn\'t connected shows its state and no controls', (tester) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(find.byKey(const Key('simple-mount-park-parked')), findsNothing);
  });
}
