import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
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

SwitchPort _port(int id, String name, double value, {double min = 0, double max = 1, bool write = true}) =>
    SwitchPort(id: id, name: name, value: value, min: min, max: max, canWrite: write);

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

Future<(ProviderContainer, _Rig)> _pump(WidgetTester tester, List<SwitchDevice> devices) async {
  final rig = _Rig(devices);
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_Servers()),
    switchApiFactoryProvider.overrideWithValue((_) => rig),
    // A server would otherwise dial a real event stream.
    wsEventStreamProvider.overrideWith((ref) => null),
    simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
  ]);
  addTearDown(c.dispose);
  await c.read(switchListProvider.future);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: const MaterialApp(
      home: Scaffold(body: Align(alignment: Alignment.topLeft, child: SimpleSwitchesWindow())),
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

  testWidgets('a port that cannot be written shows its value', (tester) async {
    await _pump(tester, [
      _box(ports: [_port(2, 'Input voltage', 12.4, max: 20, write: false), _port(3, 'Hub', 1, write: false)]),
    ]);
    expect(find.text('12.4'), findsOneWidget);
    expect(find.text('On'), findsOneWidget, reason: 'a read-only on/off reads On or Off');
    expect(find.byType(Slider), findsNothing);
    expect(find.byType(SimpleSegment), findsNothing);
  });

  testWidgets("the camera's cooling fan isn't listed; each device has its titled section", (tester) async {
    await _pump(tester, [
      _box(),
      _box(id: 'ts', name: 'ToupTek Thermal Switch', ports: [_port(0, 'Fan', 1), _port(1, 'Heater', 0, max: 5)]),
    ]);
    expect(find.text('Pegasus Powerbox · 3 ports'), findsOneWidget);
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

  testWidgets('while a write is on its way the port shows a ring and every other control is locked', (tester) async {
    final (c, rig) = await _pump(tester, [_box()]);
    rig.hold = Completer();
    await tester.tap(_segment('box-0', 'off'));
    await tester.pump();
    final row = find.byKey(const Key('simple-window-row-switch-box-0'));
    final ring = find.descendant(of: row, matching: find.byType(StatusDot));
    expect(tester.widget<StatusDot>(ring).ring, isTrue);
    expect(tester.widget<SimpleSegment>(_segment('box-0', 'off')).selected, isTrue, reason: 'shows what was asked');
    expect(tester.widget<SimpleSegment>(_segment('box-0', 'on')).onTap, isNull);
    expect(tester.widget<Slider>(_slider('box-1')).onChanged, isNull);
    expect(c.read(simpleSwitchSettingProvider), '12V Out 1', reason: 'the status pill names the port');
    rig.devices = [
      _box(ports: [_port(0, '12V Out 1', 0), _port(1, 'Dew 1', 60, max: 100)]),
    ];
    rig.hold!.complete();
    await tester.pumpAndSettle();
    expect(ring, findsNothing);
    expect(tester.widget<Slider>(_slider('box-1')).onChanged, isNotNull);
    expect(c.read(simpleSwitchSettingProvider), isNull);
  });

  testWidgets('locked On / Off segments look dimmed, not only untappable', (tester) async {
    final (_, rig) = await _pump(tester, [
      _box(ports: [_port(0, '12V Out 1', 1), _port(1, '12V Out 2', 0)]),
    ]);
    double opacity(String port) {
      final o = find.ancestor(of: _segment(port, 'on'), matching: find.byType(Opacity));
      return o.evaluate().isEmpty ? 1 : tester.widget<Opacity>(o.first).opacity;
    }
    expect(opacity('box-1'), 1);
    rig.hold = Completer();
    await tester.tap(_segment('box-0', 'off'));
    await tester.pump();
    expect(opacity('box-1'), lessThan(1), reason: 'the other port waits, and shows it');
    rig.hold!.complete();
    await tester.pumpAndSettle();
    expect(opacity('box-1'), 1);
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
