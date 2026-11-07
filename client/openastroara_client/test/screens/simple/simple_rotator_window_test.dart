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

  /// The rotator refuses the next sync.
  bool refuseSync = false;

  @override
  Future<bool> sync(double skyAngleDeg) async {
    calls.add('sync ${skyAngleDeg.toStringAsFixed(1)}');
    if (refuseSync) throw StateError('Rotator is moving');
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
    {bool inSettings = false, DeviceDriver? driver, bool planRunning = false, double? width, bool scrolling = false}) async {
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
          child: scrolling
              // As in Settings: the window on a page that scrolls.
              ? SingleChildScrollView(
                  child: Column(children: [
                  SizedBox(width: 360, child: SimpleRotatorWindow(inSettings: inSettings)),
                  const SizedBox(height: 2000),
                ]))
              : SizedBox(width: width, child: SimpleRotatorWindow(inSettings: inSettings)),
        ),
      ),
    ),
  ));
  await tester.pump();
  return (c, rotator);
}

final _dial = find.byKey(const Key('simple-rotator-dial'));
final _stop = find.byKey(const Key('simple-rotator-stop'));
final _go = find.byKey(const Key('simple-rotator-go'));
final _sync = find.byKey(const Key('simple-rotator-sync'));

Offset _rim(WidgetTester tester, double deg) {
  final centre = tester.getCenter(_dial);
  final r = tester.getSize(_dial).width * 0.42;
  final a = (deg - 90) * math.pi / 180;
  return centre + Offset(r * math.cos(a), r * math.sin(a));
}

/// Turns the dial by [degrees] clockwise (negative: counter-clockwise) from
/// near its rim, half a degree at a time like a finger. Give a half degree
/// past the clicks wanted (17.5 for 17), so rounding never drops one.
Future<void> _turn(WidgetTester tester, double degrees, {bool cancel = false}) async {
  final g = await tester.startGesture(_rim(tester, 0));
  final steps = (degrees.abs() / 0.5).round();
  for (var i = 1; i <= steps; i++) {
    await g.moveTo(_rim(tester, degrees.sign * i * 0.5));
    await tester.pump();
  }
  if (cancel) {
    await g.cancel();
  } else {
    await g.up();
  }
  await tester.pump();
}

String _syncLabel(WidgetTester tester) =>
    tester.widget<Text>(find.descendant(of: _sync, matching: find.byType(Text))).data!;

String _goLabel(WidgetTester tester) =>
    tester.widget<Text>(find.descendant(of: _go, matching: find.byType(Text))).data!;

void main() {
  testWidgets('the sky angle big on the left, the mechanical angle under it, the dial on the right; no step picker',
      (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Rotator · Falcon'), findsOneWidget);
    expect(find.text('92.0°'), findsOneWidget);
    expect(find.text('88.0°'), findsOneWidget);
    expect(tester.getRect(_dial).left, greaterThan(tester.getRect(find.text('92.0°')).right));
    expect(find.byKey(const Key('simple-rotator-steps')), findsNothing, reason: 'one step: 1°');
  });

  testWidgets('with the mechanical angle the same as the sky angle, only the sky angle shows', (tester) async {
    await _pump(tester, _wire(mech: 92));
    expect(find.text('Mechanical angle'), findsNothing);
  });

  testWidgets('turning sets a target a degree a click; letting go moves nothing; Rotate moves there', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    expect(_goLabel(tester), 'Rotate');
    await _turn(tester, 17.5);
    // Clockwise brings lower numbers under the mark: 17 clicks from 92 is 75.
    expect(r.calls, isEmpty, reason: 'letting go no longer moves it');
    expect(_goLabel(tester), 'Rotate to 75.0°');
    expect(find.text('92.0°'), findsOneWidget, reason: 'the reading stays where the rotator is');
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, ['move 75.0 sky']);
    expect(_goLabel(tester), 'Rotate', reason: 'the target is spent');
  });

  testWidgets('turning on adds to the target; turning back to where it is clears it', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    await _turn(tester, -2.5); // two clicks counter-clockwise: up
    expect(_goLabel(tester), 'Rotate to 94.0°');
    await _turn(tester, -1.5);
    expect(_goLabel(tester), 'Rotate to 95.0°', reason: 'from the target, not the rotator');
    await _turn(tester, 3.5);
    expect(_goLabel(tester), 'Rotate', reason: 'back at 92: nothing to do');
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, isEmpty);
  });

  testWidgets('the angle wraps through 0', (tester) async {
    final (_, r) = await _pump(tester, _wire(sky: 1, mech: 1));
    await _turn(tester, 2.5); // two clicks clockwise from 1: 0, then 359
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, ['move 359.0 sky']);
  });

  testWidgets('a keyboard sets the target: the arrows move it a degree; nothing moves until Rotate', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    for (var i = 0; i < 10 && FocusManager.instance.primaryFocus?.debugLabel != 'simple-rotator-dial'; i++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
    }
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'simple-rotator-dial');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
    await tester.pump();
    expect(_goLabel(tester), 'Rotate to 94.0°');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
    await tester.pump();
    expect(_goLabel(tester), 'Rotate to 93.0°');
    expect(r.calls, isEmpty);
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, ['move 93.0 sky']);
  });

  testWidgets('a scroll over the dial sets the target, and nothing moves on its own', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    final mouse = TestPointer(1, PointerDeviceKind.mouse);
    await tester.sendEventToBinding(mouse.hover(tester.getCenter(_dial) + const Offset(60, 0)));
    await tester.sendEventToBinding(mouse.scroll(const Offset(0, -25)));
    await tester.pump(const Duration(seconds: 1));
    expect(r.calls, isEmpty);
    expect(_goLabel(tester), isNot('Rotate'));
  });

  testWidgets('screen readers get the dial as a slider: increase moves the target a degree up', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    final node = tester.getSemantics(_dial);
    expect(node.label, contains('Sky angle'));
    expect(node.value, '92.0°');
    node.owner!.performAction(node.id, SemanticsAction.increase);
    await tester.pump();
    expect(r.calls, isEmpty);
    expect(_goLabel(tester), 'Rotate to 93.0°');
    expect(tester.getSemantics(_dial).value, '93.0°', reason: 'the dial reads its target');
  });

  testWidgets('Sync tells the rotator it is at the target, without moving it', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    expect(_syncLabel(tester), 'Sync');
    await _turn(tester, 17.5);
    expect(_syncLabel(tester), 'Sync to 75.0°');
    await tester.tap(_sync);
    await tester.pump();
    expect(r.calls, ['sync 75.0']);
    expect(_syncLabel(tester), 'Sync', reason: 'the target is spent');
  });

  testWidgets('Sync sits beside Rotate, under the dial', (tester) async {
    await _pump(tester, _wire(), width: 328);
    final sync = tester.getRect(_sync);
    final go = tester.getRect(_go);
    expect(sync.top, greaterThan(tester.getRect(_dial).bottom));
    expect(go.left, greaterThan(sync.right), reason: 'Sync, then Rotate');
    expect(go.top, sync.top);
  });

  testWidgets('a refused sync says why in a pill', (tester) async {
    final (c, r) = await _pump(tester, _wire());
    r.refuseSync = true;
    await _turn(tester, 17.5);
    await tester.tap(_sync);
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, "Couldn't sync the rotator"));
  });

  testWidgets('with no target Rotate and Sync are disabled', (tester) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, _wire());
    expect(tester.getSemantics(_go), isSemantics(isButton: true, hasEnabledState: true, isEnabled: false));
    expect(tester.getSemantics(_sync), isSemantics(isButton: true, hasEnabledState: true, isEnabled: false));
    semantics.dispose();
  });

  testWidgets('while it moves the window says where to; the middle is a red stop that halts it', (tester) async {
    final (_, r) = await _pump(tester, _wire());
    await _turn(tester, 17.5);
    await tester.tap(_go);
    await tester.pump();
    r.set(_wire(runtime: 'moving'));
    await tester.pump();
    expect(find.text('Moving to 75.0°…'), findsOneWidget);
    final square = tester.widget<SimpleStopSquare>(find.descendant(of: _stop, matching: find.byType(SimpleStopSquare)));
    expect(square.color, AraColors.accentError);
    await tester.tap(_stop);
    await tester.pump();
    expect(r.calls, ['move 75.0 sky', 'halt']);
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
    await _turn(tester, 17.5);
    expect(_goLabel(tester), 'Rotate');
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, isEmpty);
  });

  testWidgets('with the sky angle unknown the dial sets nothing', (tester) async {
    final (_, r) = await _pump(tester, _wire(sky: null));
    await _turn(tester, 17.5);
    expect(_goLabel(tester), 'Rotate');
    expect(r.calls, isEmpty);
  });

  testWidgets('while a plan runs, Rotate asks first, in the window', (tester) async {
    final (_, r) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump(); // the run state arrives
    await _turn(tester, 17.5);
    await tester.tap(_go);
    await tester.pump();
    expect(r.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(r.calls, isEmpty);
    await tester.tap(_go);
    await tester.pump();
    await tester.tap(find.text('Rotate anyway'));
    await tester.pump();
    expect(r.calls, ['move 75.0 sky']);
  });

  testWidgets('a refused move says why in a pill', (tester) async {
    final (c, r) = await _pump(tester, _wire());
    r.refuse = true;
    await _turn(tester, 17.5);
    await tester.tap(_go);
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect((pill.kind, pill.title), (SimpleMessageKind.error, "Couldn't move the rotator"));
  });

  testWidgets('while it moves the dial leaves the finger to the page: a swipe on it scrolls (#1670 audit P1)', (tester) async {
    await _pump(tester, _wire(runtime: 'moving'), scrolling: true);
    final page = tester.state<ScrollableState>(find.byType(Scrollable)).position;
    // From the face near its rim, not the stop square in the middle.
    await tester.dragFrom(tester.getCenter(_dial) + Offset(tester.getSize(_dial).width * 0.4, 0), const Offset(0, -200));
    await tester.pump();
    expect(page.pixels, greaterThan(0));
  });

  testWidgets('dragging off the dial drops the target (#1670 audit P1)', (tester) async {
    await _pump(tester, _wire());
    final g = await tester.startGesture(_rim(tester, 0));
    for (var i = 1; i <= 35; i++) {
      await g.moveTo(_rim(tester, i * 0.5));
      await tester.pump();
    }
    expect(_goLabel(tester), 'Rotate to 75.0°');
    await g.moveTo(tester.getCenter(_dial) + Offset(tester.getSize(_dial).width, 0));
    await tester.pump();
    await g.up();
    await tester.pump();
    expect(_goLabel(tester), 'Rotate');
  });

  testWidgets('while it moves the dial reads as disabled, with nothing to adjust (#1670 audit P2)', (tester) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, _wire(runtime: 'moving'));
    final node = tester.getSemantics(_dial);
    expect(node, isSemantics(isSlider: true, hasEnabledState: true, isEnabled: false));
    expect(node.getSemanticsData().hasAction(SemanticsAction.increase), isFalse);
    semantics.dispose();
  });

  testWidgets('a turn cut off by the system drops the target', (tester) async {
    await _pump(tester, _wire());
    await _turn(tester, 17.5, cancel: true);
    expect(_goLabel(tester), 'Rotate');
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
