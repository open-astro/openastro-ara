import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/switch_api.dart';
import 'package:openastroara/state/equipment/switch_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusDot;

class _Servers implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

/// The rig's switches: records each write and read; a write can be held open.
class _Rig implements SwitchClient {
  _Rig(this.devices);
  List<SwitchDevice> devices;
  final writes = <String>[];
  var reads = 0;

  /// Holds the next write until completed.
  Completer<void>? hold;

  /// Thrown by the next write.
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

SwitchPort _port(int id, String name, double value, {double min = 0, double max = 1, bool write = true, double step = 0}) =>
    SwitchPort(id: id, name: name, value: value, min: min, max: max, canWrite: write, step: step);

SwitchDevice _box({String id = 'box', String name = 'Pegasus Powerbox', List<SwitchPort>? ports}) => SwitchDevice(
      deviceId: id,
      alpacaDeviceNumber: 0,
      name: name,
      connectionState: SwitchConnectionState.connected,
      ports: ports ??
          [
            _port(0, '12V Out 1', 1),
            _port(1, 'Dew 1', 60, max: 100),
            _port(2, 'Input voltage', 12.4, max: 20, write: false),
          ],
    );

Future<(ProviderContainer, _Rig)> _pump(WidgetTester tester, List<SwitchDevice> devices,
    {bool inSettings = false, Map<String, DeviceDriver> drivers = const {}}) async {
  final rig = _Rig(devices);
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_Servers()),
    switchApiFactoryProvider.overrideWithValue((_) => rig),
    // A server would otherwise dial a real event stream.
    wsEventStreamProvider.overrideWith((ref) => null),
    simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
    deviceDriverProvider.overrideWith((ref, key) async => drivers[key]),
  ]);
  addTearDown(c.dispose);
  await c.read(switchListProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(body: Align(alignment: Alignment.topLeft, child: SimpleSwitchesWindow(inSettings: inSettings))),
    ),
  ));
  await tester.pumpAndSettle();
  return (c, rig);
}

Finder _segment(String port, String which) => find.byKey(Key('simple-switch-$port-$which'));
Finder _slider(String port) => find.byKey(Key('simple-switch-$port'));

void main() {
  testWidgets('an on/off port is On / Off segments, and a tap writes 1 or 0', (tester) async {
    final (_, rig) = await _pump(tester, [_box()]);
    expect(find.text('12V Out 1'), findsOneWidget);
    expect(tester.widget<SimpleSegment>(_segment('box-0', 'on')).selected, isTrue);
    await tester.tap(_segment('box-0', 'off'));
    await tester.pumpAndSettle();
    expect(rig.writes, ['box/0=0.0']);
  });

  testWidgets('a port with a range is a slider with its level by the name; release writes a whole number', (tester) async {
    final (_, rig) = await _pump(tester, [_box()]);
    expect(find.text('Dew 1 · 60'), findsOneWidget);
    final slider = tester.widget<Slider>(_slider('box-1'));
    slider.onChanged!(37.4);
    await tester.pump();
    expect(find.text('Dew 1 · 37'), findsOneWidget, reason: 'the level follows the drag');
    expect(rig.writes, isEmpty, reason: 'nothing is sent while dragging');
    slider.onChangeEnd!(37.4);
    await tester.pumpAndSettle();
    expect(rig.writes, ['box/1=37.0']);
  });

  testWidgets('a narrow range is written to 0.01', (tester) async {
    final (_, rig) = await _pump(tester, [
      _box(ports: [_port(4, 'Heater', 2.5, max: 5)]),
    ]);
    expect(find.text('Heater · 2.5'), findsOneWidget);
    tester.widget<Slider>(_slider('box-4')).onChangeEnd!(1.23456);
    await tester.pumpAndSettle();
    expect(rig.writes, ['box/4=1.23']);
  });

  testWidgets("a port with a step stops on it and writes only values on it (#1680)", (tester) async {
    final (_, rig) = await _pump(tester, [
      _box(ports: [_port(0, 'Light Box', 40, max: 100, step: 10), _port(1, 'Temperature', 5, max: 15, step: 0.1)]),
    ]);
    final light = tester.widget<Slider>(_slider('box-0'));
    expect(light.divisions, 10);
    light.onChanged!(37);
    await tester.pump();
    expect(find.text('Light Box · 40'), findsOneWidget, reason: 'the level reads the value that will be sent');
    light.onChangeEnd!(37);
    await tester.pumpAndSettle();
    tester.widget<Slider>(_slider('box-1')).onChangeEnd!(8.47);
    await tester.pumpAndSettle();
    expect(rig.writes, ['box/0=40.0', 'box/1=8.5']);
  });

  testWidgets("in Settings each device ends with its readings under Sensors and its versions, like any device", (tester) async {
    await _pump(
      tester,
      [
        _box(),
        _box(id: 'meter', name: 'Weather meter', ports: [_port(0, 'Sky quality', 21.3, max: 25, write: false)]),
      ],
      inSettings: true,
      drivers: const {'switch/box': DeviceDriver(driverVersion: '2.1', firmware: '1.4', serialNumber: 'PB-77')},
    );
    final box = find.byKey(const Key('simple-switch-device-box'));
    expect(find.descendant(of: box, matching: find.text('Sensors')), findsOneWidget);
    expect(find.descendant(of: box, matching: find.text('Input voltage')), findsOneWidget);
    expect(find.descendant(of: box, matching: find.text('12.4')), findsOneWidget);
    for (final t in ['Driver', '2.1', 'Firmware', '1.4', 'Serial number', 'PB-77']) {
      expect(find.descendant(of: box, matching: find.text(t)), findsOneWidget, reason: t);
    }
    final meter = find.byKey(const Key('simple-switch-device-meter'));
    expect(find.descendant(of: meter, matching: find.text('Weather meter')), findsOneWidget,
        reason: 'a device with only readings is still listed in Settings');
    expect(find.descendant(of: meter, matching: find.text('21.3')), findsOneWidget);
    expect(find.text('Nothing to switch'), findsNothing);
  });

  testWidgets('from the dot the window shows neither readings nor versions', (tester) async {
    await _pump(tester, [_box()], drivers: const {'switch/box': DeviceDriver(driverVersion: '2.1')});
    expect(find.text('Sensors'), findsNothing);
    expect(find.text('Driver'), findsNothing);
  });

  testWidgets("a port that can't be set isn't listed: the window shows what you can switch", (tester) async {
    await _pump(tester, [
      _box(ports: [_port(2, 'Input voltage', 12.4, max: 20, write: false), _port(0, '12V Out 1', 1)]),
      _box(id: 'env', name: 'Env box', ports: [_port(0, 'Humidity', 70, max: 100, write: false)]),
    ]);
    expect(find.text('Input voltage'), findsNothing);
    expect(find.text('12.4'), findsNothing);
    expect(find.text('Pegasus Powerbox · 1 port'), findsOneWidget, reason: 'the count is of what it lists');
    expect(find.textContaining('Env box'), findsNothing, reason: 'nothing to switch on it');
  });

  testWidgets('connected devices with nothing to switch say so', (tester) async {
    await _pump(tester, [
      _box(id: 'env', name: 'Env box', ports: [_port(0, 'Humidity', 70, max: 100, write: false)]),
    ]);
    expect(find.text('Nothing to switch'), findsOneWidget);
    expect(find.text('Not connected'), findsNothing);
  });

  testWidgets("the camera's cooling fan isn't listed; each device has its titled section", (tester) async {
    await _pump(tester, [
      _box(),
      _box(id: 'ts', name: 'ToupTek Thermal Switch', ports: [_port(0, 'Fan', 1), _port(1, 'Heater', 0, max: 5)]),
    ]);
    expect(find.text('Pegasus Powerbox · 2 ports'), findsOneWidget);
    expect(find.text('ToupTek Thermal Switch · 1 port'), findsOneWidget);
    expect(find.text('Fan'), findsNothing);
    expect(find.text('Heater · 0'), findsOneWidget);
  });

  testWidgets('a disconnected device is not listed', (tester) async {
    await _pump(tester, [
      SwitchDevice(
          deviceId: 'off',
          alpacaDeviceNumber: 1,
          name: 'Spare box',
          connectionState: SwitchConnectionState.disconnected,
          ports: [_port(0, 'Out', 0)]),
    ]);
    expect(find.textContaining('Spare box'), findsNothing);
    expect(find.text('Not connected'), findsOneWidget);
  });

  testWidgets('while a write is on its way the port shows what was asked and every other control is locked', (tester) async {
    final (c, rig) = await _pump(tester, [_box()]);
    rig.hold = Completer();
    await tester.tap(_segment('box-0', 'off'));
    await tester.pump();
    expect(tester.widget<SimpleSegment>(_segment('box-0', 'off')).selected, isTrue, reason: 'shows what was asked');
    expect(tester.widget<SimpleSegment>(_segment('box-0', 'on')).onTap, isNull);
    tester.widget<Slider>(_slider('box-1')).onChangeEnd!(10);
    await tester.pump();
    expect(rig.writes, ['box/0=0.0'], reason: 'locked: no second write');
    await tester.pump(const Duration(milliseconds: 300));
    expect(tester.widget<Slider>(_slider('box-1')).onChanged, isNull, reason: 'a slow answer: the slider reads locked');
    expect(c.read(simpleSwitchSettingProvider), '12V Out 1', reason: 'the status pill names the port');
    rig.devices = [
      _box(ports: [_port(0, '12V Out 1', 0), _port(1, 'Dew 1', 60, max: 100)]),
    ];
    rig.hold!.complete();
    await tester.pumpAndSettle();
    expect(tester.widget<Slider>(_slider('box-1')).onChanged, isNotNull);
    expect(c.read(simpleSwitchSettingProvider), isNull);
  });

  testWidgets('a drag cut short by a write elsewhere goes back to what the rig has', (tester) async {
    final (c, rig) = await _pump(tester, [_box()]);
    // A finger is on the slider when another write starts (a second finger,
    // or the Settings copy of the window).
    tester.widget<Slider>(_slider('box-1')).onChanged!(80);
    await tester.pump();
    expect(find.text('Dew 1 · 80'), findsOneWidget);
    rig.hold = Completer();
    final other = c.read(switchListProvider.notifier).setValue(deviceId: 'box', portId: 0, value: 0);
    await tester.pump();
    tester.widget<Slider>(_slider('box-1')).onChangeEnd!(80);
    await tester.pump(const Duration(milliseconds: 300));
    rig.hold!.complete();
    await other;
    await tester.pumpAndSettle();
    expect(rig.writes, ['box/0=0.0'], reason: 'locked: the release sends nothing');
    expect(find.text('Dew 1 · 60'), findsOneWidget, reason: "the slider shows the rig's value, not the dropped drag");
  });

  testWidgets("a write puts no ring by the port: the name doesn't move, the status pill says it", (tester) async {
    final (_, rig) = await _pump(tester, [_box()]);
    final row = find.byKey(const Key('simple-window-row-switch-box-0'));
    final ring = find.descendant(of: row, matching: find.byType(StatusDot));
    final at = tester.getTopLeft(find.text('12V Out 1'));
    rig.hold = Completer();
    await tester.tap(_segment('box-0', 'off'));
    await tester.pump();
    expect(ring, findsNothing);
    await tester.pump(const Duration(milliseconds: 300));
    expect(ring, findsNothing, reason: 'not on a slow answer either');
    expect(tester.getTopLeft(find.text('12V Out 1')), at);
    rig.hold!.complete();
    await tester.pumpAndSettle();
  });

  testWidgets('a slow answer dims the other ports after 300 ms; a fast one never does', (tester) async {
    final (_, rig) = await _pump(tester, [
      _box(ports: [_port(0, '12V Out 1', 1), _port(1, '12V Out 2', 0)]),
    ]);
    double opacity(String port) => find
        .ancestor(of: _segment(port, 'on'), matching: find.byType(Opacity))
        .evaluate()
        .map((e) => (e.widget as Opacity).opacity)
        .fold(1.0, (a, b) => a * b);
    rig.hold = Completer();
    await tester.tap(_segment('box-0', 'off'));
    await tester.pump();
    expect(opacity('box-1'), 1, reason: 'no flash on each write');
    await tester.pump(const Duration(milliseconds: 299));
    expect(opacity('box-1'), 1);
    await tester.pump(const Duration(milliseconds: 1));
    expect(opacity('box-1'), lessThan(1), reason: 'still waiting: the other port shows it');
    expect(opacity('box-0'), 1, reason: 'the asked port stays bright beside its ring');
    rig.hold!.complete();
    await tester.pumpAndSettle();
    expect(opacity('box-1'), 1);
    // The rig answers within 300 ms: nothing dims.
    rig.hold = Completer();
    await tester.tap(_segment('box-1', 'on'));
    await tester.pump(const Duration(milliseconds: 100));
    rig.hold!.complete();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 300));
    expect(opacity('box-0'), 1);
  });

  testWidgets('a port whose range is one value is not offered', (tester) async {
    await _pump(tester, [
      _box(ports: [_port(0, 'Fixed', 5, min: 5, max: 5), _port(1, '12V Out 1', 1)]),
    ]);
    expect(tester.takeException(), isNull);
    expect(find.textContaining('Fixed'), findsNothing);
    expect(find.text('Pegasus Powerbox · 1 port'), findsOneWidget);
  });

  test('a reading shows no ".0" however close it is to a whole number', () {
    SwitchPort reading(double v) => _port(0, 'V', v, max: 20, write: false);
    final l10n = lookupAppLocalizations(const Locale('en'));
    expect(simpleSwitchReading(l10n, reading(0.999)), '1');
    expect(simpleSwitchReading(l10n, reading(12.996)), '13');
    expect(simpleSwitchReading(l10n, reading(0.004)), '0');
    expect(simpleSwitchReading(l10n, reading(12.4)), '12.4');
    expect(simpleSwitchReading(l10n, reading(1.23)), '1.23');
  });

  testWidgets('a refused write says why in a pill and the port keeps what the rig reports', (tester) async {
    final (c, rig) = await _pump(tester, [_box()]);
    rig.refuse = StateError('port over its current limit');
    tester.widget<Slider>(_slider('box-1')).onChangeEnd!(90);
    await tester.pumpAndSettle();
    final m = c.read(simpleMessagesProvider).single;
    expect(m.kind, SimpleMessageKind.error);
    expect(m.title, "Couldn't set Dew 1");
    expect(m.detail, contains('current limit'));
    expect(find.text('Dew 1 · 60'), findsOneWidget);
  });

  testWidgets('the window re-reads the ports when it opens and after each write', (tester) async {
    final (_, rig) = await _pump(tester, [_box()]);
    expect(rig.reads, 2, reason: 'the list read, then the window opening');
    await tester.tap(_segment('box-0', 'off'));
    await tester.pumpAndSettle();
    expect(rig.reads, 3);
  });
}
