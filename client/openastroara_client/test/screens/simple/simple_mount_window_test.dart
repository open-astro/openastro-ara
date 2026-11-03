import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

/// A mount that records what it was asked to do.
class _Mount extends MountNotifier {
  _Mount(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(json);

  @override
  Future<bool> setTracking(bool enabled) async {
    calls.add('tracking $enabled');
    return true;
  }

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

Map<String, dynamic> _wire({bool tracking = true, bool parked = false, String state = 'connected'}) => {
      'device_id': 'm',
      'name': 'EQM-35 Pro',
      'state': state,
      'capabilities': {'can_park': true, 'can_unpark': true, 'can_set_tracking': true, 'can_slew': true},
      'runtime': {'tracking': tracking, 'parked': parked},
    };

Future<(ProviderContainer, _Mount)> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {bool inSettings = false, DeviceDriver? driver}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final mount = _Mount(wire);
  final c = ProviderContainer(overrides: [
    mountProvider.overrideWith(() => mount),
    deviceDriverProvider.overrideWith((ref, type) async => type == 'telescope' ? driver : null),
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
    await tester.tap(find.byKey(const Key('simple-mount-park')));
    await tester.pump();
    expect(mount.calls.last, 'park');
    c.read(safetyPoliciesProvider.notifier).setMeridianFlipAuto(true);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-mount-flip-off')));
    await tester.pump();
    expect(c.read(safetyPoliciesProvider).meridianFlipAuto, isFalse, reason: 'kept in the profile');
    double row(String label) => tester.getSize(find.byKey(Key('simple-window-row-$label'))).height;
    expect(row('Park'), row('Tracking'));
    expect(row('Meridian flip'), row('Tracking'));
  });

  testWidgets('a parked mount offers Unpark', (tester) async {
    final (_, mount) = await _pump(tester, _wire(tracking: false, parked: true));
    expect(find.text('Parked'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-mount-park')));
    await tester.pump();
    expect(mount.calls, ['unpark']);
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
    expect(find.byKey(const Key('simple-mount-park')), findsNothing);
  });
}
