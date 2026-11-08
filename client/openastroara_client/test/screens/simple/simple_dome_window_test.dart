import 'package:flutter/gestures.dart' show PointerDeviceKind;
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_shell.dart'
    show SimpleSegment;
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/dome_shutter_state.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusBlink;

/// A dome that records what it was asked to do.
class _Dome extends DomeNotifier {
  _Dome(this.json);
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// Another command still in flight: asks are dropped.
  bool busy = false;

  /// The dome refuses the next command.
  bool refuse = false;

  @override
  Future<DomeStatus?> build() async => DomeStatus.fromJson(json);

  Future<bool> _call(String name) async {
    calls.add(name);
    if (refuse) throw StateError('Shutter jammed');
    return !busy;
  }

  @override
  Future<bool> openShutter() => _call('open');
  @override
  Future<bool> closeShutter() => _call('close');
  @override
  Future<bool> park() => _call('park');
  @override
  Future<bool> findHome() => _call('home');
  @override
  Future<bool> abortSlew() => _call('abort');
  @override
  Future<bool> slew(double targetAzimuthDeg) =>
      _call('slew ${targetAzimuthDeg.round()}');

  void set(Map<String, dynamic> next) =>
      state = AsyncData(DomeStatus.fromJson(next));
}

/// Shutter times kept in memory, not on disk.
class _Times extends DomeShutterTimesService {
  _Times() : times = {};
  Map<String, double> times;

  @override
  Future<Map<String, double>> load() async => Map.of(times);

  @override
  Future<void> save(Map<String, double> next) async => times = Map.of(next);
}

Map<String, dynamic> _wire({
  String state = 'connected',
  String runtime = 'idle',
  double? azimuth = 212,
  bool open = false,
  bool parked = false,
  bool home = false,
  bool shutter = true,
  bool rotates = true,
  bool canPark = true,
  bool canHome = true,
}) => {
  'device_id': 'd',
  'name': 'ScopeDome',
  'state': state,
  'capabilities': {
    'can_set_shutter': shutter,
    'can_set_azimuth': rotates,
    'can_park': canPark,
    'can_find_home': canHome,
  },
  'runtime': {
    'state': runtime,
    'azimuth_deg': azimuth,
    'shutter_open': open,
    'at_home': home,
    'parked': parked,
  },
};

class _Running extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => const SequenceRunStateInfo(
    sequenceId: 'seq-1',
    state: SequenceRunState.running,
  );
}

Future<(ProviderContainer, _Dome)> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire, {
  bool inSettings = false,
  DeviceDriver? driver,
  bool planRunning = false,
  double width = 328,
  Locale? locale,
  _Times? times,
}) async {
  await tester.pumpWidget(const SizedBox());
  await tester.pump();
  final dome = _Dome(wire);
  final c = ProviderContainer(
    overrides: [
      domeProvider.overrideWith(() => dome),
      domeShutterTimesServiceProvider.overrideWithValue(times ?? _Times()),
      simpleModeActiveProvider.overrideWithValue(true), // errors go to a pill
      deviceDriverProvider.overrideWith(
        (ref, type) async => type == 'dome' ? driver : null,
      ),
      if (planRunning) sequenceRunStateProvider.overrideWith(_Running.new),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        locale: locale,
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Align(
            alignment: Alignment.topLeft,
            child: SizedBox(
              width: width,
              child: SimpleDomeWindow(inSettings: inSettings),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  return (c, dome);
}

Finder _key(String k) => find.byKey(Key('simple-dome-$k'));

void main() {
  testWidgets(
    'the dome from the front and from above, its azimuth, and the shutter as segments',
    (tester) async {
      final (_, dome) = await _pump(tester, _wire());
      expect(find.text('Dome · ScopeDome'), findsOneWidget);
      expect(tester.getSize(_key('front')), const Size.square(150));
      expect(tester.getSize(_key('dial')), const Size.square(150));
      expect(
        tester.getCenter(_key('dial')).dx,
        lessThan(tester.getCenter(_key('front')).dx),
        reason: 'the dial on the left, the drawing on the right',
      );
      expect(find.text('212°'), findsOneWidget);
      expect(find.text('SW'), findsOneWidget);
      expect(
        tester.widget<SimpleSegment>(_key('shutter-closed')).selected,
        isTrue,
      );
      await tester.tap(_key('shutter-open'));
      await tester.pump();
      await tester.tap(_key('park'));
      await tester.pump();
      await tester.tap(_key('home'));
      await tester.pump();
      expect(dome.calls, ['open', 'park', 'home']);
      await tester.pump(const Duration(seconds: 4)); // the ask's timer
    },
  );

  testWidgets('parked and at home read under the azimuth', (tester) async {
    await _pump(tester, _wire(parked: true));
    expect(find.text('Azimuth · Parked'), findsOneWidget);
    await _pump(tester, _wire(home: true, azimuth: 0));
    expect(find.text('Azimuth · At home'), findsOneWidget);
  });

  testWidgets(
    'turning reads Moving… in blinking green; Stop sits in the dial\'s middle',
    (tester) async {
      final (_, dome) = await _pump(tester, _wire(runtime: 'slewing'));
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
      expect(tester.widget<SimpleSegment>(_key('shutter-open')).onTap, isNull);
      await tester.tap(_key('park'));
      await tester.pump();
      expect(dome.calls, isEmpty, reason: 'Park and Home lock while it moves');
      await tester.tap(_key('hub'));
      await tester.pump();
      expect(dome.calls, ['abort']);
    },
  );

  testWidgets('the shutter moving reads Shutter moving… and Stop shows', (
    tester,
  ) async {
    final (_, dome) = await _pump(tester, _wire(runtime: 'shutter_moving'));
    expect(find.text('Shutter moving…'), findsOneWidget);
    await tester.tap(_key('hub'));
    await tester.pump();
    expect(dome.calls, ['abort']);
  });

  testWidgets('no Stop while the dome stands still', (tester) async {
    await _pump(tester, _wire());
    expect(_key('stop'), findsNothing);
  });

  testWidgets(
    'a roll-off roof gets the shutter alone: no azimuth, no Park, no Home',
    (tester) async {
      final (_, dome) = await _pump(
        tester,
        _wire(rotates: false, canPark: false, canHome: false, azimuth: null),
      );
      expect(_key('dial'), findsNothing);
      expect(find.textContaining('Azimuth'), findsNothing);
      expect(_key('park'), findsNothing);
      expect(_key('home'), findsNothing);
      await tester.tap(_key('shutter-open'));
      await tester.pump();
      expect(dome.calls, ['open']);
      await tester.pump(const Duration(seconds: 4)); // the ask's timer
    },
  );

  testWidgets('a roof that moves gets its Stop as a button', (tester) async {
    final (_, dome) = await _pump(
      tester,
      _wire(
        rotates: false,
        canPark: false,
        canHome: false,
        runtime: 'shutter_moving',
      ),
    );
    await tester.tap(_key('stop'));
    await tester.pump();
    expect(dome.calls, ['abort']);
  });

  testWidgets('a dome without park or home has neither button', (tester) async {
    await _pump(tester, _wire(canPark: false, canHome: false));
    expect(_key('dial'), findsOneWidget);
    expect(_key('park'), findsNothing);
    expect(_key('home'), findsNothing);
  });

  testWidgets('a refused command says why in a pill', (tester) async {
    final (c, dome) = await _pump(tester, _wire());
    dome.refuse = true;
    await tester.tap(_key('shutter-open'));
    await tester.pump();
    final pill = c.read(simpleMessagesProvider).single;
    expect(
      (pill.kind, pill.title),
      (SimpleMessageKind.error, "Couldn't move the dome"),
    );
  });

  testWidgets(
    'a tap dropped while the last command runs says so in a pill, once',
    (tester) async {
      final (c, dome) = await _pump(tester, _wire());
      dome.busy = true;
      await tester.tap(_key('park'));
      await tester.pump();
      await tester.tap(_key('home'));
      await tester.pump();
      final pill = c.read(simpleMessagesProvider).single;
      expect((pill.kind, pill.title), (SimpleMessageKind.error, 'Still busy'));
      await tester.pump(const Duration(seconds: 11)); // it fades like a result
    },
  );

  testWidgets('while a plan runs, a move asks first; Stop never asks', (
    tester,
  ) async {
    // Wide: the test font is wider than the real one and overflows the ask.
    final (_, dome) = await _pump(
      tester,
      _wire(),
      planRunning: true,
      width: 600,
    );
    await tester.pump(); // the run state arrives
    await tester.tap(_key('shutter-open'));
    await tester.pump();
    expect(dome.calls, isEmpty);
    expect(find.textContaining('A plan is running'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pump();
    expect(find.textContaining('A plan is running'), findsNothing);
    await tester.tap(_key('park'));
    await tester.pump();
    expect(dome.calls, isEmpty);
    await tester.tap(find.text('Change anyway'));
    await tester.pump();
    expect(dome.calls, ['park']);
    final (_, moving) = await _pump(
      tester,
      _wire(runtime: 'slewing'),
      planRunning: true,
    );
    await tester.pump();
    await tester.tap(_key('hub'));
    await tester.pump();
    expect(moving.calls, ['abort']);
    await tester.pump(const Duration(seconds: 4)); // the ask's timer
  });

  testWidgets(
    'drag the dial to a target, then its middle turns the dome there',
    (tester) async {
      final (_, dome) = await _pump(tester, _wire());
      // East is the dial's right edge.
      final dial = tester.getRect(_key('dial'));
      await tester.dragFrom(
        Offset(dial.right - 10, dial.center.dy - 30),
        const Offset(0, 30),
      );
      await tester.pump();
      expect(find.text('→ 90°'), findsOneWidget);
      await tester.tap(_key('hub'));
      await tester.pump();
      expect(dome.calls, ['slew 90']);
      expect(find.text('→ 90°'), findsNothing, reason: 'spent');
      await tester.pump(const Duration(seconds: 4)); // the ask's timer
    },
  );

  testWidgets(
    'two fingers on a trackpad step the target from where it is, each time',
    (tester) async {
      await _pump(tester, _wire());
      // Anywhere on the face: where the fingers land doesn't pick a target.
      final at = tester.getRect(_key('dial')).centerLeft + const Offset(12, 0);
      Future<void> scroll(double dy) async {
        final g = await tester.createGesture(kind: PointerDeviceKind.trackpad);
        await g.panZoomStart(at);
        await g.panZoomUpdate(at, pan: Offset(0, dy));
        await g.panZoomEnd();
        await tester.pump();
      }

      await scroll(21); // 7 pt a degree, down clockwise, as the rotator's
      expect(find.text('→ 215°'), findsOneWidget);
      await scroll(14);
      expect(
        find.text('→ 217°'),
        findsOneWidget,
        reason: 'a second scroll goes on from the target, not from the finger',
      );
    },
  );

  testWidgets('a mouse wheel steps the target a degree a notch', (
    tester,
  ) async {
    await _pump(tester, _wire());
    final at = tester.getRect(_key('dial')).centerLeft + const Offset(12, 0);
    final g = TestPointer(1, PointerDeviceKind.mouse);
    await tester.sendEventToBinding(g.hover(at));
    await tester.sendEventToBinding(g.scroll(const Offset(0, 20)));
    await tester.pump();
    await tester.sendEventToBinding(g.scroll(const Offset(0, 20)));
    await tester.pump();
    expect(find.text('→ 214°'), findsOneWidget);
  });

  testWidgets('without a target the dial\'s middle takes no tap', (
    tester,
  ) async {
    final (_, dome) = await _pump(tester, _wire());
    await tester.tap(_key('hub'));
    await tester.pump();
    expect(dome.calls, isEmpty);
  });

  testWidgets('while a plan runs, a turn asks first', (tester) async {
    final (_, dome) = await _pump(
      tester,
      _wire(),
      planRunning: true,
      width: 600,
    );
    await tester.pump();
    final dial = tester.getRect(_key('dial'));
    await tester.tapAt(Offset(dial.center.dx, dial.bottom - 8)); // south
    await tester.pump();
    expect(find.text('→ 180°'), findsOneWidget);
    await tester.tap(_key('hub'));
    await tester.pump();
    expect(dome.calls, isEmpty);
    await tester.tap(find.text('Change anyway'));
    await tester.pump();
    expect(dome.calls, ['slew 180']);
    await tester.pump(const Duration(seconds: 4)); // the ask's timer
  });

  testWidgets(
    'a shutter seen from its start reads Opening…, and its time is kept',
    (tester) async {
      final times = _Times();
      final (_, dome) = await _pump(tester, _wire(), times: times);
      await tester.tap(_key('shutter-open'));
      await tester.pump();
      dome.set(_wire(runtime: 'shutter_moving'));
      await tester.pump();
      expect(find.text('Opening…'), findsOneWidget);
      await tester.pump(const Duration(seconds: 12));
      dome.set(_wire(open: true));
      await tester.pump();
      expect(find.text('Opening…'), findsNothing);
      expect(
        times.times['d'],
        closeTo(12, 0.5),
        reason: 'the next move slides over about 12 s',
      );
      dome.set(_wire(open: true, runtime: 'shutter_moving'));
      await tester.pump();
      expect(find.text('Closing…'), findsOneWidget);
      dome.set(_wire(open: true));
      await tester.pump();
    },
  );

  testWidgets('the window keeps its size while the dome moves', (tester) async {
    for (final rotates in [true, false]) {
      final (_, dome) = await _pump(
        tester,
        _wire(rotates: rotates, canPark: rotates, canHome: rotates),
      );
      // The window shrink-wraps in the shell: where its last row sits is its
      // height.
      double bottom() => tester
          .getBottomLeft(
            find.byKey(
              Key(
                rotates ? 'simple-window-row-dome-shutter' : 'simple-dome-stop',
              ),
            ),
          )
          .dy;
      final still = bottom();
      for (final runtime in ['shutter_moving', 'slewing', 'idle']) {
        dome.set(
          _wire(
            rotates: rotates,
            canPark: rotates,
            canHome: rotates,
            runtime: runtime,
          ),
        );
        await tester.pump();
        expect(bottom(), still, reason: '$runtime, rotates: $rotates');
      }
    }
  });

  testWidgets('a shutter move Ara didn\'t ask for is drawn but not timed', (
    tester,
  ) async {
    // A plan or the safety policy started it: seen up to a poll late, its
    // start would make the learned time too short.
    final times = _Times();
    final (_, dome) = await _pump(tester, _wire(), times: times);
    dome.set(_wire(runtime: 'shutter_moving'));
    await tester.pump();
    expect(find.text('Opening…'), findsOneWidget);
    await tester.pump(const Duration(seconds: 12));
    dome.set(_wire(open: true));
    await tester.pump();
    expect(times.times, isEmpty);
  });

  testWidgets('a shutter fault says so in red, in the moving line\'s place', (
    tester,
  ) async {
    // Wide: the test font is wider than the real one and wraps the line.
    final (_, dome) = await _pump(tester, _wire(), width: 600);
    double bottom() => tester
        .getBottomLeft(find.byKey(const Key('simple-window-row-dome-shutter')))
        .dy;
    final still = bottom();
    dome.set(_wire(runtime: 'error'));
    await tester.pump();
    expect(find.text('Shutter fault: open or close again.'), findsOneWidget);
    expect(
      tester
          .widget<Text>(find.text('Shutter fault: open or close again.'))
          .style
          ?.color,
      AraColors.errorText,
    );
    expect(bottom(), still, reason: 'the window keeps its size');
  });

  testWidgets(
    'after a shutter fault both segments take a tap, so it can be cleared',
    (tester) async {
      // A driver clears a faulted shutter on the next open or close; Closed
      // read as selected and took no tap, so only Open was left.
      final (_, dome) = await _pump(tester, _wire(runtime: 'error'));
      expect(find.text('Shutter fault: open or close again.'), findsOneWidget);
      expect(
        tester.widget<SimpleSegment>(_key('shutter-closed')).selected,
        isFalse,
      );
      await tester.tap(_key('shutter-closed'));
      await tester.pump();
      expect(dome.calls, ['close']);
      await tester.pump(const Duration(seconds: 4)); // the ask's timer
    },
  );

  testWidgets('what was asked shows at once, until the dome is done', (
    tester,
  ) async {
    // A fast dome (or one already there) moves between two reads: the ask
    // says Parking… / Finding home… / Turning to … from the tap, as Mount's
    // Park does.
    final (c, dome) = await _pump(tester, _wire());
    await tester.tap(_key('park'));
    await tester.pump();
    expect(find.text('Parking…'), findsOneWidget);
    dome.set(_wire(runtime: 'slewing'));
    await tester.pump();
    expect(find.text('Parking…'), findsOneWidget);
    dome.set(_wire(runtime: 'parked', parked: true));
    await tester.pump();
    expect(find.text('Parking…'), findsNothing);
    expect(c.read(simpleDomeAskProvider), isNull);
    await tester.tap(_key('home'));
    await tester.pump();
    expect(find.text('Finding home…'), findsOneWidget);
    // Already home: nothing moves, so the ask goes after a few seconds.
    await tester.pump(const Duration(seconds: 4));
    expect(find.text('Finding home…'), findsNothing);
  });

  testWidgets('a turn reads Turning to 90°…', (tester) async {
    await _pump(tester, _wire());
    final dial = tester.getRect(_key('dial'));
    await tester.tapAt(Offset(dial.right - 8, dial.center.dy));
    await tester.pump();
    await tester.tap(_key('hub'));
    await tester.pump();
    expect(find.text('Turning to 90°…'), findsOneWidget);
    await tester.pump(const Duration(seconds: 4));
  });

  testWidgets('the shutter segments sit clear of the drawing', (tester) async {
    await _pump(tester, _wire());
    final gap =
        tester
            .getTopLeft(find.byKey(const Key('simple-window-row-dome-shutter')))
            .dy -
        tester.getBottomLeft(_key('front')).dy;
    expect(gap, greaterThanOrEqualTo(12));
  });

  testWidgets('a narrow window drops the drawing, keeping the dial', (
    tester,
  ) async {
    await _pump(tester, _wire(), width: 200);
    expect(tester.takeException(), isNull);
    expect(_key('front'), findsNothing);
    expect(_key('dial'), findsOneWidget);
    expect(find.text('212°'), findsOneWidget);
  });

  testWidgets('a failed read shows no state', (tester) async {
    await _pump(tester, _wire(state: 'error'));
    expect(find.text('Dome read failed — check the device.'), findsOneWidget);
    expect(_key('shutter-open'), findsNothing);
    expect(find.text('212°'), findsNothing);
  });

  testWidgets('a dome that isn\'t connected shows its state and no controls', (
    tester,
  ) async {
    await _pump(tester, _wire(state: 'disconnected'));
    expect(find.text('Not connected'), findsOneWidget);
    expect(_key('shutter-open'), findsNothing);
    expect(_key('front'), findsNothing);
  });

  testWidgets('in pt-BR', (tester) async {
    await _pump(
      tester,
      _wire(
        rotates: false,
        canPark: false,
        canHome: false,
        runtime: 'shutter_moving',
      ),
      locale: const Locale('pt', 'BR'),
    );
    expect(find.text('Janela'), findsOneWidget);
    expect(find.text('Janela em movimento…'), findsOneWidget);
    expect(find.text('Parar'), findsOneWidget);
  });

  testWidgets(
    'in Settings it ends with the dome\'s versions; from the dot, none',
    (tester) async {
      const driver = DeviceDriver(driverVersion: '5.1.0');
      await _pump(tester, _wire(), driver: driver);
      expect(find.text('Versions'), findsNothing);
      await _pump(tester, _wire(), driver: driver, inSettings: true);
      await tester.pump();
      expect(find.text('5.1.0'), findsOneWidget);
    },
  );
}
