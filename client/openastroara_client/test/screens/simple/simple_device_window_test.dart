import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/imaging/exposure_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';

/// A camera with a cooler; records the cooler commands instead of sending them.
class _FixedFan extends CoolingFanNotifier {
  @override
  Future<CoolingFan> build() async => CoolingFan.none;
}

class _Camera extends CameraStatusNotifier {
  _Camera(this.json);
  final Map<String, dynamic> json;
  final calls = <(bool, double?)>[];

  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(json);

  @override
  Future<bool> setCooler(bool enabled, {double? targetTemperatureC}) async {
    calls.add((enabled, targetTemperatureC));
    return !busy;
  }

  bool busy = false;
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
  Map<String, dynamic> wire, {
  DeviceDriver? driver,
  bool inSettings = false,
}) async {
  // A test that pumps twice lets the first window's providers wind down.
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final camera = _Camera(wire);
  final c = ProviderContainer(
    overrides: [
      cameraStatusProvider.overrideWith(() => camera),
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'camera' ? driver : null,
      ),
      coolingFanProvider.overrideWith(() => _FixedFan()),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        home: Scaffold(body: SimpleCameraWindow(inSettings: inSettings)),
      ),
    ),
  );
  await tester.pump();
  return (c, camera);
}

void main() {
  testWidgets('a cooler tap dropped while the last command runs says so in a pill (ALP-4982)', (tester) async {
    final (c, camera) = await _pump(tester, _wire());
    camera.busy = true;
    await tester.tap(find.byKey(const Key('simple-camera-cooler-off')));
    await tester.pump();
    expect(c.read(simpleMessagesProvider).single.title, 'Still busy');
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets(
    'the camera window: its name and dot, the cooler, the target and what it reads now',
    (tester) async {
      await _pump(tester, _wire());
      expect(find.text('Main Camera · ASI2600MM'), findsOneWidget);
      expect(find.text('Cooler'), findsOneWidget);
      expect(find.text('Sensor temperature'), findsOneWidget, reason: 'what the sensor reads (user, 2026-11-04)');
      expect(find.text('Target temperature'), findsOneWidget);
      expect(find.text('Current temperature'), findsNothing);
      expect(find.text('−10 °C'), findsOneWidget, reason: 'the target');
      expect(find.text('−9.8 °C · 34 % power'), findsOneWidget, reason: 'now');
      // Current temperature, Cooler and Target sit on an even rhythm: the rows are one height.
      double row(String label) =>
          tester.getSize(find.byKey(Key('simple-window-row-$label'))).height;
      expect(row('Current temperature'), row('Cooler'));
      expect(tester.getRect(find.byKey(const Key('simple-window-row-Current temperature'))).bottom,
          lessThanOrEqualTo(tester.getRect(find.byKey(const Key('simple-window-row-Cooler'))).top),
          reason: 'the reading first, above the cooler');
      expect(row('Target'), row('Cooler'));
      // Compact: rows and their sliders sit close, not 12 pt apart.
      final cooler = tester.getRect(find.byKey(const Key('simple-window-row-Cooler')));
      final target = tester.getRect(find.byKey(const Key('simple-window-row-Target')));
      expect(target.top - cooler.bottom, lessThanOrEqualTo(4));
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

  testWidgets('the target is a stepped slider (5 °C steps, −40 to +10); letting go sets it, a running cooler '
      'follows and plans use it too', (tester) async {
    final (c, camera) = await _pump(tester, _wire());
    c.read(imagingDefaultsProvider.notifier).setCoolerTargetC(-10);
    await tester.pump();
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-camera-target')));
    expect((slider.min, slider.max, slider.divisions, slider.value), (-40.0, 10.0, 10, -10.0));
    slider.onChanged!(-15);
    await tester.pump();
    expect(find.text('−15 °C'), findsOneWidget, reason: 'the value follows the thumb');
    expect(camera.calls, isEmpty, reason: 'nothing is sent while dragging');
    tester.widget<Slider>(find.byKey(const Key('simple-camera-target'))).onChangeEnd!(-15);
    await tester.pump();
    expect(c.read(imagingDefaultsProvider).coolerTargetC, -15);
    expect(camera.calls, [(true, -15.0)]);

  });

  testWidgets('the target shows only while the cooler is on', (tester) async {
    await _pump(tester, _wire(on: false, setpoint: null));
    expect(find.byKey(const Key('simple-camera-target')), findsNothing);
    expect(find.byKey(const Key('simple-window-row-Target')), findsNothing);
    await _pump(tester, _wire());
    expect(find.byKey(const Key('simple-camera-target')), findsOneWidget);
  });

  testWidgets('Gain is a slider over the camera\'s range: the same gain as Preview\'s chip; plans keep their own', (tester) async {
    final (c, _) = await _pump(tester, {
      ..._wire(),
      'capabilities': {'has_cooler': true, 'can_set_temperature': true, 'min_gain': 0, 'max_gain': 300},
    });
    c.read(exposureControllerProvider.notifier).setGain(100);
    await tester.pump();
    final plans = c.read(imagingDefaultsProvider).defaultGain;
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-camera-gain')));
    expect((slider.min, slider.max, slider.value), (0.0, 300.0, 100.0));
    slider.onChanged!(123);
    await tester.pump();
    expect(find.text('120'), findsOneWidget, reason: 'steps of 10');
    tester.widget<Slider>(find.byKey(const Key('simple-camera-gain'))).onChangeEnd!(127);
    await tester.pump();
    expect(c.read(exposureControllerProvider).gain, 130, reason: 'steps of 10; Preview\'s chip shows it too');
    expect(c.read(imagingDefaultsProvider).defaultGain, plans, reason: 'the plans\' gain is its own setting');
  });

  testWidgets('a camera with named gain modes slides through the names', (tester) async {
    final (c, _) = await _pump(tester, {
      ..._wire(),
      'capabilities': {'has_cooler': true, 'can_set_temperature': true, 'gains': ['Low', 'Medium', 'High']},
    });
    c.read(exposureControllerProvider.notifier).setGain(1);
    await tester.pump();
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-camera-gain')));
    expect((slider.min, slider.max, slider.divisions), (0.0, 2.0, 2));
    expect(find.text('Medium'), findsOneWidget);
  });

  testWidgets('in Settings the window ends with a Versions table like Rig\'s; from the dot it shows none', (tester) async {
    const driver = DeviceDriver(driverVersion: '6.0.0~nightly', sdkVersion: '1.41.0.0', serialNumber: '0c190e111d020900');
    await _pump(tester, _wire(), driver: driver);
    await tester.pump();
    expect(find.text('Versions'), findsNothing, reason: 'the short window from the dot stays short');

    await _pump(tester, _wire(), driver: driver, inSettings: true);
    await tester.pump();
    expect(tester.widget<Text>(find.text('Versions')).style!.fontWeight, FontWeight.w600);
    for (final (name, value) in const [
      ('SDK', '1.41.0.0'),
      ('Driver', '6.0.0~nightly'),
      ('Serial number', '0c190e111d020900'),
    ]) {
      final n = tester.getRect(find.text(name));
      final v = tester.getRect(find.text(value));
      expect(v.left - n.right, lessThan(80), reason: '$value beside its name');
      expect((v.center.dy - n.center.dy).abs(), lessThan(2), reason: 'one row each');
      expect(tester.widget<Text>(find.text(value)).style!.fontSize, greaterThanOrEqualTo(14), reason: 'readable');
    }
    expect(find.text('Firmware'), findsNothing, reason: 'only what the rig reports');
  });

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

  testWidgets('the Cooling fan picker sits in the Camera window of a cooling camera', (tester) async {
    await _pump(tester, _wire());
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('cooling-fan-picker')), findsOneWidget);
    expect(find.text('Cooling fan'), findsOneWidget);
    expect(find.text('No fan is chosen, so Ara does not run a fan with the cooler.'), findsOneWidget);
  });

  testWidgets('a camera with no cooler has no Cooling fan picker', (tester) async {
    await _pump(tester, _wire(cooler: false));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('cooling-fan-picker')), findsNothing);
  });

  testWidgets('the Camera window picks its Cooling fan inline, no dropdown route', (tester) async {
    final (c, _) = await _pump(tester, _wire());
    await tester.pumpAndSettle();
    expect(find.byType(DropdownButton<CoolingFan>), findsNothing);
    final none = find.byKey(const Key('cooling-fan-picker-0'));
    await tester.ensureVisible(none);
    expect(tester.getSize(none).height, greaterThanOrEqualTo(44));
    expect(tester.state<NavigatorState>(find.byType(Navigator)).canPop(), isFalse);
    expect(c.read(coolingFanProvider).value, CoolingFan.none);
  });
}
