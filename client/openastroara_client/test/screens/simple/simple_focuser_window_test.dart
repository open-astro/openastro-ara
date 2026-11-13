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

  final actions = <String>[];

  @override
  Future<bool> setZero() async {
    actions.add('setzero');
    return true;
  }

  @override
  Future<bool> calibrate({bool abort = false}) async {
    actions.add(abort ? 'calibrate/abort' : 'calibrate');
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

Future<_Focuser> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {double width = 360, String? font, bool scrolling = false, bool inSettings = false}) async {
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
        home: Scaffold(
            body: scrolling
                // As in Settings: the window on a page that scrolls.
                ? SingleChildScrollView(
                    child: Column(children: [SizedBox(width: width, child: SimpleFocuserWindow(inSettings: inSettings)), const SizedBox(height: 2000)]))
                : Center(child: SizedBox(width: width, child: const SimpleFocuserWindow())))),
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

Map<String, dynamic> _withActions(Map<String, dynamic> wire, {bool zero = false, bool calibrate = false, String? calibration}) => {
      ...wire,
      'capabilities': {...wire['capabilities'] as Map<String, dynamic>, 'can_set_zero': zero, 'can_calibrate': calibrate},
      'runtime': {...wire['runtime'] as Map<String, dynamic>, 'calibration': ?calibration},
    };

void main() {
  testWidgets('Settings: Set zero asks first, then makes the current position 0', (tester) async {
    final f = await _pump(tester, _withActions(_wire(), zero: true), scrolling: true, inSettings: true);
    expect(find.byKey(const Key('simple-focuser-position-section')), findsOneWidget);
    expect(find.byKey(const Key('simple-focuser-calibrate')), findsNothing, reason: 'only what the driver offers');
    await tester.ensureVisible(find.byKey(const Key('simple-focuser-set-zero')));
    await tester.tap(find.byKey(const Key('simple-focuser-set-zero')));
    await tester.pump();
    expect(f.actions, isEmpty, reason: 'it asks first');
    expect(find.textContaining('Make 12000 the new 0?'), findsOneWidget);
    await tester.ensureVisible(find.widgetWithText(TextButton, 'Set zero'));
    await tester.tap(find.widgetWithText(TextButton, 'Set zero'));
    await tester.pump();
    expect(f.actions, ['setzero']);
  });

  testWidgets('Settings: Calibrate travel starts it, and while it runs Stop ends it and Set zero waits', (tester) async {
    final f = await _pump(tester, _withActions(_wire(), zero: true, calibrate: true, calibration: 'not_calibrated'),
        scrolling: true, inSettings: true);
    await tester.ensureVisible(find.byKey(const Key('simple-focuser-calibrate')));
    await tester.tap(find.byKey(const Key('simple-focuser-calibrate')));
    await tester.pump();
    expect(f.actions, ['calibrate']);
    f.state = AsyncData(FocuserStatus.fromJson(_withActions(_wire(), zero: true, calibrate: true, calibration: 'calibrating')));
    await tester.pump();
    expect(find.text('Running to both stops…'), findsOneWidget);
    expect(tester.widget<OutlinedButton>(find.byKey(const Key('simple-focuser-set-zero'))).onPressed, isNull);
    await tester.ensureVisible(find.byKey(const Key('simple-focuser-calibrate-stop')));
    await tester.tap(find.byKey(const Key('simple-focuser-calibrate-stop')));
    await tester.pump();
    expect(f.actions, ['calibrate', 'calibrate/abort']);
  });

  testWidgets('no Position section when the driver offers neither, nor outside Settings', (tester) async {
    await _pump(tester, _wire(), scrolling: true, inSettings: true);
    expect(find.byKey(const Key('simple-focuser-position-section')), findsNothing);
    await _pump(tester, _withActions(_wire(), zero: true, calibrate: true), scrolling: true);
    expect(find.byKey(const Key('simple-focuser-set-zero')), findsNothing, reason: 'the night window stays knobs');
  });

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

  /// One click out on [which], as a screen reader's increase sends it.
  Future<void> clickOut(WidgetTester tester, String which) async {
    final node = tester.getSemantics(_knob(which));
    node.owner!.performAction(node.id, SemanticsAction.increase);
    await tester.pump();
  }

  testWidgets('a click scales to the travel: coarse about 1 %, fine a tenth of that', (tester) async {
    var f = await _pump(tester, _wire()); // 0-60 000
    await clickOut(tester, 'coarse');
    await clickOut(tester, 'fine');
    expect(f.moves, [12600, 12060]);
    f = await _pump(tester, _wire(max: 5000, position: 2000));
    await clickOut(tester, 'coarse');
    await clickOut(tester, 'fine');
    expect(f.moves, [2050, 2005]);
    f = await _pump(tester, _wire(max: 50, position: 20));
    await clickOut(tester, 'coarse');
    await clickOut(tester, 'fine');
    expect(f.moves, [21, 21], reason: 'never 0');
  });

  testWidgets('a relative focuser, or one with no travel, clicks 100 and 10', (tester) async {
    var f = await _pump(tester, _wire(absolute: false));
    await clickOut(tester, 'coarse');
    await clickOut(tester, 'fine');
    expect(f.moves, [100, 10], reason: 'a relative focuser is sent the change');
    f = await _pump(tester, _wire(max: 0));
    await clickOut(tester, 'coarse');
    await clickOut(tester, 'fine');
    expect(f.moves, [12100, 12010]);
  });

  testWidgets('under each knob its name only, no click size (user, 2026-11-07)', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Coarse'), findsOneWidget);
    expect(find.text('Fine'), findsOneWidget);
    expect(find.textContaining('('), findsNothing);
  });

  testWidgets('In and Out carry arrows: up above the knobs, down under them', (tester) async {
    await _pump(tester, _wire());
    final up = tester.getRect(find.byIcon(Icons.arrow_drop_up));
    final down = tester.getRect(find.byIcon(Icons.arrow_drop_down));
    final inText = tester.getRect(find.text('In'));
    final outText = tester.getRect(find.text('Out'));
    expect(up.center.dy, moreOrLessEquals(inText.center.dy, epsilon: 4), reason: 'beside In');
    expect(down.center.dy, moreOrLessEquals(outText.center.dy, epsilon: 4), reason: 'beside Out');
    expect(up.bottom, lessThanOrEqualTo(tester.getRect(_knob('coarse')).top));
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

  testWidgets('the two knobs\' marks line up: both centred on one line, their captions on another', (tester) async {
    await _pump(tester, _wire());
    expect(tester.getCenter(_knob('fine')).dy, moreOrLessEquals(tester.getCenter(_knob('coarse')).dy, epsilon: 0.5));
    expect(tester.getRect(find.text('Fine')).top, moreOrLessEquals(tester.getRect(find.text('Coarse')).top, epsilon: 0.5));
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

  for (final scrolling in [false, true]) {
    testWidgets('a mouse wheel over a knob rolls it, on a page that scrolls too: $scrolling', (tester) async {
      final f = await _pump(tester, _wire(), scrolling: scrolling);
      final mouse = TestPointer(1, PointerDeviceKind.mouse);
      await tester.sendEventToBinding(mouse.hover(tester.getCenter(_knob('coarse'))));
      await tester.sendEventToBinding(mouse.scroll(const Offset(0, 50))); // one wheel notch
      await tester.pump();
      expect(find.text('Let go to move to 12 600'), findsOneWidget, reason: 'one notch, one click');
      await tester.pump(const Duration(seconds: 1));
      expect(f.moves, [12600]);
    });

    testWidgets('a two-finger trackpad scroll over a knob rolls it, on a page that scrolls too: $scrolling', (tester) async {
      final f = await _pump(tester, _wire(), scrolling: scrolling);
      final at = tester.getCenter(_knob('coarse'));
      final g = await tester.startGesture(at, kind: PointerDeviceKind.trackpad);
      for (var i = 1; i <= 10; i++) {
        await g.panZoomUpdate(at, pan: Offset(0, i * kSimpleFocusClick * 0.35));
        await tester.pump();
      }
      expect(find.textContaining('Let go to move to'), findsOneWidget);
      await g.panZoomEnd();
      await tester.pump(const Duration(seconds: 1));
      expect(f.moves, hasLength(1));
    });
  }

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

  testWidgets('Autofocus is green to start; while it runs the button is red, reads Cancel autofocus, and has no stop square',
      (tester) async {
    await _pump(tester, _wire());
    Color fill() => tester
        .widget<Material>(find.ancestor(of: find.byKey(const Key('simple-focuser-autofocus')), matching: find.byType(Material)).first)
        .color!;
    expect(fill(), AraColors.accentConnected);
    _live.running();
    await tester.pump();
    expect(fill(), AraColors.emergencyStop, reason: 'white on it keeps 5:1; Fault Red gives 4.2 (#1670 re-audit P1)');
    expect(find.descendant(of: find.byKey(const Key('simple-focuser-autofocus')), matching: find.text('Cancel autofocus')),
        findsOneWidget);
    expect(find.descendant(of: find.byKey(const Key('simple-focuser-autofocus')), matching: find.byKey(const Key('simple-stop-square'))),
        findsNothing);
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

  testWidgets('a locked knob leaves the finger to the page: a swipe on it scrolls (#1670 audit P1)', (tester) async {
    final f = await _pump(tester, _wire(), scrolling: true);
    _live.running();
    await tester.pump();
    final page = tester.state<ScrollableState>(find.byType(Scrollable)).position;
    await tester.drag(_knob('coarse'), const Offset(0, -200));
    await tester.pump();
    expect(page.pixels, greaterThan(0), reason: 'the page scrolled');
    expect(f.moves, isEmpty);
  });

  testWidgets('dragging off a knob drops the target: nothing moves (#1670 audit P1)', (tester) async {
    final f = await _pump(tester, _wire());
    final g = await tester.startGesture(tester.getCenter(_knob('coarse')));
    for (var i = 0; i < 10; i++) {
      await g.moveBy(Offset(0, kSimpleFocusClick * 0.35));
      await tester.pump();
    }
    expect(find.text('Let go to move to 13 800'), findsOneWidget);
    await g.moveBy(Offset(tester.getSize(_knob('coarse')).width * 2, 0)); // past a knob's width off its side
    await tester.pump();
    expect(find.textContaining('Let go to move'), findsNothing, reason: 'the target dropped as the finger left');
    await g.moveBy(const Offset(0, 60));
    await tester.pump();
    await g.up();
    await tester.pump();
    expect(f.moves, isEmpty);
  });

  testWidgets('a locked knob reads as disabled to screen readers, with nothing to adjust (#1670 audit P2)', (tester) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, _wire());
    _live.running();
    await tester.pump();
    final node = tester.getSemantics(_knob('coarse'));
    expect(node, isSemantics(isSlider: true, hasEnabledState: true, isEnabled: false));
    expect(node.getSemanticsData().hasAction(SemanticsAction.increase), isFalse);
    semantics.dispose();
  });

  testWidgets('screen readers hear which way increase goes: out (#1670 audit P2)', (tester) async {
    await _pump(tester, _wire());
    expect(tester.getSemantics(_knob('fine')).hint, 'Increase moves out, decrease moves in');
  });

  test('a negative position keeps its minus sign (#1670 audit)', () {
    expect(simpleSteps(-1200), '−1 200');
    expect(simpleSteps(-5), '−5');
    expect(simpleSteps(12000), '12 000');
  });

  testWidgets('a busy Autofocus button reads as disabled (#1670 re-audit P3)', (tester) async {
    final semantics = tester.ensureSemantics();
    await _pump(tester, _wire());
    _live.state = const AutofocusLive(busy: true);
    await tester.pump();
    expect(tester.getSemantics(find.byKey(const Key('simple-focuser-autofocus'))),
        isSemantics(isButton: true, hasEnabledState: true, isEnabled: false));
    semantics.dispose();
  });

  testWidgets('a roll cut off by the system moves nothing', (tester) async {
    final f = await _pump(tester, _wire());
    await _roll(tester, 'coarse', 3, cancel: true);
    expect(f.moves, isEmpty);
    expect(find.textContaining('Let go to move'), findsNothing);
  });
}
