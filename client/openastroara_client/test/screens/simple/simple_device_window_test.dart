import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';

/// A camera with a cooler; records the cooler commands instead of sending them.
class _Camera extends CameraStatusNotifier {
  _Camera(this.json);
  final Map<String, dynamic> json;
  final calls = <(bool, double?)>[];

  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(json);

  @override
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) async {
    calls.add((enabled, targetTemperatureC));
    return true;
  }
}

Map<String, dynamic> _wire({
  String state = 'connected',
  bool cooler = true,
  bool on = true,
  double? setpoint = -10,
}) => {
  'device_id': 'c',
  'name': 'ASI2600MM',
  'state': state,
  'capabilities': {'has_cooler': cooler, 'can_set_temperature': cooler},
  'runtime': {
    'ccd_temperature': -9.8,
    'cooler_power_pct': 34,
    'cooler_on': on,
    'cooler_setpoint_c': setpoint,
  },
};

Future<(ProviderContainer, _Camera)> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire,
) async {
  final camera = _Camera(wire);
  final c = ProviderContainer(
    overrides: [cameraStatusProvider.overrideWith(() => camera)],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(home: Scaffold(body: SimpleCameraWindow())),
    ),
  );
  await tester.pump();
  return (c, camera);
}

void main() {
  testWidgets(
    'the camera window: its name and dot, the cooler, the target and what it reads now',
    (tester) async {
      await _pump(tester, _wire());
      expect(find.text('Camera · ASI2600MM'), findsOneWidget);
      expect(find.text('Cooler'), findsOneWidget);
      expect(find.text('−10 °C'), findsOneWidget, reason: 'the target');
      expect(find.text('−9.8 °C · 34 % power'), findsOneWidget, reason: 'now');
      expect(
        find.text('Disconnect'),
        findsNothing,
        reason: 'connecting lives in Settings › Devices only',
      );
    },
  );

  testWidgets('Off turns the cooler off; On cools to the target', (
    tester,
  ) async {
    final (_, camera) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-camera-cooler-off')));
    await tester.pump();
    expect(camera.calls, [(false, null)]);

    final (c2, off) = await _pump(tester, _wire(on: false, setpoint: null));
    c2.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-15);
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-camera-cooler-on')));
    await tester.pump();
    expect(off.calls, [(true, -15.0)], reason: 'the profile\'s target');
  });

  testWidgets(
    'the target steps by a degree; a running cooler follows it, and plans use it too',
    (tester) async {
      final (c, camera) = await _pump(tester, _wire());
      c.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-10);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-camera-target-warmer')));
      await tester.pump();
      expect(c.read(imagingDefaultsProvider).coolerTargetC, -9);
      expect(camera.calls, [(true, -9.0)]);
      expect(find.text('−9 °C'), findsOneWidget);

      final (c2, off) = await _pump(tester, _wire(on: false, setpoint: null));
      c2.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-10);
      await tester.pump();
      await tester.tap(find.byKey(const Key('simple-camera-target-colder')));
      await tester.pump();
      expect(c2.read(imagingDefaultsProvider).coolerTargetC, -11);
      expect(off.calls, isEmpty, reason: 'a cooler that is off stays off');
    },
  );

  testWidgets(
    'a camera without a cooler says so and still reads its temperature',
    (tester) async {
      await _pump(tester, _wire(cooler: false, on: false, setpoint: null));
      expect(find.text('No cooler'), findsOneWidget);
      expect(find.byKey(const Key('simple-camera-cooler-on')), findsNothing);
      expect(find.textContaining('−9.8 °C'), findsOneWidget);
    },
  );

  testWidgets(
    'a camera that isn\'t connected shows its state and no controls',
    (tester) async {
      await _pump(tester, _wire(state: 'disconnected'));
      expect(find.text('Not connected'), findsOneWidget);
      expect(find.byKey(const Key('simple-camera-cooler-on')), findsNothing);
    },
  );
}
