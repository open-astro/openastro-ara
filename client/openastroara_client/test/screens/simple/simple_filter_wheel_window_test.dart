import 'package:flutter/material.dart';
import 'package:flutter/services.dart' show LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/widgets/simple/filter_wheel.dart' show SimpleFilterWheel;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/imaging/exposure_controls_panel.dart' show FilterDropdown;

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

  /// No rig behind it: a re-read keeps what the test set.
  @override
  Future<void> refresh([client]) async {}

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
  String ha = 'Ha',
  int extra = 0,
}) =>
    {
      'device_id': 'w',
      'name': 'EFW 7',
      'state': state,
      'manual': manual,
      'slots': [
        {'position': 0, 'name': 'L'},
        {'position': 1, 'name': ha},
        {'position': 2, 'name': ''},
        for (var i = 0; i < extra; i++) {'position': 3 + i, 'name': 'F${3 + i}'},
      ],
      'runtime': {'state': runtime, 'current_slot': current, 'pending_slot': ?pending},
    };

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async =>
      const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running);
}

Future<(ProviderContainer, _Wheel)> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {bool inSettings = false,
    DeviceDriver? driver,
    bool planRunning = false,
    double? width,
    bool alsoPicker = false,
    bool reduceMotion = false,
    double textScale = 1}) async {
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
    child: MaterialApp(
      builder: (context, child) => MediaQuery(
        data: MediaQuery.of(context)
            .copyWith(disableAnimations: reduceMotion, textScaler: TextScaler.linear(textScale)),
        child: child!,
      ),
      home: Scaffold(
        body: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: width,
            child: alsoPicker
                // Preview's inline filter picker on screen beside the window.
                ? Column(mainAxisSize: MainAxisSize.min, children: [
                    SimpleFilterWheelWindow(inSettings: inSettings),
                    FilterDropdown(value: 'L', onChanged: (_) {}, inline: (names, value, pick) => Text('picker $value')),
                  ])
                : SimpleFilterWheelWindow(inSettings: inSettings),
          ),
        ),
      ),
    ),
  ));
  await tester.pump();
  return (c, wheel);
}

Finder _slot(int p) => find.byKey(Key('simple-filterwheel-slot-$p'));

void main() {
  testWidgets('the filter in place big with its slot under it; a tap on a cell moves the wheel to it', (tester) async {
    final semantics = tester.ensureSemantics();
    final (_, wheel) = await _pump(tester, _wire());
    expect(find.text('Filter wheel · EFW 7'), findsOneWidget);
    expect(find.text('Filter'), findsOneWidget, reason: 'the reading caption');
    expect(find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text('L')), findsOneWidget);
    expect(find.text('Slot 0'), findsOneWidget, reason: 'the position under the name');
    expect(tester.getSemantics(_slot(0)), isSemantics(isButton: true, isSelected: true, hasEnabledState: true, isEnabled: true, label: '0 · L'));
    expect(tester.getSemantics(_slot(1)), isSemantics(isButton: true, hasEnabledState: true, isEnabled: true, label: '1 · Ha'));
    expect(tester.getSemantics(_slot(2)), isSemantics(isButton: true, hasEnabledState: true, isEnabled: true, label: 'Slot 2'), reason: 'an unnamed slot by its number');
    await tester.tap(_slot(1));
    await tester.pump();
    expect(wheel.calls, ['change 1']);
    await tester.pump(const Duration(seconds: 11));
    semantics.dispose();
  });

  testWidgets('the cell in place takes no tap', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    await tester.tap(_slot(0));
    await tester.pump();
    expect(wheel.calls, isEmpty);
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
    expect(find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text('Ha')), findsOneWidget);
    await tester.pump(const Duration(seconds: 2));
  });

  testWidgets('the reading keeps the filter in place until the wheel lands', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    Finder inReading(String text) =>
        find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text(text));
    await tester.tap(_slot(1));
    await tester.pump();
    expect(inReading('L'), findsOneWidget, reason: 'L is still in');
    expect(inReading('Ha'), findsNothing, reason: 'Ha is not in yet');
    expect(find.text('Moving to Ha…'), findsOneWidget);
    wheel.set(_wire(current: 1));
    await tester.pump();
    expect(inReading('Ha'), findsOneWidget, reason: 'landed');
    await tester.pump(const Duration(seconds: 2));
  });

  testWidgets('a move started elsewhere reads Moving…', (tester) async {
    await _pump(tester, _wire(runtime: 'moving', current: -1));
    expect(find.text('Moving…'), findsOneWidget);
  });

  testWidgets('a move started elsewhere turns the drawn wheel to the slot it lands on', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    final mark = find.byType(SimpleFilterWheel);
    wheel.set(_wire(current: 2));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(tester.getCenter(_slot(2)).dx, moreOrLessEquals(tester.getRect(mark).center.dx, epsilon: 1));
    expect(tester.getCenter(_slot(2)).dy, lessThan(tester.getRect(mark).center.dy), reason: 'under the mark at the top');
    expect(wheel.calls, isEmpty, reason: 'the window only follows');
  });

  testWidgets('a wheel opened mid-move turns to the slot it lands on', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(runtime: 'moving', current: -1, extra: 1));
    final mark = find.byType(SimpleFilterWheel);
    wheel.set(_wire(current: 3, extra: 1));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(tester.getCenter(_slot(3)).dx, moreOrLessEquals(tester.getRect(mark).center.dx, epsilon: 1));
    expect(tester.getCenter(_slot(3)).dy, lessThan(tester.getRect(mark).center.dy), reason: 'under the mark at the top');
  });

  testWidgets('a wheel that gains slots keeps the filter in place under the mark', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(current: 2));
    final mark = find.byType(SimpleFilterWheel);
    Future<void> slot2UnderTheMark(Map<String, dynamic> next, String why) async {
      wheel.set(next);
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      expect(tester.getCenter(_slot(2)).dx, moreOrLessEquals(tester.getRect(mark).center.dx, epsilon: 1), reason: why);
      expect(tester.getCenter(_slot(2)).dy, lessThan(tester.getRect(mark).center.dy), reason: why);
    }

    await slot2UnderTheMark(_wire(current: 2, extra: 3), '3 slots to 6: the same filter, the cells closer together');
    await slot2UnderTheMark(_wire(runtime: 'moving', current: -1, extra: 5), '8 slots while its slot is unknown: held');
  });

  testWidgets('a wheel that loses slots while its slot is unknown never shows a slot past the end', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(current: 7, extra: 5));
    final mark = find.byType(SimpleFilterWheel);
    wheel.set(_wire(runtime: 'moving', current: -1, extra: 3));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(tester.getCenter(_slot(0)).dx, moreOrLessEquals(tester.getRect(mark).center.dx, epsilon: 1));
    expect(tester.getCenter(_slot(0)).dy, lessThan(tester.getRect(mark).center.dy), reason: 'slot 0 is under the mark');
  });

  testWidgets('a keyboard user tabs to a cell and picks it with Enter or Space', (tester) async {
    final (_, wheel) = await _pump(tester, _wire());
    // Reading order; the cell in place takes no focus, so slot 2 (lower left) is first.
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.enter);
    await tester.pump();
    expect(wheel.calls, ['change 2']);
    await tester.pump(const Duration(seconds: 11)); // the settle time frees the cells
    await tester.sendKeyEvent(LogicalKeyboardKey.tab);
    await tester.pump();
    await tester.sendKeyEvent(LogicalKeyboardKey.space);
    await tester.pump();
    expect(wheel.calls, hasLength(2), reason: 'Space picks too');
    expect(wheel.calls.last, startsWith('change '));
    await tester.pump(const Duration(seconds: 11));
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

  testWidgets('with the inline picker on screen too, a window tap asks once and a refusal says so once', (tester) async {
    final (c, wheel) = await _pump(tester, _wire(), planRunning: true, alsoPicker: true);
    await tester.pump();
    await tester.tap(_slot(1));
    await tester.pump();
    expect(find.textContaining('A plan is running'), findsOneWidget, reason: 'only the window that asked shows it');
    wheel.busy = true;
    await tester.tap(find.text('Change anyway'));
    await tester.pump();
    expect(c.read(simpleMessagesProvider), hasLength(1), reason: 'one pill, from the window that asked');
    await tester.pump(const Duration(seconds: 15)); // the pill fades
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

  testWidgets('a manual wheel turns the drawn wheel only once the swap is confirmed', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(manual: true));
    final drawn = find.byType(SimpleFilterWheel);
    bool underMark(int p) => (tester.getCenter(_slot(p)).dx - tester.getRect(drawn).center.dx).abs() < 1 &&
        tester.getCenter(_slot(p)).dy < tester.getRect(drawn).center.dy;
    await tester.tap(_slot(1));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(underMark(0), isTrue, reason: 'asked, not swapped yet');
    wheel.set(_wire(manual: true, runtime: 'awaiting_user', pending: 1));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(underMark(0), isTrue, reason: 'waiting for the hand swap');
    wheel.set(_wire(manual: true, current: 1));
    await tester.pump();
    await tester.pump(const Duration(seconds: 2));
    expect(underMark(1), isTrue, reason: 'confirmed: now it turns');
    await tester.pump(const Duration(seconds: 11));
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

  testWidgets('a wheel that reports no filters says so and draws no wheel', (tester) async {
    await _pump(tester, _wire()..['slots'] = <Map<String, dynamic>>[]);
    expect(find.text('This wheel reports no filters.'), findsOneWidget);
    expect(find.byType(SimpleFilterWheel), findsNothing);
  });

  testWidgets('an unnamed filter in place reads by its slot alone', (tester) async {
    await _pump(tester, _wire(current: 2));
    expect(find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text('Slot 2')), findsOneWidget);
    expect(find.textContaining('(slot 2)'), findsNothing);
    expect(find.text('Slot 2'), findsOneWidget, reason: 'the slot alone, not twice');
  });

  testWidgets('a change accepted but never seen moving clears Moving to … and frees the filters', (tester) async {
    // #1666 audit P1: accepted (202), then the wheel stays idle on the old slot.
    final (_, wheel) = await _pump(tester, _wire());
    await tester.tap(_slot(1));
    await tester.pump();
    expect(find.text('Moving to Ha…'), findsOneWidget);
    await tester.pump(const Duration(seconds: 11));
    expect(find.text('Moving to Ha…'), findsNothing);
    expect(find.text('Slot 0'), findsOneWidget);
    await tester.tap(_slot(1));
    await tester.pump();
    expect(wheel.calls, ['change 1', 'change 1'], reason: 'the filters take a tap again');
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('locked filters are read as disabled', (tester) async {
    // #1666 audit P2: while the wheel turns, a screen reader must not hear working buttons.
    final semantics = tester.ensureSemantics();
    await _pump(tester, _wire(runtime: 'moving', current: -1));
    expect(tester.getSemantics(_slot(0)), isSemantics(isButton: true, hasEnabledState: true, isEnabled: false));
    await _pump(tester, _wire());
    expect(tester.getSemantics(_slot(1)), isSemantics(isButton: true, hasEnabledState: true, isEnabled: true));
    // Re-audit P2: the filter in place takes no tap but is selected, not dimmed (as a native segmented control).
    expect(tester.getSemantics(_slot(0)),
        isSemantics(isButton: true, isSelected: true, hasEnabledState: true, isEnabled: true));
    semantics.dispose();
  });

  testWidgets('on a narrow window the plan question sits over its buttons', (tester) async {
    // #1666 audit P2: beside two buttons the question wrapped a few words a line.
    await _pump(tester, _wire(), planRunning: true, width: 280);
    await tester.pump();
    await tester.tap(_slot(1));
    await tester.pump();
    final question = tester.getRect(find.textContaining('A plan is running'));
    expect(tester.getTopLeft(find.text('Change anyway')).dy, greaterThanOrEqualTo(question.bottom));
    expect(tester.getTopLeft(find.text('Cancel')).dy, greaterThanOrEqualTo(question.bottom));
  });

  testWidgets('a long name shows the slot number in its cell and the full name in the reading', (tester) async {
    const long = 'Hydrogen-alpha 3 nm narrowband';
    await _pump(tester, _wire(ha: long, current: 1), width: 280);
    expect(find.descendant(of: _slot(1), matching: find.text('1')), findsOneWidget);
    expect(find.descendant(of: _slot(1), matching: find.text(long)), findsNothing);
    expect(find.descendant(of: _slot(0), matching: find.text('L')), findsOneWidget, reason: 'a short name fits its cell');
    final reading = tester.widget<Text>(find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text(long)));
    expect(reading.data, long);
    expect(tester.takeException(), isNull);
  });

  testWidgets('the cells cannot be tapped while a plan asks or a swap waits', (tester) async {
    final (_, wheel) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump();
    await tester.tap(_slot(1));
    await tester.pump();
    await tester.tap(_slot(2));
    await tester.pump();
    expect(wheel.calls, isEmpty, reason: 'asking: the other cell takes no tap');
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    final (_, manual) = await _pump(tester, _wire(manual: true, runtime: 'awaiting_user', pending: 1));
    await tester.tap(_slot(2));
    await tester.pump();
    expect(manual.calls, isEmpty, reason: 'swapping');
  });

  testWidgets('a compact wheel for 8 slots, a large one for 9', (tester) async {
    await _pump(tester, _wire(extra: 5), width: 360); // 8 slots
    final compact = tester.getSize(find.byType(SimpleFilterWheel));
    expect(find.byKey(const Key('simple-filterwheel-reading')), findsOneWidget);
    expect(compact.width, 180);
    await _pump(tester, _wire(extra: 6), width: 360); // 9 slots
    final large = tester.getSize(find.byType(SimpleFilterWheel));
    expect(find.byKey(const Key('simple-filterwheel-reading')), findsNothing, reason: 'the name moves into the hub');
    expect(large.width, 260);
    expect(find.descendant(of: find.byType(SimpleFilterWheel), matching: find.text('L')), findsWidgets);
    expect(tester.takeException(), isNull);
  });

  testWidgets('every cell of a large wheel is at least a 44 pt target', (tester) async {
    await _pump(tester, _wire(extra: 9), width: 360); // 12 slots
    for (var p = 0; p < 12; p++) {
      final size = tester.getSize(_slot(p));
      expect(size.width, greaterThanOrEqualTo(44), reason: 'slot $p');
      expect(size.height, greaterThanOrEqualTo(44), reason: 'slot $p');
    }
  });

  testWidgets("a tap on a cell's edge picks that cell, never its neighbour", (tester) async {
    // Slot 2 is drawn after slot 1, so where their targets overlapped it took the tap.
    for (final (wire, width, why) in [
      (_wire(extra: 5), 280.0, '8 slots on a wheel a narrow window shrinks'),
      (_wire(extra: 9), 360.0, '12 slots, the cells diagonal to each other'),
    ]) {
      final (_, wheel) = await _pump(tester, wire, width: width);
      final one = tester.getCenter(_slot(1)), two = tester.getCenter(_slot(2));
      expect(tester.getSize(_slot(1)).width, lessThanOrEqualTo((two - one).distance + 0.01),
          reason: '$why: never past half-way to the next cell');
      await tester.tapAt(Offset.lerp(one, two, 0.42)!);
      await tester.pump();
      expect(wheel.calls, ['change 1'], reason: why);
      await tester.pump(const Duration(seconds: 11));
    }
  });

  testWidgets('a large wheel engraves its slot numbers in Text Secondary, growing with the text', (tester) async {
    // Slot 5's cell reads "F5", so the wheel's only "5" is the engraved number.
    Finder five() => find.descendant(of: find.byType(SimpleFilterWheel), matching: find.text('5'));
    await _pump(tester, _wire(extra: 9), width: 360); // 12 slots
    expect(tester.widget<Text>(five()).style?.color, AraColors.textSecondary, reason: '4.5:1 on the wheel face');
    final atOne = tester.getSize(five()).height;
    await _pump(tester, _wire(extra: 9), width: 360, textScale: 2);
    expect(tester.getSize(five()).height, greaterThan(atOne), reason: 'grows with the wheel');
    expect(tester.takeException(), isNull);
  });

  testWidgets('the wheel turns to bring a slot under the mark, and jumps with reduced motion', (tester) async {
    Offset under(Finder wheel) => Offset(tester.getRect(wheel).center.dx, tester.getRect(wheel).top);
    final (_, motion) = await _pump(tester, _wire());
    await tester.tap(_slot(1));
    await tester.pump();
    motion.set(_wire(current: 1));
    await tester.pump();
    final wheel = find.byType(SimpleFilterWheel);
    expect((tester.getCenter(_slot(1)).dx - under(wheel).dx).abs(), greaterThan(5), reason: 'still turning');
    await tester.pump(const Duration(seconds: 1));
    expect(tester.getCenter(_slot(1)).dx, moreOrLessEquals(under(wheel).dx, epsilon: 1));
    expect(tester.getCenter(_slot(1)).dy, lessThan(tester.getRect(wheel).center.dy));

    final (_, still) = await _pump(tester, _wire(), reduceMotion: true);
    await tester.tap(_slot(1));
    await tester.pump();
    still.set(_wire(current: 1));
    await tester.pump();
    expect(tester.getCenter(_slot(1)).dx, moreOrLessEquals(under(wheel).dx, epsilon: 1), reason: 'landed without turning');
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets("the filter in place's saved offset shows under its slot line, only once any offset is non-zero (#1675)",
      (tester) async {
    Map<String, dynamic> wire(int ha) => {
          ..._wire(current: 1),
          'slots': [
            {'position': 0, 'name': 'L', 'focus_offset': 0},
            {'position': 1, 'name': 'Ha', 'focus_offset': ha},
          ],
        };
    final (_, wheel) = await _pump(tester, wire(0));
    expect(find.byKey(const Key('simple-filterwheel-offset')), findsNothing, reason: 'every offset 0');
    wheel.set(wire(42));
    await tester.pump();
    expect(find.text('Offset +42 steps'), findsOneWidget);
    final line = tester.getRect(
        find.descendant(of: find.byKey(const Key('simple-filterwheel-reading')), matching: find.text('Slot 1')));
    expect(tester.getRect(find.text('Offset +42 steps')).top, greaterThanOrEqualTo(line.bottom),
        reason: 'under the slot line');
    expect(find.descendant(of: _slot(1), matching: find.textContaining('42')), findsNothing,
        reason: 'the cells don\'t change');
  });

  testWidgets("a large wheel shows the saved offset in its hub, under the slot line (#1675)", (tester) async {
    final wire = _wire(current: 1, extra: 6); // 9 slots
    (wire['slots'] as List)[1] = {'position': 1, 'name': 'Ha', 'focus_offset': 7};
    await _pump(tester, wire, width: 360);
    final hub = find.descendant(of: find.byType(SimpleFilterWheel), matching: find.text('Offset +7 steps'));
    expect(hub, findsOneWidget);
    final line = tester.getRect(find.descendant(of: find.byType(SimpleFilterWheel), matching: find.text('slot 1')));
    expect(tester.getRect(hub).top, greaterThanOrEqualTo(line.bottom), reason: 'under the slot line');
  });
}
