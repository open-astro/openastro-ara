import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
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

class _Live extends AutofocusLiveNotifier {
  int starts = 0;
  @override
  AutofocusLive build() => const AutofocusLive();
  @override
  Future<void> start() async => starts++;
}

late _Live _live;
late ProviderContainer _c;

Future<_Focuser> _pump(WidgetTester tester, Map<String, dynamic> wire, {double width = 360, String? font}) async {
  final focuser = _Focuser(wire);
  _live = _Live();
  final c = _c = ProviderContainer(overrides: [
    autofocusLiveProvider.overrideWith(() => _live),
    focuserProvider.overrideWith(() => focuser),
    deviceDriverProvider.overrideWith((ref, type) async => null),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
        theme: ThemeData(fontFamily: font),
        home: Scaffold(body: Center(child: SizedBox(width: width, child: const SimpleFocuserWindow())))),
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

  testWidgets('Position is where the focuser is; the target only shows in the line above the dial', (tester) async {
    await _pump(tester, _wire());
    final knob = find.byKey(const Key('simple-focuser-knob'));
    final centre = tester.getCenter(knob);
    final r = tester.getSize(knob).width * 0.4;
    Offset at(double deg) {
      final a = (deg - 90) * math.pi / 180;
      return centre + Offset(r * math.cos(a), r * math.sin(a));
    }
    final g = await tester.startGesture(at(0));
    for (var d = 5.0; d <= 95; d += 5) {
      await g.moveTo(at(d));
      await tester.pump();
    }
    expect(find.descendant(of: find.byKey(const Key('simple-window-row-Position')), matching: find.text('12 000')),
        findsOneWidget);
    expect(find.text('Let go to move to 12 300'), findsOneWidget);
    await g.up();
    await tester.pump();
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

  testWidgets('the middle of the knob runs autofocus', (tester) async {
    final f = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-focuser-autofocus')));
    await tester.pump();
    expect(_live.starts, 1);
    expect(f.moves, isEmpty, reason: 'a tap in the middle is not a turn');
  });

  testWidgets('the whole dial turns, like a safe\'s', (tester) async {
    await _pump(tester, _wire());
    double turn() => tester.widget<Transform>(find.byKey(const Key('simple-focuser-dial-face'))).transform.getRotation().entry(1, 0);
    expect(turn(), closeTo(0, 1e-9));
    await _turn(tester, 45);
    expect(turn(), isNot(closeTo(0, 1e-3)), reason: 'the face kept the turn');
  });

  testWidgets('Refocus after: 0.5 °C steps, Never at 0, kept in the profile', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Refocus after'), findsOneWidget);
    expect(find.text('2 °C'), findsOneWidget, reason: "the profile's default");
    await tester.tap(find.byKey(const Key('simple-focuser-refocus-up')));
    await tester.pump();
    expect(_c.read(autofocusSettingsProvider).triggerTempDeltaC, 2.5);
    for (var i = 0; i < 6; i++) {
      await tester.tap(find.byKey(const Key('simple-focuser-refocus-down')));
      await tester.pump();
    }
    expect(_c.read(autofocusSettingsProvider).triggerTempDeltaC, 0);
    expect(find.text('Never'), findsOneWidget);
  });

  // The test font draws every glyph as a square; fit is measured with the
  // Roboto the Flutter SDK ships (as simple_text_scale_test does).
  final fonts = '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';
  final hasFonts = File('$fonts/Roboto-Regular.ttf').existsSync();
  for (final scale in [1.0, 1.3]) {
    testWidgets('every row label fits on one line at ${scale}x text', skip: !hasFonts, (tester) async {
      final roboto = FontLoader('Roboto')
        ..addFont(Future.value(ByteData.view(File('$fonts/Roboto-Regular.ttf').readAsBytesSync().buffer)));
      await roboto.load();
      tester.platformDispatcher.textScaleFactorTestValue = scale;
      addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
      // The window's inner width: 360 pt grown with the text, less its padding.
      await _pump(tester, _wire(), width: 360 * (scale / 1.1).clamp(1, 2) - 32, font: 'Roboto');
      for (final label in ['Position', 'Temperature', 'Refocus after']) {
        final p = tester.renderObject<RenderParagraph>(find.text(label));
        expect(p.size.height, lessThan(14 * scale * 2), reason: '$label wraps');
      }
    });
  }

  testWidgets('the step sits centred under the knob, with room', (tester) async {
    await _pump(tester, _wire());
    final knob = tester.getRect(find.byKey(const Key('simple-focuser-knob')));
    final steps = tester.getRect(find.byKey(const Key('simple-focuser-steps')));
    expect(steps.center.dx, closeTo(knob.center.dx, 1));
    expect(steps.top - knob.bottom, greaterThanOrEqualTo(16));
  });
}
