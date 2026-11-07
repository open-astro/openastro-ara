import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart'
    show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusBlink;

/// A flat panel that records what it was asked to do.
class _Panel extends FlatPanelNotifier {
  _Panel(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// Another command still in flight: asks are dropped (the notifier's re-entrancy guard).
  bool busy = false;

  /// When set, the next command stays in flight until this completes.
  Completer<bool>? hold;

  /// The panel refuses the next command.
  bool refuse = false;

  /// The panel took the command but never showed it.
  bool confirmed = true;

  @override
  Future<FlatPanelStatus?> build() async => FlatPanelStatus.fromJson(json);

  void set(Map<String, dynamic> next) =>
      state = AsyncData(FlatPanelStatus.fromJson(next));

  @override
  Future<bool> apply({bool? openCover, bool? lightOn, int? brightness}) async {
    calls.add(
      [
        if (openCover != null) 'cover=$openCover',
        if (lightOn != null) 'light=$lightOn',
        if (brightness != null) 'brightness=$brightness',
      ].join(' '),
    );
    if (refuse) throw StateError('Cover is moving');
    final held = hold;
    if (held != null) {
      hold = null;
      return held.future;
    }
    return !busy;
  }

  @override
  bool get lastApplyConfirmed => confirmed;
}

Map<String, dynamic> _wire({
  String state = 'connected',
  String runtime = 'cover_closed',
  bool open = false,
  bool light = false,
  int brightness = 128,
  int max = 255,
  bool cover = true,
  bool calibrator = true,
  bool warming = false,
}) => {
  'device_id': 'f',
  'name': 'Flip-Flat',
  'state': state,
  'runtime': {
    'state': runtime,
    'cover_open': open,
    'light_on': light,
    'brightness': brightness,
    'max_brightness': max,
    'has_cover': cover,
    'has_calibrator': calibrator,
    'light_warming': warming,
  },
};

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
    sequenceId: 'seq-1',
    state: SequenceRunState.running,
  );
}

Future<(ProviderContainer, _Panel)> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire, {
  bool inSettings = false,
  DeviceDriver? driver,
  bool planRunning = false,
  double? width,
}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final panel = _Panel(wire);
  final c = ProviderContainer(
    overrides: [
      flatPanelProvider.overrideWith(() => panel),
      simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'flatdevice' ? driver : null,
      ),
      if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: width,
              child: SimpleFlatPanelWindow(inSettings: inSettings),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return (c, panel);
}

Finder _key(String k) => find.byKey(Key('simple-flat-$k'));
bool _selected(WidgetTester t, String k) =>
    t.widget<SimpleSegment>(_key(k)).selected;

void main() {
  testWidgets('cover and light as segments, and the panel face beside them', (
    tester,
  ) async {
    final (_, panel) = await _pump(tester, _wire());
    expect(find.text('Flat panel · Flip-Flat'), findsOneWidget);
    expect(_selected(tester, 'cover-closed'), isTrue);
    expect(_selected(tester, 'light-off'), isTrue);
    expect(_key('face'), findsOneWidget);
    expect(
      find.text('50 %'),
      findsOneWidget,
      reason: 'the level as a percent of the panel\'s maximum (128 of 255)',
    );
    await tester.tap(_key('cover-open'));
    await tester.pump();
    await tester.tap(_key('light-on'));
    await tester.pump();
    expect(panel.calls, ['cover=true', 'light=true']);
  });

  testWidgets('the brightness slider keeps its width from 0 % to 100 %', (
    tester,
  ) async {
    await _pump(tester, _wire(brightness: 0));
    final empty = tester.getSize(_key('brightness')).width;
    await _pump(tester, _wire(brightness: 255, light: true));
    expect(tester.getSize(_key('brightness')).width, empty);
  });

  testWidgets('the brightness is sent once, when the slider is let go', (
    tester,
  ) async {
    final (_, panel) = await _pump(tester, _wire(light: true));
    await tester.drag(_key('brightness'), const Offset(-400, 0));
    await tester.pump();
    expect(panel.calls, [
      'brightness=0',
    ], reason: 'dragged to the left end; one command on release');
  });

  testWidgets(
    'a moving cover reads Moving… in blinking green and takes no tap',
    (tester) async {
      await _pump(tester, _wire(runtime: 'cover_moving'));
      expect(find.text('Moving…'), findsOneWidget);
      expect(
        tester
            .widget<StatusBlink>(
              find.ancestor(
                of: find.text('Moving…'),
                matching: find.byType(StatusBlink),
              ),
            )
            .on,
        isTrue,
      );
      expect(
        tester.widget<SimpleSegment>(_key('cover-open')).onTap,
        isNull,
        reason: 'the segments stay, locked, so the window keeps its size',
      );
    },
  );

  testWidgets('a warming light reads Warming up…', (tester) async {
    await _pump(tester, _wire(light: true, warming: true, runtime: 'light_on'));
    expect(find.text('Warming up…'), findsOneWidget);
    expect(tester.widget<SimpleSegment>(_key('light-off')).onTap, isNull);
  });

  testWidgets(
    'the window keeps its size while the cover moves and the light warms up',
    (tester) async {
      final (_, panel) = await _pump(tester, _wire(), width: 328);
      // The slider is the window's last row: where it sits is the window's height.
      double slider() => tester.getTopLeft(_key('brightness')).dy;
      final top = slider();
      panel.set(_wire(runtime: 'cover_moving'));
      await tester.pump();
      expect(find.text('Moving…'), findsOneWidget);
      expect(slider(), top);
      panel.set(_wire(light: true, warming: true, runtime: 'light_on'));
      await tester.pump();
      expect(find.text('Warming up…'), findsOneWidget);
      expect(slider(), top);
    },
  );

  testWidgets('the panel face is 140 pt in the 360 pt device window', (
    tester,
  ) async {
    await _pump(tester, _wire(), width: 328); // less the window's padding
    expect(tester.takeException(), isNull);
    expect(tester.getSize(_key('face')), const Size.square(140));
  });

  testWidgets(
    'a cover-only panel shows no light; a light-only panel no cover',
    (tester) async {
      await _pump(tester, _wire(calibrator: false));
      expect(_key('cover-open'), findsOneWidget);
      expect(_key('light-on'), findsNothing);
      expect(_key('brightness'), findsNothing);
      await _pump(tester, _wire(cover: false));
      expect(_key('cover-open'), findsNothing);
      expect(_key('light-on'), findsOneWidget);
      expect(_key('brightness'), findsOneWidget);
    },
  );

  testWidgets('a level just above 0 reads 1 %, never 0 % with the light on', (
    tester,
  ) async {
    await _pump(tester, _wire(light: true, brightness: 1, max: 4095));
    expect(find.text('1 %'), findsOneWidget);
  });

  testWidgets(
    'before the panel reports its maximum the slider is disabled and reads —',
    (tester) async {
      await _pump(tester, _wire(max: 0));
      expect(tester.widget<Slider>(_key('brightness')).onChanged, isNull);
      expect(find.text('—'), findsOneWidget);
    },
  );

  testWidgets(
    'a tap while a brightness command runs says busy and leaves it be',
    (tester) async {
      final (c, panel) = await _pump(tester, _wire(light: true));
      final hold = panel.hold = Completer<bool>();
      await tester.drag(_key('brightness'), const Offset(-400, 0));
      await tester.pump();
      expect(panel.calls, ['brightness=0']);
      await tester.tap(_key('cover-open'));
      await tester.pump();
      expect(panel.calls, [
        'brightness=0',
      ], reason: 'the second ask is not sent');
      expect(tester.widget<Slider>(_key('brightness')).value, 0);
      expect(
        tester.widget<SimpleSegment>(_key('light-on')).onTap,
        isNull,
        reason: 'the light stays locked while the first command runs',
      );
      expect(c.read(simpleMessagesProvider).single.title, 'Still busy');
      hold.complete(true);
      await tester.pump();
      expect(tester.widget<Slider>(_key('brightness')).value, 128);
      await tester.pump(const Duration(seconds: 11)); // the pill fades
    },
  );

  testWidgets('a second drag while a brightness command runs is ignored', (
    tester,
  ) async {
    final (_, panel) = await _pump(tester, _wire(light: true));
    final hold = panel.hold = Completer<bool>();
    await tester.drag(_key('brightness'), const Offset(-400, 0));
    await tester.pump();
    expect(
      tester.widget<Slider>(_key('brightness')).onChanged,
      isNull,
      reason: 'the slider is locked while the first command runs',
    );
    await tester.drag(_key('brightness'), const Offset(400, 0));
    await tester.pump();
    expect(tester.widget<Slider>(_key('brightness')).value, 0);
    expect(panel.calls, ['brightness=0']);
    hold.complete(true);
    await tester.pump();
    await tester.pump(const Duration(seconds: 11));
  });

  testWidgets('a refused command says why in a pill', (tester) async {
    final (c, panel) = await _pump(tester, _wire());
    panel.refuse = true;
    await tester.tap(_key('cover-open'));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect(
      (pill.kind, pill.title),
      (SimpleMessageKind.error, "Couldn't change the flat panel"),
    );
  });

  testWidgets('a command the panel never showed says so in a pill', (
    tester,
  ) async {
    final (c, panel) = await _pump(tester, _wire());
    panel.confirmed = false;
    await tester.tap(_key('light-on'));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect(
      (pill.kind, pill.title),
      (SimpleMessageKind.error, "The flat panel didn't make that change"),
    );
  });

  testWidgets(
    'a tap dropped while the last command runs says so in a pill, once',
    (tester) async {
      final (c, panel) = await _pump(tester, _wire());
      panel.busy = true;
      await tester.tap(_key('cover-open'));
      await tester.pump();
      await tester.tap(_key('light-on'));
      await tester.pump();
      final pill = c.read(simpleMessagesProvider).single;
      expect((pill.kind, pill.title), (SimpleMessageKind.error, 'Still busy'));
      await tester.pump(const Duration(seconds: 11)); // it fades like a result
    },
  );

  testWidgets('while a plan runs, a change asks first, in the window', (
    tester,
  ) async {
    final (_, panel) = await _pump(tester, _wire(), planRunning: true);
    await tester.pump(); // the run state arrives
    await tester.tap(_key('cover-open'));
    await tester.pump();
    expect(panel.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(panel.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsNothing);
    await tester.drag(_key('brightness'), const Offset(-400, 0));
    await tester.pump();
    expect(panel.calls, isEmpty, reason: 'the brightness asks too');
    await tester.tap(find.text('Change anyway'));
    await tester.pump();
    expect(panel.calls, ['brightness=0']);
  });

  testWidgets(
    'in Settings it ends with the panel\'s versions; from the dot, none',
    (tester) async {
      const driver = DeviceDriver(driverVersion: '2.3.1', firmware: '3.11');
      await _pump(tester, _wire(), driver: driver);
      expect(find.text('Versions'), findsNothing);
      await _pump(tester, _wire(), driver: driver, inSettings: true);
      await tester.pump();
      expect(find.text('Firmware'), findsOneWidget);
      expect(find.text('3.11'), findsOneWidget);
    },
  );

  testWidgets('a panel that isn\'t connected shows its state and no controls', (
    tester,
  ) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(_key('cover-open'), findsNothing);
    expect(_key('face'), findsNothing);
  });

  testWidgets(
    'on a narrow window the face gives way and the controls stay whole',
    (tester) async {
      await _pump(tester, _wire(), width: 260);
      expect(_key('face'), findsNothing);
      expect(_key('cover-open'), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('the brightness level keeps tabular figures', (tester) async {
    await _pump(tester, _wire(light: true, brightness: 1, max: 4095));
    final level = tester.widget<Text>(find.text('1 %'));
    expect(level.style?.fontFeatures, contains(FontFeature.tabularFigures()));
  });

  testWidgets('a slider level is measured in the text style it draws', (
    tester,
  ) async {
    late BuildContext ctx;
    await tester.pumpWidget(
      MaterialApp(
        home: DefaultTextStyle(
          style: const TextStyle(letterSpacing: 6),
          child: Builder(
            builder: (c) {
              ctx = c;
              return Align(
                alignment: Alignment.topLeft,
                child: simpleSliderLevel(ctx, '100 %'),
              );
            },
          ),
        ),
      ),
    );
    final drawn = tester
        .renderObject<RenderParagraph>(find.text('100 %'))
        .getMaxIntrinsicWidth(double.infinity);
    expect(
      tester.getSize(find.text('100 %')).width,
      greaterThanOrEqualTo(drawn),
      reason: 'the reserved width fits the text as drawn',
    );
  });
}
