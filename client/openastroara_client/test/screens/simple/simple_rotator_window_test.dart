import 'dart:math' as math;

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/rotator_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleStopSquare;
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/rotator_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

/// A rotator that records what it was asked to do.
class _Rotator extends RotatorNotifier {
  _Rotator(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// The rotator refuses the next move.
  bool refuse = false;

  @override
  Future<RotatorStatus?> build() async => RotatorStatus.fromJson(json);

  void set(Map<String, dynamic> next) => state = AsyncData(RotatorStatus.fromJson(next));

  @override
  Future<bool> move(double targetAngleDeg, {bool useSkyAngle = false}) async {
    calls.add('move ${targetAngleDeg.toStringAsFixed(1)}${useSkyAngle ? ' sky' : ''}');
    if (refuse) throw StateError('Rotator is moving');
    return true;
  }

  @override
  Future<bool> halt() async {
    calls.add('halt');
    return true;
  }
}

Map<String, dynamic> _wire({String state = 'connected', String runtime = 'idle', double? sky = 92, double? mech = 88}) => {
      'device_id': 'r',
      'name': 'Falcon',
      'state': state,
      'capabilities': {'can_reverse': true, 'step_size': 0.1},
      'runtime': {'state': runtime, 'sky_angle_deg': sky, 'mechanical_angle_deg': mech, 'reverse': false},
    };

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async =>
      const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running);
}

Future<(ProviderContainer, _Rotator)> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {bool inSettings = false, DeviceDriver? driver, bool planRunning = false, double? width}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final rotator = _Rotator(wire);
  final c = ProviderContainer(overrides: [
    rotatorProvider.overrideWith(() => rotator),
    simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
    deviceDriverProvider.overrideWith((ref, type) async => type == 'rotator' ? driver : null),
    if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(width: width, child: SimpleRotatorWindow(inSettings: inSettings)),
        ),
      ),
    ),
  ));
  await tester.pump();
  return (c, rotator);
}

final _dial = find.byKey(const Key('simple-rotator-dial'));
final _stop = find.byKey(const Key('simple-rotator-stop'));

/// Turns the dial by [degrees] clockwise (negative: counter-clockwise) from
/// near its rim, a degree at a time like a finger.
Future<void> _turn(WidgetTester tester, double degrees, {bool cancel = false}) async {
  final centre = tester.getCenter(_dial);
  final r = tester.getSize(_dial).width * 0.42;
  Offset at(double deg) {
    final a = (deg - 90) * math.pi / 180;
    return centre + Offset(r * math.cos(a), r * math.sin(a));
  }
  final g = await tester.startGesture(at(0));
  for (var i = 1; i <= degrees.abs().round(); i++) {
    await g.moveTo(at(degrees.sign * i));
    await tester.pump();
  }
  if (cancel) {
    await g.cancel();
  } else {
    await g.up();
  }
  await tester.pump();
}

void main() {
  testWidgets('the sky angle big on the left, the mechanical angle under it, the dial on the right', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Rotator · Falcon'), findsOneWidget);
    expect(find.text('92.0°'), findsOneWidget);
    expect(find.text('88.0°'), findsOneWidget);
    expect(tester.getRect(_dial).left, greaterThan(tester.getRect(find.text('92.0°')).right));
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-rotator-step-5'))).selected, isTrue,
        reason: '5° to start');
  });

  testWidgets('with the mechanical angle the same as the sky angle, only the sky angle shows', (tester) async {
    await _pump(tester, _wire(mech: 92));
    expect(find.text('Mechanical angle'), findsNothing);
  });

  testWidgets('turning shows the target, on the step\'s marks; the rotator moves there on letting go', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    final centre = tester.getCenter(_dial);
    final radius = tester.getSize(_dial).width * 0.42;
    Offset at(double deg) {
      final a = (deg - 90) * math.pi / 180;
      return centre + Offset(radius * math.cos(a), radius * math.sin(a));
    }
    final g = await tester.startGesture(at(0));
    for (var d = 1; d <= 17; d++) {
      await g.moveTo(at(d.toDouble()));
      await tester.pump();
    }
    // Clockwise brings lower numbers under the mark: three 5° clicks from 92 are 90, 85, 80.
    expect(find.text('Let go to move to 80.0°'), findsOneWidget);
    expect(find.text('92.0°'), findsOneWidget, reason: 'the reading stays where the rotator is');
    expect(r.calls, isEmpty, reason: 'nothing moves until letting go');
    await g.up();
    await tester.pump();
    expect(r.calls, ['move 80.0 sky']);
  });

  testWidgets('counter-clockwise goes up, and the step size sets each click', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-rotator-step-15')));
    await tester.pump();
    await _turn(tester, -32); // two 15° clicks
    expect(r.calls, ['move 120.0 sky'], reason: 'the next marks up from 92: 105, then 120');
  });

  testWidgets('the angle wraps through 0', (tester) async {
    final (_, r) = await _pump(tester, _wire(sky: 2, mech: 2));
    await _turn(tester, 11); // two 5° clicks clockwise from 2: 0, then 355
    expect(r.calls, ['move 355.0 sky']);
  });

  testWidgets('a keyboard turns it: the arrows move a step from where the rotator is', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    for (var i = 0; i < 10 && FocusManager.instance.primaryFocus?.debugLabel != 'simple-rotator-dial'; i++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
    }
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'simple-rotator-dial');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(r.calls, ['move 95.0 sky'], reason: 'up or right: the next mark up');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.pump();
    expect(r.calls.last, 'move 90.0 sky', reason: 'down or left: the next mark down, from the reported angle');
  });

  testWidgets('a scroll over the dial turns it, and it moves once the scrolling stops', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    final mouse = TestPointer(1, PointerDeviceKind.mouse);
    await tester.sendEventToBinding(mouse.hover(tester.getCenter(_dial) + const Offset(60, 0)));
    await tester.sendEventToBinding(mouse.scroll(const Offset(0, -60)));
    await tester.pump(const Duration(milliseconds: 50));
    expect(r.calls, isEmpty, reason: 'not while the scroll goes on');
    await tester.pump(const Duration(milliseconds: 500));
    expect(r.calls.single, startsWith('move '));
  });

  testWidgets('screen readers get the dial as a slider: increase moves a step up', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    final node = tester.getSemantics(_dial);
    expect(node.label, contains('Sky angle'));
    expect(node.value, '92.0°');
    node.owner!.performAction(node.id, SemanticsAction.increase);
    await tester.pump();
    expect(r.calls, ['move 95.0 sky']);
  });

  testWidgets('while it moves the window says where to; the middle is a red stop that halts it', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    await _turn(tester, 17);
    r.set(_wire(runtime: 'moving'));
    await tester.pump();
    expect(find.text('Moving to 80.0°…'), findsOneWidget);
    final square = tester.widget<SimpleStopSquare>(find.descendant(of: _stop, matching: find.byType(SimpleStopSquare)));
    expect(square.color, AraColors.accentError);
    await tester.tap(_stop);
    await tester.pump();
    expect(r.calls, ['move 80.0 sky', 'halt']);
  });

  testWidgets('at rest the stop is grey and takes no tap', (tester) async {
    final semantics = tester.ensureSemantics();
    final (_, r) = await _pump(tester, _wire());
    final square = tester.widget<SimpleStopSquare>(find.descendant(of: _stop, matching: find.byType(SimpleStopSquare)));
    expect(square.color, isNot(AraColors.accentError));
    await tester.tap(_stop);
    await tester.pump();
    expect(r.calls, isEmpty);
    expect(tester.getSemantics(_stop), isSemantics(isButton: true, hasEnabledState: true, isEnabled: false));
    semantics.dispose();
  });

  testWidgets('a move started elsewhere reads Moving…, and the dial turns nothing until it stops', (tester) async {
    final (_, r) = await _pump(tester, _wire(runtime: 'moving'));
    expect(find.text('Moving…'), findsOneWidget);
    await _turn(tester, 17);
    expect(r.calls, isEmpty);
  });

  testWidgets('with the sky angle unknown the dial moves nothing', (tester) async {
    final (_, r) = await _pump(tester, _wire(sky: null));
    await _turn(tester, 17);
    expect(r.calls, isEmpty);
  });

  testWidgets('while a plan runs, a move asks first, in the window', (tester) async {
    final (_, r) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump(); // the run state arrives
    await _turn(tester, 17);
    expect(r.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(r.calls, isEmpty);
    await _turn(tester, 17);
    await tester.tap(find.text('Rotate anyway'));
    await tester.pump();
    expect(r.calls, ['move 80.0 sky']);
  });

  testWidgets('a refused move says why in a pill', (tester) async {
    final (c, r) = await _pump(tester, _wire());
    r.refuse = true;
    await _turn(tester, 17);
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, "Couldn't move the rotator"));
  });

  testWidgets('a turn cut off by the system moves nothing', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    await _turn(tester, 17, cancel: true);
    expect(r.calls, isEmpty);
    expect(find.textContaining('Let go to move'), findsNothing);
  });

  testWidgets('in Settings it ends with the rotator\'s versions; from the dot, none', (tester) async {
    const driver = DeviceDriver(driverVersion: '2.1', firmware: '1.4');
    await _pump(tester, _wire(), driver: driver);
    expect(find.text('Firmware'), findsNothing);
    await _pump(tester, _wire(), driver: driver, inSettings: true);
    await tester.pump();
    expect(find.text('Firmware'), findsOneWidget);
  });

  testWidgets('a rotator that isn\'t connected shows its state and no controls', (tester) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(_dial, findsNothing);
  });

  testWidgets('the dial fits a narrow window', (tester) async {
    await _pump(tester, _wire(), width: 260);
    expect(tester.takeException(), isNull);
    expect(tester.getRect(_dial).right, lessThanOrEqualTo(260));
    expect(tester.renderObject<RenderParagraph>(find.text('92.0°')).didExceedMaxLines, isFalse);
  });
}
