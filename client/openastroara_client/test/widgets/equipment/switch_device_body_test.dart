import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/widgets/equipment/switch_device_body.dart';

SwitchPort _port(
  int id,
  String name, {
  double value = 0,
  double min = 0,
  double max = 1,
  bool canWrite = true,
  double step = 0,
}) =>
    SwitchPort(
      id: id,
      name: name,
      value: value,
      min: min,
      max: max,
      canWrite: canWrite,
      step: step,
    );

/// The shape a Gemini Power & Data Hub ADV3 actually reports — the case this
/// layout exists for: telemetry, two prefix-grouped rails, a dew channel with
/// its mode companion, and sensor status bits, all in one flat ASCOM list.
SwitchDevice _powerBox() => SwitchDevice(
      deviceId: 'PDH',
      alpacaDeviceNumber: 0,
      name: 'Gemini Power & Data Hubs Advanced 3',
      connectionState: SwitchConnectionState.connected,
      ports: [
        _port(0, 'USB A', value: 1),
        _port(1, 'USB B', value: 1),
        _port(6, 'DC1', value: 1, canWrite: false),
        _port(7, 'DC2', value: 1),
        _port(11, 'DEW6', value: 0, max: 100),
        _port(13, 'DEW6 Mode', value: 1, max: 2),
        _port(15, 'Input Voltage', value: 12.6, max: 30, canWrite: false),
        _port(22, 'AHT20 Sensor', value: 1, canWrite: false),
      ],
    );

Future<void> _pump(
  WidgetTester tester,
  SwitchDevice device, {
  SwitchPortWriter? onWrite,
}) async {
  await tester.pumpWidget(MaterialApp(
    home: Scaffold(
      body: SingleChildScrollView(
        child: SwitchDeviceBody(
          device: device,
          onWrite: onWrite ?? (_, _) async => true,
        ),
      ),
    ),
  ));
}

void main() {
  testWidgets('telemetry renders as a value tile with its inferred unit',
      (tester) async {
    await _pump(tester, _powerBox());
    expect(find.text('12.6'), findsOneWidget);
    expect(find.text('V'), findsOneWidget);
    expect(find.text('Input Voltage'), findsOneWidget);
  });

  testWidgets('ports sharing a prefix get their own labelled group',
      (tester) async {
    await _pump(tester, _powerBox());
    expect(find.text('USB'), findsOneWidget);
    expect(find.text('DC'), findsOneWidget);
  });

  testWidgets('a read-only rail reads as "Always on", not a dead switch',
      (tester) async {
    await _pump(tester, _powerBox());
    expect(find.text('Always on'), findsOneWidget);
    // Three writable two-state ports (USB A, USB B, DC2) — DC1 contributes none.
    expect(find.byType(Switch), findsNWidgets(3));
  });

  testWidgets('a read-only status bit renders as a sensor pill', (tester) async {
    await _pump(tester, _powerBox());
    expect(find.text('AHT20 Sensor'), findsOneWidget);
    expect(find.text('Attached'), findsOneWidget);
  });

  testWidgets('a level port shows its value and a mode picker', (tester) async {
    await _pump(tester, _powerBox());
    expect(find.text('DEW6'), findsOneWidget);
    expect(find.text('0 %'), findsOneWidget);
    // The companion mode port is folded into the level card, never a row.
    expect(find.text('DEW6 MODE'), findsNothing);
    for (final label in ['Auto', 'Manual', 'Switch']) {
      expect(find.text(label), findsOneWidget);
    }
  });

  testWidgets('toggling a port writes its max', (tester) async {
    final writes = <(int, double)>[];
    await _pump(
      tester,
      _powerBox(),
      onWrite: (port, value) async {
        writes.add((port.id, value));
        return true;
      },
    );
    // USB A is on; tapping drives it to min.
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(writes, [(0, 0.0)]);
  });

  testWidgets('choosing a mode writes the segment value', (tester) async {
    final writes = <(int, double)>[];
    await _pump(
      tester,
      _powerBox(),
      onWrite: (port, value) async {
        writes.add((port.id, value));
        return true;
      },
    );
    await tester.tap(find.text('Auto'));
    await tester.pumpAndSettle();
    expect(writes, [(13, 0.0)]);
  });

  testWidgets('a device with no ports renders nothing rather than throwing',
      (tester) async {
    await _pump(
      tester,
      const SwitchDevice(
        deviceId: 'empty',
        alpacaDeviceNumber: 0,
        name: 'Empty',
        connectionState: SwitchConnectionState.connected,
        ports: [],
      ),
    );
    expect(find.byType(Switch), findsNothing);
  });

  testWidgets('a refused write snaps the slider back off the rejected value',
      (tester) async {
    // The safety case: a fan-off refused by the cooling interlock must not
    // leave the thumb parked at "off" while the fan is still running.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'refuse',
        alpacaDeviceNumber: 0,
        name: 'Refusing',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(11, 'DEW6', value: 100, max: 100)],
      ),
      onWrite: (_, _) async => false,
    );
    expect(find.text('100 %'), findsOneWidget);
    await tester.drag(find.byType(Slider), const Offset(-400, 0));
    await tester.pumpAndSettle();
    // The device value never changed, so the card must still read 100.
    expect(find.text('100 %'), findsOneWidget);
  });

  testWidgets('a committed write resyncs to the value the device confirms',
      (tester) async {
    // The card must NOT keep showing the dragged number after the write is
    // answered: the notifier refreshes before returning and the daemon re-reads
    // the device right after the PUT, so by now the confirmed value has landed.
    // Holding the drag would strand the card on a number the device may have
    // clamped or quantised away from, with no later transition to correct it.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'commit',
        alpacaDeviceNumber: 0,
        name: 'Committing',
        connectionState: SwitchConnectionState.connected,
        // The device holds 100 regardless of what is written — the clamping
        // case that used to strand the card.
        ports: [_port(11, 'DEW6', value: 100, max: 100)],
      ),
      onWrite: (_, _) async => true,
    );
    await tester.drag(find.byType(Slider), const Offset(-400, 0));
    await tester.pumpAndSettle();
    expect(find.text('100 %'), findsOneWidget);
  });

  testWidgets('an Auto dew channel is a readout, not a control', (tester) async {
    // The device regulates from its own dew-point maths in Auto; offering a
    // live control invites the user to fight it, and the help promises a
    // readout.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'auto',
        alpacaDeviceNumber: 0,
        name: 'Auto',
        connectionState: SwitchConnectionState.connected,
        ports: [
          _port(11, 'DEW6', value: 42, max: 100),
          _port(13, 'DEW6 Mode', value: 0, max: 2),
        ],
      ),
    );
    expect(find.byType(Slider), findsNothing);
    expect(find.text('Set automatically'), findsOneWidget);
    expect(find.text('42 %'), findsOneWidget);
    // The mode itself stays switchable, so the user can take manual control.
    expect(find.text('Manual'), findsOneWidget);
  });

  testWidgets('a Manual dew channel keeps its slider', (tester) async {
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'manual',
        alpacaDeviceNumber: 0,
        name: 'Manual',
        connectionState: SwitchConnectionState.connected,
        ports: [
          _port(11, 'DEW6', value: 42, max: 100),
          _port(13, 'DEW6 Mode', value: 1, max: 2),
        ],
      ),
    );
    expect(find.byType(Slider), findsOneWidget);
    expect(find.text('Set automatically'), findsNothing);
  });

  testWidgets('a slider write is rounded to a whole step', (tester) async {
    final writes = <double>[];
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'round',
        alpacaDeviceNumber: 0,
        name: 'Round',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(11, 'Big', value: 0, max: 65535)],
      ),
      onWrite: (_, value) async {
        writes.add(value);
        return true;
      },
    );
    await tester.drag(find.byType(Slider), const Offset(200, 0));
    await tester.pumpAndSettle();
    expect(writes, hasLength(1));
    expect(writes.single, writes.single.roundToDouble());
  });

  testWidgets("the slider stops on the device's step (#1680)", (tester) async {
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'flat',
        alpacaDeviceNumber: 0,
        name: 'Flat panel',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(3, 'Light Box', value: 40, max: 100, step: 10)],
      ),
    );
    expect(tester.widget<Slider>(find.byType(Slider)).divisions, 10);
  });

  testWidgets('an Auto boolean channel reads On/Off, not a bare 1',
      (tester) async {
    // The ADV3 reports the output as 0..1 in Auto, where "1" alone means
    // nothing to the reader.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'autobool',
        alpacaDeviceNumber: 0,
        name: 'Auto bool',
        connectionState: SwitchConnectionState.connected,
        ports: [
          _port(11, 'DEW6', value: 1),
          _port(13, 'DEW6 Mode', value: 0, max: 2),
        ],
      ),
    );
    expect(find.text('On'), findsOneWidget);
    expect(find.text('1'), findsNothing);
  });

  testWidgets('the Readings strip survives OS text scaling', (tester) async {
    // A fixed tile height overflowed at 1.15x (Windows 125%, macOS
    // accessibility sizes), striping the whole strip.
    await tester.pumpWidget(MaterialApp(
      home: MediaQuery(
        data: const MediaQueryData(textScaler: TextScaler.linear(1.3)),
        child: Scaffold(
          body: SingleChildScrollView(
            child: SwitchDeviceBody(
              device: SwitchDevice(
                deviceId: 'scaled',
                alpacaDeviceNumber: 0,
                name: 'Scaled',
                connectionState: SwitchConnectionState.connected,
                ports: [
                  _port(15, 'Input Voltage',
                      value: 12.6, max: 30, canWrite: false),
                  _port(18, 'Ambient Temperature Probe',
                      value: 23.5, max: 150, canWrite: false),
                ],
              ),
              onWrite: (_, _) async => true,
            ),
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets('a refresh mid-drag does not snap the thumb', (tester) async {
    // The panel now re-reads every ~3 s on its own; a poll landing during a
    // gesture must not clear the drag value under the user's finger.
    final device = SwitchDevice(
      deviceId: 'drag',
      alpacaDeviceNumber: 0,
      name: 'Drag',
      connectionState: SwitchConnectionState.connected,
      ports: [_port(11, 'DEW6', value: 20, max: 100)],
    );
    await _pump(tester, device);
    final gesture = await tester.startGesture(
      tester.getCenter(find.byType(Slider)),
    );
    await gesture.moveBy(const Offset(120, 0));
    await tester.pump();
    final draggedText = find.textContaining('%');
    final before = tester.widget<Text>(draggedText.first).data;
    // A poll delivers a DIFFERENT confirmed value while the finger is down.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'drag',
        alpacaDeviceNumber: 0,
        name: 'Drag',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(11, 'DEW6', value: 55, max: 100)],
      ),
    );
    await tester.pump();
    expect(tester.widget<Text>(draggedText.first).data, before);
    await gesture.up();
    await tester.pumpAndSettle();
  });

  testWidgets('an unnamed port still shows an identifying label',
      (tester) async {
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'blank',
        alpacaDeviceNumber: 0,
        name: 'Blank',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(4, '', value: 1)],
      ),
    );
    expect(find.text('Port 4'), findsOneWidget);
  });

  testWidgets('a Switch-mode channel keeps its mode picker and shows a toggle',
      (tester) async {
    // The ADV3 reports a dew output as 0..1 outside Manual. In Switch mode the
    // channel is a plain on/off the user drives, and it must stay paired with
    // its mode rather than splitting into a row plus an orphan picker.
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'switchmode',
        alpacaDeviceNumber: 0,
        name: 'Switch mode',
        connectionState: SwitchConnectionState.connected,
        ports: [
          _port(11, 'DEW6', value: 1),
          _port(13, 'DEW6 Mode', value: 2, max: 2),
        ],
      ),
    );
    expect(find.byType(Slider), findsNothing);
    expect(find.byType(Switch), findsOneWidget);
    expect(find.text('Auto'), findsOneWidget);
  });

  testWidgets('a read-only mode companion is telemetry, not a picker',
      (tester) async {
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'ro',
        alpacaDeviceNumber: 0,
        name: 'Read-only mode',
        connectionState: SwitchConnectionState.connected,
        ports: [
          _port(11, 'DEW6', value: 40, max: 100),
          _port(13, 'DEW6 Mode', value: 1, max: 2, canWrite: false),
        ],
      ),
    );
    // Rendered once, as a reading — never as an interactive segmented control.
    expect(find.text('Auto'), findsNothing);
    expect(find.text('DEW6 Mode'), findsOneWidget);
  });

  testWidgets('an orphan mode port still gets a control', (tester) async {
    await _pump(
      tester,
      SwitchDevice(
        deviceId: 'orphan',
        alpacaDeviceNumber: 0,
        name: 'Orphan',
        connectionState: SwitchConnectionState.connected,
        ports: [_port(0, 'Heater Mode', value: 1, max: 2)],
      ),
    );
    expect(find.text('Auto'), findsOneWidget);
  });
}
