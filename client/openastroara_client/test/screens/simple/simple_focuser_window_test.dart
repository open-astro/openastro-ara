import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/screens/simple/simple_shell.dart' show SimpleSegment;

/// A focuser that records where it was sent.
class _Focuser extends FocuserNotifier {
  _Focuser(this.json);
  final Map<String, dynamic> json;
  final moves = <int>[];

  @override
  Future<FocuserStatus?> build() async => FocuserStatus.fromJson(json);

  @override
  Future<bool> move(int targetPosition, {bool useTempComp = false}) async {
    moves.add(targetPosition);
    return true;
  }
}

Map<String, dynamic> _wire({bool absolute = true, String runtime = 'idle', int position = 12000}) => {
      'device_id': 'f',
      'name': 'ZWO EAF',
      'state': 'connected',
      'capabilities': {'min_position': 0, 'max_position': 60000, 'absolute_focuser': absolute},
      'runtime': {'state': runtime, 'position': position, 'temperature': 8.4},
    };

Future<_Focuser> _pump(WidgetTester tester, Map<String, dynamic> wire) async {
  final focuser = _Focuser(wire);
  final c = ProviderContainer(overrides: [
    focuserProvider.overrideWith(() => focuser),
    deviceDriverProvider.overrideWith((ref, type) async => null),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: const MaterialApp(home: Scaffold(body: Center(child: SizedBox(width: 360, child: SimpleFocuserWindow())))),
  ));
  await tester.pump();
  return focuser;
}

/// Turns the knob by [degrees] clockwise (negative: counter-clockwise),
/// from its top, in small steps like a finger.
Future<void> _turn(WidgetTester tester, double degrees) async {
  final knob = find.byKey(const Key('simple-focuser-knob'));
  final centre = tester.getCenter(knob);
  final r = tester.getSize(knob).width * 0.4;
  Offset at(double deg) {
    final a = (deg - 90) * math.pi / 180;
    return centre + Offset(r * math.cos(a), r * math.sin(a));
  }
  final g = await tester.startGesture(at(0));
  final steps = (degrees.abs() / 5).round();
  for (var i = 1; i <= steps; i++) {
    await g.moveTo(at(degrees.sign * i * 5));
    await tester.pump();
  }
  await g.up();
  await tester.pump();
}

void main() {
  testWidgets('the focuser window: position, temperature, a knob and the step', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Focuser · ZWO EAF'), findsOneWidget);
    expect(find.text('12 000'), findsOneWidget);
    expect(find.text('8.4 °C'), findsOneWidget);
    expect(find.byKey(const Key('simple-focuser-knob')), findsOneWidget);
    expect(tester.widget<SimpleSegment>(find.byKey(const Key('simple-focuser-step-100'))).selected, isTrue,
        reason: '100 to start');
  });

  testWidgets('turning the knob shows the target, one step per 30°, and moves there on letting go', (tester) async {
    final f = await _pump(tester, _wire());
    await _turn(tester, 95); // three detents clockwise: out
    expect(f.moves, [12300]);
    await tester.tap(find.byKey(const Key('simple-focuser-step-1000')));
    await tester.pump();
    await _turn(tester, -65); // two counter-clockwise: in
    expect(f.moves.last, 10000, reason: 'from the reported position, 1000 a detent');
  });

  testWidgets('a relative focuser is sent the change, not a position', (tester) async {
    final f = await _pump(tester, _wire(absolute: false));
    await _turn(tester, 65);
    expect(f.moves, [200]);
  });

  testWidgets('while it moves the window says where to', (tester) async {
    await _pump(tester, _wire(runtime: 'moving'));
    expect(find.textContaining('Moving'), findsOneWidget);
  });

  testWidgets('screen readers get the knob as a slider: increase and decrease move a step', (tester) async {
    final f = await _pump(tester, _wire());
    final node = tester.getSemantics(find.byKey(const Key('simple-focuser-knob')));
    expect(node.label, contains('Focus position'));
    expect(node.value, '12 000');
    node.owner!.performAction(node.id, SemanticsAction.increase);
    await tester.pump();
    expect(f.moves, [12100]);
  });
}
