import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/switch_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

// #1663: the Camera window's Dew heater row over a fake rig.

class _Rig implements SwitchClient {
  _Rig(this.devices);
  List<SwitchDevice> devices;
  final writes = <String>[];
  var reads = 0;
  Completer<void>? hold;
  Object? refuse;

  @override
  Future<List<SwitchDevice>> getAll() async {
    reads++;
    return devices;
  }

  @override
  Future<void> setValue({required String deviceId, required int portId, required double value}) async {
    writes.add('$deviceId/$portId=$value');
    await hold?.future;
    final e = refuse;
    refuse = null;
    if (e != null) throw e;
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect(String deviceId) async {}
  @override
  Future<void> remove(String deviceId) async {}
  @override
  Future<void> reconnect() async {}
  @override
  Future<void> reconnectDevice(String deviceId) async {}
  @override
  void close() {}
}

class _FixedFan extends CoolingFanNotifier {
  @override
  Future<CoolingFan> build() async => CoolingFan.none;
}

class _Camera extends CameraStatusNotifier {
  _Camera(this.json);
  final Map<String, dynamic> json;
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(json);
}

Map<String, dynamic> _wire({Map<String, dynamic>? heater = const {'device_id': 'sw', 'port_id': 2}}) => {
  'device_id': 'ZWO_SN_A',
  'name': 'ASI2600MM',
  'state': 'connected',
  'capabilities': {'has_cooler': true, 'can_set_temperature': true},
  'runtime': {'ccd_temperature': -9.8, 'cooler_on': true, 'cooler_setpoint_c': -10},
  'dew_heater': ?heater,
};

SwitchPort _port(int id, String name, double value, {double max = 1, bool write = true}) =>
    SwitchPort(id: id, name: name, value: value, min: 0, max: max, canWrite: write, step: 0);

SwitchDevice _heater({double value = 0, double max = 1, bool write = true, bool connected = true}) => SwitchDevice(
  deviceId: 'sw',
  alpacaDeviceNumber: 0,
  name: 'ZWO Dew Heater',
  connectionState: connected ? SwitchConnectionState.connected : SwitchConnectionState.disconnected,
  ports: [_port(0, 'Other', 0), _port(2, 'DewHeater', value, max: max, write: write)],
);

Future<(ProviderContainer, _Rig)> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire,
  SwitchDevice? heater, {
  bool inSettings = false,
}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final rig = _Rig([?heater]);
  final c = ProviderContainer(
    overrides: [
      switchApiProvider.overrideWithValue(rig),
      wsEventStreamProvider.overrideWith((ref) => null),
      simpleModeActiveProvider.overrideWithValue(true),
      cameraStatusProvider.overrideWith(() => _Camera(wire)),
      deviceDriverProvider.overrideWith((ref, type) async => null),
      coolingFanProvider.overrideWith(() => _FixedFan()),
    ],
  );
  addTearDown(c.dispose);
  await c.read(switchListProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(home: Scaffold(body: SingleChildScrollView(child: SimpleCameraWindow(inSettings: inSettings)))),
    ),
  );
  await tester.pumpAndSettle();
  return (c, rig);
}

Finder get _row => find.byKey(const Key('simple-window-row-dewheater'));

void main() {
  testWidgets('no heater in the camera status: no Dew heater row', (tester) async {
    await _pump(tester, _wire(heater: null), _heater());
    expect(find.text('Dew heater'), findsNothing);
  });

  testWidgets('a heater the status names is a Dew heater row; the camera status field parses to it', (tester) async {
    expect(CameraStatus.fromJson(_wire()).dewHeater, const DewHeaterRef(deviceId: 'sw', portId: 2));
    expect(CameraStatus.fromJson(_wire(heater: null)).dewHeater, isNull);
    await _pump(tester, _wire(), _heater());
    expect(find.text('Dew heater'), findsOneWidget);
    expect(tester.getSize(_row).height, greaterThanOrEqualTo(44));
  });

  testWidgets('an on/off port is On / Off segments; a tap writes the port max or min', (tester) async {
    final (c, rig) = await _pump(tester, _wire(), _heater());
    expect(find.byKey(const Key('simple-camera-dewheater')), findsNothing, reason: 'no slider');
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-camera-dewheater-off'))).selected, isTrue);
    await tester.tap(find.byKey(const Key('simple-camera-dewheater-on')));
    await tester.pumpAndSettle();
    expect(rig.writes, ['sw/2=1.0']);
    rig.devices = [_heater(value: 1)];
    await c.read(switchListProvider.notifier).refresh();
    await tester.pump();
    await tester.tap(find.byKey(const Key('simple-camera-dewheater-off')));
    await tester.pumpAndSettle();
    expect(rig.writes.last, 'sw/2=0.0');
  });

  testWidgets('a port with a range is a slider with its level; release writes the rounded level, drag writes nothing', (tester) async {
    final (_, rig) = await _pump(tester, _wire(), _heater(value: 60, max: 100));
    expect(find.text('60 %'), findsOneWidget);
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-camera-dewheater')));
    slider.onChanged!(37.4);
    await tester.pump();
    expect(find.text('37 %'), findsOneWidget, reason: 'the level follows the drag');
    expect(rig.writes, isEmpty);
    slider.onChangeEnd!(37.4);
    await tester.pumpAndSettle();
    expect(rig.writes, ['sw/2=37.0']);
  });

  testWidgets('the row is busy while the write is in flight and shows the asked state', (tester) async {
    final (_, rig) = await _pump(tester, _wire(), _heater());
    rig.hold = Completer<void>();
    await tester.tap(find.byKey(const Key('simple-camera-dewheater-on')));
    await tester.pump();
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-camera-dewheater-on'))).selected, isTrue, reason: 'the asked state');
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-camera-dewheater-off'))).onTap, isNull, reason: 'locked while writing');
    rig.hold!.complete();
    await tester.pumpAndSettle();
  });

  testWidgets('a refused write says why in an error pill', (tester) async {
    final (c, rig) = await _pump(tester, _wire(), _heater());
    rig.refuse = StateError('heater refused');
    await tester.tap(find.byKey(const Key('simple-camera-dewheater-on')));
    await tester.pumpAndSettle();
    final pill = c.read(simpleMessagesProvider).single;
    expect(pill.title, "Couldn't set Dew heater");
    expect(pill.detail, contains('heater refused'));
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('the switch not connected, read-only or absent: no row', (tester) async {
    await _pump(tester, _wire(), _heater(connected: false));
    expect(find.text('Dew heater'), findsNothing);
    await _pump(tester, _wire(), _heater(write: false));
    expect(find.text('Dew heater'), findsNothing);
    await _pump(tester, _wire(), null);
    expect(find.text('Dew heater'), findsNothing);
  });

  testWidgets('the switch list is read as the window opens', (tester) async {
    final (_, rig) = await _pump(tester, _wire(), _heater());
    expect(rig.reads, greaterThanOrEqualTo(2), reason: 'once by the provider, once as the row opened');
  });

  testWidgets('the row shows in Settings and from the dot, and reads as Dew heater with its state', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, _wire(), _heater(), inSettings: true);
    expect(find.text('Dew heater'), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp('Dew heater')), findsOneWidget);
    await _pump(tester, _wire(), _heater());
    expect(find.text('Dew heater'), findsOneWidget);
    handle.dispose();
  });
}
