import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;

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
    return true;
  }

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
    {bool inSettings = false, DeviceDriver? driver, bool planRunning = false}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final mount = _Mount(wire);
  final c = ProviderContainer(overrides: [
    mountProvider.overrideWith(() => mount),
    deviceDriverProvider.overrideWith((ref, type) async => type == 'telescope' ? driver : null),
    if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(home: Scaffold(body: SimpleMountWindow(inSettings: inSettings))),
  ));
  await tester.pump();
  return (c, mount);
}

void main() {
  testWidgets('the mount window: tracking, park and meridian flip, rows of one height', (tester) async {
    final (c, mount) = await _pump(tester, _wire());
    expect(find.text('Mount · EQM-35 Pro'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-mount-tracking-off')));
    await tester.pump();
    expect(mount.calls, ['tracking false']);
    c.read(safetyPoliciesProvider.notifier).setMeridianFlipAuto(true);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mount-flip-off')));
    await tester.pump();
    expect(c.read(safetyPoliciesProvider).meridianFlipAuto, isFalse, reason: 'kept in the profile');
    double row(String label) => tester.getSize(find.byKey(Key('simple-window-row-$label'))).height;
    expect(row('Park'), row('Tracking'));
    expect(row('Meridian flip'), row('Tracking'));
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

  testWidgets('a park that stops short clears Parking… and says so', (tester) async {
    final (c, mount) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
    await tester.pump();
    mount.set(_wire(runtime: 'slewing'));
    await tester.pump();
    expect(find.text('Parking…'), findsOneWidget);
    mount.set(_wire(tracking: false, runtime: 'idle')); // aborted: stopped, not parked
    await tester.pump();
    expect(find.text('Parking…'), findsNothing);
    expect(find.byKey(const Key('simple-mount-park-parked')), findsOneWidget, reason: 'the control is back');
    expect(c.read(simpleMessagesProvider).single.title, "The mount didn't finish parking");
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
