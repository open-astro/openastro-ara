import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

/// A filter wheel that records what it was asked to do.
class _Wheel extends FilterWheelNotifier {
  _Wheel(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// Another command still in flight: asks are dropped (the notifier's re-entrancy guard).
  bool busy = false;

  /// The wheel refuses the next change.
  bool refuse = false;

  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson(json);

  void set(Map<String, dynamic> next) => state = AsyncData(FilterWheelStatus.fromJson(next));

  @override
  Future<bool> changeFilter(int position) async {
    calls.add('change $position');
    if (refuse) throw StateError('Filter wheel is moving');
    return !busy;
  }

  @override
  Future<bool> reportInstalled(int position) async {
    calls.add('installed $position');
    return true;
  }

  @override
  Future<bool> cancelManualSwap() async {
    calls.add('cancel');
    return true;
  }
}

Map<String, dynamic> _wire({
  String state = 'connected',
  String runtime = 'idle',
  int current = 0,
  bool manual = false,
  int? pending,
}) =>
    {
      'device_id': 'w',
      'name': 'EFW 7',
      'state': state,
      'manual': manual,
      'slots': [
        {'position': 0, 'name': 'L'},
        {'position': 1, 'name': 'Ha'},
        {'position': 2, 'name': ''},
      ],
      'runtime': {'state': runtime, 'current_slot': current, 'pending_slot': ?pending},
    };

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async =>
      const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running);
}

Future<(ProviderContainer, _Wheel)> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {bool inSettings = false, DeviceDriver? driver, bool planRunning = false}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final wheel = _Wheel(wire);
  final c = ProviderContainer(overrides: [
    filterWheelProvider.overrideWith(() => wheel),
    simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
    deviceDriverProvider.overrideWith((ref, type) async => type == 'filterwheel' ? driver : null),
    if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(home: Scaffold(body: SimpleFilterWheelWindow(inSettings: inSettings))),
  ));
  await tester.pump();
  return (c, wheel);
}

Finder _slot(int p) => find.byKey(Key('simple-filterwheel-slot-$p'));

void main() {
  testWidgets('the filter in place by name; a tap on a filter moves the wheel to it', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    expect(find.text('Filter wheel · EFW 7'), findsOneWidget);
    expect(tester.widget<SimpleSegment>(_slot(0)).selected, isTrue);
    expect(find.text('Slot 2'), findsOneWidget, reason: 'an unnamed slot by its number');
    await tester.tap(_slot(1));
    await tester.pump();
    expect(wheel.calls, ['change 1']);
  });

  testWidgets('it reads Moving to … until the wheel lands, and takes no tap meanwhile', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    await tester.tap(_slot(1));
    await tester.pump();
    expect(find.text('Moving to Ha…'), findsOneWidget, reason: 'before the wheel reports moving');
    wheel.set(_wire(runtime: 'moving', current: -1));
    await tester.pump();
    expect(find.text('Moving to Ha…'), findsOneWidget);
    await tester.tap(_slot(0));
    await tester.pump();
    expect(wheel.calls, ['change 1'], reason: 'no second move while it turns');
    wheel.set(_wire(current: 1));
    await tester.pump();
    expect(find.text('Moving to Ha…'), findsNothing);
    expect(tester.widget<SimpleSegment>(_slot(1)).selected, isTrue);
  });

  testWidgets('a move started elsewhere reads Moving…', (tester) async {
    await _pump(tester, _wire(runtime: 'moving', current: -1));
    expect(find.text('Moving…'), findsOneWidget);
  });

  testWidgets('a refused change says why in a pill', (tester) async {
    final (c, wheel) = await _pump(tester, _wire());
    wheel.refuse = true;
    await tester.tap(_slot(1));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, "Couldn't change the filter"));
    expect(find.text('Moving to Ha…'), findsNothing);
  });

  testWidgets('a tap dropped while the last command runs says so in a pill, once', (tester) async {
    final (c, wheel) = await _pump(tester, _wire());
    wheel.busy = true;
    await tester.tap(_slot(1));
    await tester.pump();
    await tester.tap(_slot(2));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, 'Still busy'));
    expect(find.text('Moving to Ha…'), findsNothing);
    await tester.pump(const Duration(seconds: 11)); // it fades like a result
  });

  testWidgets('while a plan runs, a filter change asks first, in the window', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump(); // the run state arrives
    await tester.tap(_slot(1));
    await tester.pump();
    expect(wheel.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(wheel.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsNothing);
    await tester.tap(_slot(1));
    await tester.pump();
    await tester.tap(find.text('Change anyway'));
    await tester.pump();
    expect(wheel.calls, ['change 1']);
  });

  testWidgets('a manual wheel asks for the swap; confirming reports it installed, Cancel drops it', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(manual: true));
    await tester.tap(_slot(1));
    await tester.pump();
    expect(wheel.calls, ['change 1']);
    wheel.set(_wire(manual: true, runtime: 'awaiting_user', pending: 1));
    await tester.pump();
    expect(find.text('Swap in Ha, then confirm.'), findsOneWidget);
    await tester.tap(find.text('Ha is in'));
    await tester.pump();
    expect(wheel.calls, ['change 1', 'installed 1']);
    await tester.tap(find.text('Cancel swap'));
    await tester.pump();
    expect(wheel.calls, ['change 1', 'installed 1', 'cancel']);
  });

  testWidgets('in Settings it ends with the wheel\'s versions; from the dot, none', (tester) async {
    const driver = DeviceDriver(driverVersion: '6.0.0~nightly', firmware: '1.2');
    await _pump(tester, _wire(), driver: driver);
    expect(find.text('Versions'), findsNothing);
    await _pump(tester, _wire(), driver: driver, inSettings: true);
    await tester.pump();
    expect(find.text('Firmware'), findsOneWidget);
    expect(find.text('1.2'), findsOneWidget);
  });

  testWidgets('a wheel that isn\'t connected shows its state and no controls', (tester) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(_slot(0), findsNothing);
  });
}
