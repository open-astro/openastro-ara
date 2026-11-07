import 'dart:io';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/models/autofocus_run.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show kSimpleStopSquare;
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/autofocus_live_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';

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

Map<String, dynamic> _wire({bool absolute = true, String runtime = 'idle', int? position = 12000, int max = 60000}) => {
      'device_id': 'f',
      'name': 'ZWO EAF',
      'state': 'connected',
      'capabilities': {'min_position': 0, 'max_position': max, 'absolute_focuser': absolute},
      'runtime': {'state': runtime, 'position': position, 'temperature': 8.4},
    };

class _Live extends AutofocusLiveNotifier {
  int starts = 0;
  @override
  AutofocusLive build() => const AutofocusLive();
  void running() => state = const AutofocusLive(run: AutofocusRun(state: AutofocusRunStates.running));
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

Finder _knob(String which) => find.byKey(Key('simple-focuser-$which'));

/// Rolls a knob by [clicks] ridges like a finger: down (positive) goes out,
/// up (negative) goes in.
Future<void> _roll(WidgetTester tester, String which, int clicks, {bool cancel = false}) async {
  final g = await tester.startGesture(tester.getCenter(_knob(which)));
  // A little past the last ridge, in small moves.
  final total = clicks * kSimpleFocusClick + clicks.sign * kSimpleFocusClick / 3;
  for (var i = 1; i <= 10; i++) {
    await g.moveBy(Offset(0, total / 10));
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
  testWidgets('the focuser window: position, temperature, and a coarse and a fine knob', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Focuser · ZWO EAF'), findsOneWidget);
    expect(find.text('12 000'), findsOneWidget);
    expect(find.text('8.4 °C'), findsOneWidget);
    expect(_knob('coarse'), findsOneWidget);
    expect(_knob('fine'), findsOneWidget);
    expect(find.byKey(const Key('simple-focuser-steps')), findsNothing, reason: 'the two knobs replace the step picker');
    expect(tester.getSize(_knob('fine')).height, lessThan(tester.getSize(_knob('coarse')).height),
        reason: 'fine is the small knob, as on a dual-speed focuser');
  });

  testWidgets('a click scales to the travel: coarse about 1 %, fine a tenth of that', (tester) async {
    await _pump(tester, _wire()); // 0-60 000
    expect(find.text('Coarse · 600'), findsOneWidget);
    expect(find.text('Fine · 60'), findsOneWidget);
    await _pump(tester, _wire(max: 5000));
    expect(find.text('Coarse · 50'), findsOneWidget);
    expect(find.text('Fine · 5'), findsOneWidget);
    await _pump(tester, _wire(max: 50));
    expect(find.text('Coarse · 1'), findsOneWidget);
    expect(find.text('Fine · 1'), findsOneWidget, reason: 'never 0');
  });

  testWidgets('a relative focuser, or one with no travel, clicks 100 and 10', (tester) async {
    await _pump(tester, _wire(absolute: false));
    expect(find.text('Coarse · 100'), findsOneWidget);
    expect(find.text('Fine · 10'), findsOneWidget);
    await _pump(tester, _wire(max: 0));
    expect(find.text('Coarse · 100'), findsOneWidget);
    expect(find.text('Fine · 10'), findsOneWidget);
  });

  testWidgets('rolling down goes out and up goes in, as a drawtube; it moves on letting go', (tester) async {
    final f = await _pump(tester, _wire());
    await _roll(tester, 'coarse', 3);
    expect(f.moves, [13800]);
    await _roll(tester, 'fine', -2);
    expect(f.moves.last, 11880, reason: 'from the reported position, 60 a click');
  });

  testWidgets('In is above the knobs and Out below them', (tester) async {
    await _pump(tester, _wire());
    final knobs = tester.getRect(_knob('coarse'));
    expect(tester.getRect(find.text('In')).bottom, lessThanOrEqualTo(knobs.top));
    expect(tester.getRect(find.text('Out')).top, greaterThanOrEqualTo(knobs.bottom));
  });

  testWidgets('the knobs sit beside the position, Autofocus under them', (tester) async {
    await _pump(tester, _wire());
    final pos = tester.getRect(find.text('12 000'));
    final coarse = tester.getRect(_knob('coarse'));
    final fine = tester.getRect(_knob('fine'));
    expect(coarse.left, greaterThan(pos.right), reason: 'the reading left, the knobs right');
    expect(fine.left, greaterThan(coarse.right), reason: 'coarse, then fine, side by side');
    final af = tester.getRect(find.byKey(const Key('simple-focuser-autofocus')));
    expect(af.top, greaterThan(tester.getRect(find.text('Out')).bottom));
    expect(af.width, tester.getRect(find.byKey(const Key('simple-device-focuser'))).width, reason: 'full width');
  });

  testWidgets('Position is where the focuser is; the target only shows in the line under it', (tester) async {
    await _pump(tester, _wire());
    final g = await tester.startGesture(tester.getCenter(_knob('coarse')));
    for (var i = 0; i < 10; i++) {
      await g.moveBy(Offset(0, kSimpleFocusClick * 0.35));
      await tester.pump();
    }
    expect(find.text('12 000'), findsOneWidget);
    expect(find.text('Let go to move to 13 800'), findsOneWidget);
    await g.up();
    await tester.pump();
  });

  testWidgets('a scroll over a knob rolls it, and the focuser moves once the scrolling stops', (tester) async {
    final f = await _pump(tester, _wire());
    final mouse = TestPointer(1, PointerDeviceKind.mouse);
    final at = tester.getCenter(_knob('fine'));
    await tester.sendEventToBinding(mouse.hover(at));
    for (var i = 0; i < 2; i++) {
      await tester.sendEventToBinding(mouse.scroll(const Offset(0, kSimpleFocusClick)));
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text('Let go to move to 12 120'), findsOneWidget);
    expect(f.moves, isEmpty, reason: 'not while the scroll goes on');
    await tester.pump(const Duration(milliseconds: 500));
    expect(f.moves, [12120], reason: 'scrolling down: out, two fine clicks');
  });

  testWidgets('while autofocus runs the knobs move nothing (a touch would spoil its curve)', (tester) async {
    final f = await _pump(tester, _wire());
    _live.running();
    await tester.pump();
    await _roll(tester, 'coarse', 2);
    final mouse = TestPointer(1, PointerDeviceKind.mouse);
    await tester.sendEventToBinding(mouse.hover(tester.getCenter(_knob('coarse'))));
    await tester.sendEventToBinding(mouse.scroll(const Offset(0, 40)));
    await tester.pump(const Duration(seconds: 1));
    expect(f.moves, isEmpty);
  });

  testWidgets('while autofocus runs, its button shows the shutter\'s stop square: same size, red, rounded',
      (tester) async {
    await _pump(tester, _wire());
    _live.running();
    await tester.pump();
    final square = find.descendant(
        of: find.byKey(const Key('simple-focuser-autofocus')), matching: find.byKey(const Key('simple-stop-square')));
    expect(tester.getSize(square), const Size.square(kSimpleStopSquare));
    final box = tester.widget<Container>(square).decoration! as BoxDecoration;
    expect((box.color, box.borderRadius), (AraColors.accentError, BorderRadius.circular(4)));
  });

  testWidgets('with its position unknown the knobs move nothing: no blind jump from 0', (tester) async {
    final f = await _pump(tester, _wire(position: null));
    await _roll(tester, 'coarse', 2);
    expect(f.moves, isEmpty);
  });

  testWidgets('a relative focuser is sent the change, not a position', (tester) async {
    final f = await _pump(tester, _wire(absolute: false));
    await _roll(tester, 'coarse', 2);
    expect(f.moves, [200]);
  });

  testWidgets('while it moves the window says where to', (tester) async {
    await _pump(tester, _wire(runtime: 'moving'));
    expect(find.textContaining('Moving'), findsOneWidget);
  });

  testWidgets('screen readers get each knob as a slider: increase goes out a click', (tester) async {
    final f = await _pump(tester, _wire());
    final node = tester.getSemantics(_knob('fine'));
    expect(node.label, contains('Fine focus'));
    expect(node.value, '12 000');
    node.owner!.performAction(node.id, SemanticsAction.increase);
    await tester.pump();
    expect(f.moves, [12060]);
  });

  testWidgets('the Autofocus button runs autofocus', (tester) async {
    final f = await _pump(tester, _wire());
    await tester.tap(find.byKey(const Key('simple-focuser-autofocus')));
    await tester.pump();
    expect(_live.starts, 1);
    expect(f.moves, isEmpty);
  });

  testWidgets('Refocus every is a slider, 0-10 °C in whole degrees, Never at 0, kept in the profile', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Refocus every'), findsOneWidget);
    expect(find.text('2 °C'), findsOneWidget, reason: "the profile's default");
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-focuser-refocus')));
    expect((slider.min, slider.max, slider.divisions, slider.value), (0.0, 10.0, 10, 2.0));
    slider.onChanged!(3);
    await tester.pump();
    expect(find.text('3 °C'), findsOneWidget, reason: 'the value follows the thumb');
    expect(_c.read(autofocusSettingsProvider).triggerTempDeltaC, 2, reason: 'saved on letting go');
    tester.widget<Slider>(find.byKey(const Key('simple-focuser-refocus'))).onChangeEnd!(3);
    await tester.pump();
    expect(_c.read(autofocusSettingsProvider).triggerTempDeltaC, 3);
    tester.widget<Slider>(find.byKey(const Key('simple-focuser-refocus'))).onChangeEnd!(0);
    await tester.pump();
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
      for (final label in ['Temperature', 'Refocus every', 'Autofocus']) {
        final p = tester.renderObject<RenderParagraph>(find.text(label));
        expect(p.size.height, lessThan(14 * scale * 2), reason: '$label wraps');
      }
    });
  }

  testWidgets('a keyboard rolls a knob: Tab to it, Down goes out a click, Up in', (tester) async {
    final f = await _pump(tester, _wire());
    for (var i = 0; i < 10 && FocusManager.instance.primaryFocus?.debugLabel != 'simple-focuser-coarse'; i++) {
      await tester.sendKeyEvent(LogicalKeyboardKey.tab);
      await tester.pump();
    }
    expect(FocusManager.instance.primaryFocus?.debugLabel, 'simple-focuser-coarse');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    expect(f.moves, [12600], reason: 'down: out a coarse click');
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowUp);
    await tester.pump();
    expect(f.moves.last, 11400, reason: 'up: in a click, from the reported position');
  });

  testWidgets('a roll cut off by the system moves nothing', (tester) async {
    final f = await _pump(tester, _wire());
    await _roll(tester, 'coarse', 3, cancel: true);
    expect(f.moves, isEmpty);
    expect(find.textContaining('Let go to move'), findsNothing);
  });
}
