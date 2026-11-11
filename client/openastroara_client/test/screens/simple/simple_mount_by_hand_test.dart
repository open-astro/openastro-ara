import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations_en.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/plate_solve_result.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/models/verify_position_result.dart';
import 'package:openastroara/screens/simple/simple_device_windows.dart';
import 'package:openastroara/screens/simple/simple_mount_controls.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleStatusPill, SimpleStopSquare;
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/services/center_api.dart';
import 'package:openastroara/services/plate_solve_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/plate_solve/plate_solve_api_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/util/slew_rates.dart';
import 'package:openastroara/widgets/status_indicator.dart' show StatusBlink;

/// #1649 / #1650 — Simple Mode's Mount window moves the mount by hand and
/// checks where it points. Fakes record what the rig is asked.
class _Mount extends MountNotifier {
  _Mount(this.json);
  Map<String, dynamic> json;
  final calls = <String>[];
  Object? refuseMoves;

  @override
  Future<MountStatus?> build() async => MountStatus.fromJson(json);

  void set(Map<String, dynamic> next) => state = AsyncData(MountStatus.fromJson(json = next));

  @override
  Future<bool> moveAxis({required int axis, required double rate, int? leaseMs}) async {
    calls.add('move $axis $rate${leaseMs == null ? '' : ' lease $leaseMs'}');
    if (rate != 0 && refuseMoves != null) throw refuseMoves!;
    return true;
  }

  int refreshes = 0;

  @override
  Future<void> refresh([client]) async => refreshes++;

  @override
  Future<bool> abortSlew() async {
    calls.add('abort');
    return true;
  }

  @override
  Future<bool> park() async {
    calls.add('park');
    return true;
  }

  @override
  Future<bool> findHome() async {
    calls.add('home');
    return true;
  }

  @override
  Future<bool> setTrackingRate(String rate) async {
    calls.add('rate $rate');
    return true;
  }

  @override
  Future<bool> slewTo(double raHours, double decDegrees, {bool sync = false, bool force = false}) async {
    calls.add('${sync ? 'sync' : 'slew'} ${raHours.toStringAsFixed(3)} ${decDegrees.toStringAsFixed(3)}');
    return true;
  }
}

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus(
    deviceId: 'cam',
    name: 'ATR2600M',
    connectionState: EquipmentConnectionState.connected,
    capabilities: null,
    runtimeState: 'idle',
    ccdTemperature: null,
    coolerPowerPct: null,
    coolerOn: false,
    exposureProgressPct: null,
  );
}

class _Imaging extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async =>
      const SequenceRunStateInfo(sequenceId: 'seq-1', state: SequenceRunState.running);
}

class _Run extends SimpleRunNotifier {
  @override
  SimpleRun build() => const SimpleRun(
    targets: [SimpleTarget(name: 'M 31', raDeg: 10.6847, decDeg: 41.2692)],
  );
}

class _Solver implements PlateSolveApi {
  VerifyPositionResult? answer;
  Object? error;
  int checks = 0;

  /// Held open: the check stays under way until it completes.
  Completer<void>? hold;

  @override
  Future<VerifyPositionResult> verifyPosition({double? exposureSeconds}) async {
    checks++;
    await hold?.future;
    if (error != null) throw error!;
    return answer!;
  }

  @override
  Future<PlateSolveResult> solve(String id) => throw UnimplementedError();
}

class _Centre implements CenterClient {
  final calls = <String>[];
  String jobState = 'running';

  @override
  Future<AutofocusJob> start(double raHoursJ2000, double decDegJ2000) async {
    calls.add('start ${raHoursJ2000.toStringAsFixed(3)} ${decDegJ2000.toStringAsFixed(3)}');
    return const AutofocusJob(jobId: 'j1', state: 'running');
  }

  @override
  Future<AutofocusJob?> job(String jobId) async => AutofocusJob(jobId: jobId, state: jobState);

  @override
  Future<void> cancel(String jobId) async => calls.add('cancel');

  @override
  void close() {}
}

Map<String, dynamic> _wire({
  bool parked = false,
  bool atHome = false,
  String runtime = 'tracking',
  bool home = true,
  bool rates = true,
  String? trackingRate = 'Sidereal',
}) => {
  'device_id': 'm',
  'name': 'EQ6-R',
  'state': 'connected',
  'capabilities': {
    'can_park': true,
    'can_unpark': true,
    'can_set_tracking': true,
    'can_slew': true,
    'can_sync': true,
    'can_find_home': home,
    'can_move_axis': rates,
    'move_axis_rate_bands_deg_per_sec': rates
        ? [
            {'min': 0.002, 'max': 4.0},
          ]
        : [],
    'supported_sidereal_rates': ['Sidereal', 'Lunar', 'Solar'],
  },
  'runtime': {
    'state': runtime,
    'tracking': !parked,
    'parked': parked,
    'at_home': atHome,
    'right_ascension_hours': 0.712,
    'declination_degrees': 41.27,
    'tracking_rate': ?trackingRate,
  },
};

const _medium = 1.0; // 25 % of 4 deg/s

Future<(ProviderContainer, _Mount, _Solver, _Centre)> _pump(
  WidgetTester tester,
  Map<String, dynamic> wire, {
  double width = 640,
  bool imaging = false,
  double textScale = 1,
  bool withPill = false,
}) async {
  await tester.pumpWidget(const SizedBox());
  final mount = _Mount(wire);
  final solver = _Solver();
  final centre = _Centre();
  final c = ProviderContainer(
    overrides: [
      mountProvider.overrideWith(() => mount),
      cameraStatusProvider.overrideWith(_Camera.new),
      plateSolveApiProvider.overrideWithValue(solver),
      centerApiProvider.overrideWithValue(centre),
      if (imaging) sequenceRunStateProvider.overrideWith(_Imaging.new),
      simpleModeActiveProvider.overrideWithValue(true),
      simpleRunProvider.overrideWith(_Run.new),
    ],
  );
  addTearDown(c.dispose);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(textScale)),
          child: child!,
        ),
        home: Scaffold(
          body: SingleChildScrollView(
            child: Align(
              alignment: Alignment.topLeft,
              child: withPill
                  ? Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        const SimpleStatusPill(),
                        SizedBox(width: width, child: const SimpleMountWindow()),
                      ],
                    )
                  : SizedBox(width: width, child: const SimpleMountWindow()),
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pump();
  await tester.pump();
  return (c, mount, solver, centre);
}

Future<void> _hold(WidgetTester tester, String dir, Future<void> Function() whileHeld) async {
  final g = await tester.startGesture(tester.getCenter(find.byKey(Key('simple-mount-pad-$dir'))));
  await tester.pump();
  await whileHeld();
  await g.up();
  await tester.pump();
}

void main() {
  final l10n = AppLocalizationsEn();

  group('pure', () {
    test('pad speeds: Slow / Medium / Fast are 5, 25 and 100 % of the top speed', () {
      expect(simplePadSpeeds(const [SlewRateBand(0.002, 4.0)]), [('slow', 0.2), ('medium', 1.0), ('fast', 4.0)]);
    });

    test('pad speeds inside the bands of a mount with a gap (OmniSim: 0–6.7 and 10–20 deg/s)', () {
      expect(simplePadSpeeds(const [SlewRateBand(0, 6.666666666666667), SlewRateBand(10, 20)]),
          [('slow', 1.0), ('medium', 5.0), ('fast', 20.0)]);
    });

    test('a discrete-rate mount gets its nearest steps, fewer names when it has fewer', () {
      expect(simplePadSpeeds(const [SlewRateBand(0.5, 0.5), SlewRateBand(4.0, 4.0)]), [('slow', 0.5), ('fast', 4.0)]);
      expect(simplePadSpeeds(const [SlewRateBand(2.0, 2.0)]), [('fast', 2.0)]);
      expect(simplePadSpeeds(const []), isEmpty);
    });

    test('the pad blocker: parked, then the plan, polar alignment, guiding, a GoTo', () {
      MountStatus m({bool parked = false, String runtime = 'tracking'}) =>
          MountStatus.fromJson(_wire(parked: parked, runtime: runtime));
      expect(simplePadBlocker(l10n, m(parked: true), planImaging: true, polarAligning: true, guiding: true),
          'Unpark to move the mount');
      expect(simplePadBlocker(l10n, m(), planImaging: true, polarAligning: true, guiding: true),
          'Pause the plan to move the mount');
      expect(simplePadBlocker(l10n, m(), planImaging: false, polarAligning: true, guiding: true),
          'Polar alignment is running');
      expect(simplePadBlocker(l10n, m(), planImaging: false, polarAligning: false, guiding: true),
          'Stop guiding to move the mount');
      expect(simplePadBlocker(l10n, m(runtime: 'slewing'), planImaging: false, polarAligning: false, guiding: false),
          'Moving to a target: Stop ends it');
      expect(simplePadBlocker(l10n, m(), planImaging: false, polarAligning: false, guiding: false), isNull);
      expect(
          simplePadBlocker(l10n, m(runtime: 'slewing'),
              planImaging: false, polarAligning: false, guiding: false, padMoving: true),
          isNull,
          reason: 'the pad\'s own MoveAxis reads as slewing (ASCOM Slewing is true during MoveAxis)');
    });

    test('separations read in seconds, minutes or degrees, rounding at the edges', () {
      expect(simpleSeparation(0.5), '30″');
      expect(simpleSeparation(0.999), '1.0′', reason: '59.9″ rounds to a minute, never 60″');
      expect(simpleSeparation(2.44), '2.4′');
      expect(simpleSeparation(59.95), '1.0°');
      expect(simpleSeparation(150), '2.5°');
    });

    test('the Target is the queued one within 1′ of where the mount was sent', () {
      const m31 = SimpleTarget(name: 'M 31', raDeg: 10.6847, decDeg: 41.2692);
      expect(simplePointingTargetName([m31], 10.6847, 41.2692 + 0.5 / 60), 'M 31');
      expect(simplePointingTargetName([m31], 10.6847, 41.2692 + 1.5 / 60), isNull);
      expect(simplePointingTargetName([m31], 10.6847 + 360 - 360, 41.2692), 'M 31');
    });
  });

  group('the direction pad', () {
    testWidgets('holding N moves the secondary axis on a lease, renewed while held; letting go stops it',
        (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      await _hold(tester, 'n', () async {
        expect(mount.calls, ['move 1 $_medium lease 1000']);
        await tester.pump(const Duration(milliseconds: 650));
        expect(mount.calls, ['move 1 $_medium lease 1000', 'move 1 $_medium lease 1000', 'move 1 $_medium lease 1000']);
      });
      expect(mount.calls.last, 'move 1 0.0');
      expect(mount.refreshes, greaterThanOrEqualTo(4), reason: 'the position follows the move');
      mount.calls.clear();
      await tester.pump(const Duration(seconds: 1));
      expect(mount.calls, isEmpty, reason: 'no renewals after letting go');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('the mount reporting the pad move as slewing never stops the press', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-mount-pad-n'))));
      await tester.pump();
      mount.set(_wire(runtime: 'slewing')); // ASCOM: Slewing is true while MoveAxis runs
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 650));
      expect(mount.calls.where((c) => c == 'move 1 0.0'), isEmpty, reason: 'still held');
      expect(find.text('Moving to a target: Stop ends it'), findsNothing);
      await g.up();
      await tester.pump();
      expect(mount.calls.last, 'move 1 0.0');
      expect(find.text('Moving to a target: Stop ends it'), findsNothing, reason: 'the mount is still slowing down');
      await tester.pump(const Duration(seconds: 4));
      expect(find.text('Moving to a target: Stop ends it'), findsOneWidget, reason: 'still slewing long after: a GoTo');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('a diagonal moves both axes, and E/W use the full UI pad signs', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      await _hold(tester, 'nw', () async {});
      expect(mount.calls, ['move 1 $_medium lease 1000', 'move 0 -$_medium lease 1000', 'move 1 0.0', 'move 0 0.0']);
      mount.calls.clear();
      await _hold(tester, 'e', () async {});
      expect(mount.calls, ['move 0 $_medium lease 1000', 'move 0 0.0']);
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('Slow and Fast pick the other speeds', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      await tester.tap(find.byKey(const Key('simple-mount-speed-fast')));
      await tester.pump();
      await _hold(tester, 's', () async {});
      expect(mount.calls.first, 'move 1 -4.0 lease 1000');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('the centre is Home, and the red Stop square aborts every axis while the mount moves on its own',
        (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      expect(find.byKey(const Key('simple-mount-pad-home')), findsOneWidget);
      expect(find.byKey(const Key('simple-mount-pad-stop')), findsNothing);
      mount.set(_wire(runtime: 'slewing')); // a GoTo from elsewhere
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-pad-home')), findsNothing);
      expect(
        find.descendant(of: find.byKey(const Key('simple-mount-pad-stop')), matching: find.byType(SimpleStopSquare)),
        findsOneWidget,
        reason: 'the red square, not the word',
      );
      expect(find.descendant(of: find.byKey(const Key('simple-mount-pad-stop')), matching: find.text('Stop')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-mount-pad-stop')));
      await tester.pump();
      expect(mount.calls, ['abort']);
    });

    testWidgets('leaving the app mid-press stops the move', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-mount-pad-ne'))));
      await tester.pump();
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
      await tester.pump();
      expect(mount.calls.skip(2), ['move 1 0.0', 'move 0 0.0']);
      mount.calls.clear();
      await tester.pump(const Duration(seconds: 1));
      expect(mount.calls, isEmpty);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await g.up();
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('closing the window mid-press stops the move', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-mount-pad-w'))));
      await tester.pump();
      await tester.pumpWidget(const SizedBox());
      expect(mount.calls.last, 'move 0 0.0');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('closing the window mid-press lets the pad-moving flag settle', (tester) async {
      final (c, _, _, _) = await _pump(tester, _wire());
      await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-mount-pad-w'))));
      await tester.pump();
      expect(c.read(simplePadMovingProvider), isTrue);
      await tester.pumpWidget(UncontrolledProviderScope(container: c, child: const SizedBox()));
      await tester.pump(const Duration(seconds: 4));
      expect(c.read(simplePadMovingProvider), isFalse, reason: 'a later GoTo must still block the pad and show Stop');
    });

    testWidgets('Escape during a held arrow lets the pad-moving flag settle', (tester) async {
      final (c, _, _, _) = await _pump(tester, _wire(home: false));
      await tester.tap(find.byKey(const Key('simple-mount-pad-stop')));
      await tester.pump();
      await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      expect(c.read(simplePadMovingProvider), isTrue);
      await tester.sendKeyDownEvent(LogicalKeyboardKey.escape);
      await tester.pump();
      await tester.sendKeyUpEvent(LogicalKeyboardKey.escape);
      await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump(const Duration(seconds: 4));
      expect(c.read(simplePadMovingProvider), isFalse, reason: 'a later GoTo must still block the pad and show Stop');
    });

    testWidgets('while the plan images the pad says why and sends nothing', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire(), imaging: true);
      expect(find.text('Pause the plan to move the mount'), findsOneWidget);
      expect(find.byKey(const Key('simple-mount-speed-medium')), findsNothing, reason: 'the reason takes its place');
      await _hold(tester, 'n', () async {});
      expect(mount.calls, isEmpty);
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('parked, the pad says Unpark to move the mount', (tester) async {
      await _pump(tester, _wire(parked: true, runtime: 'parked'));
      expect(find.text('Unpark to move the mount'), findsOneWidget);
    });

    testWidgets('a refused start says why once per press, and stops renewing', (tester) async {
      final (c, mount, _, _) = await _pump(tester, _wire());
      mount.refuseMoves = StateError('Mount reports no MoveAxis rate');
      await _hold(tester, 'ne', () async {
        await tester.pump(const Duration(seconds: 1));
      });
      expect(c.read(simpleMessagesProvider), hasLength(1));
      expect(c.read(simpleMessagesProvider).single.title, "Couldn't move the mount");
      expect(mount.calls.where((s) => s.contains('lease')), hasLength(2), reason: 'no renewals after the refusal');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('a mount with no manual rates says so instead of a dead pad', (tester) async {
      await _pump(tester, _wire(rates: false));
      expect(find.text("This mount can't be moved by hand"), findsOneWidget);
      expect(find.byKey(const Key('simple-mount-pad-n')), findsNothing);
    });

    testWidgets('the arrow keys drive N/S/E/W while the pad has focus; Escape stops', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire(home: false));
      await tester.tap(find.byKey(const Key('simple-mount-pad-stop')));
      await tester.pump();
      mount.calls.clear();
      await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowUp);
      await tester.pump();
      expect(mount.calls, ['move 1 $_medium lease 1000', 'move 1 0.0']);
      mount.calls.clear();
      await tester.sendKeyDownEvent(LogicalKeyboardKey.escape);
      await tester.pump();
      expect(mount.calls, ['abort']);
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('screen readers hear each direction', (tester) async {
      final handle = tester.ensureSemantics();
      await _pump(tester, _wire());
      expect(find.bySemanticsLabel('Move north'), findsOneWidget);
      expect(find.bySemanticsLabel('Move south-west'), findsOneWidget);
      expect(find.bySemanticsLabel('Home'), findsOneWidget);
      await _pump(tester, _wire(home: false));
      expect(find.bySemanticsLabel('Stop the mount'), findsOneWidget);
      handle.dispose();
    });

    testWidgets('wide, the pad bottom is level with Check; narrow, it sits under the rows', (tester) async {
      await _pump(tester, _wire());
      final pad = tester.getRect(find.byKey(const Key('simple-mount-pad-s')));
      final check = tester.getRect(find.byKey(const Key('simple-mount-check')));
      expect(pad.bottom, closeTo(check.bottom, 1));
      expect(pad.left, greaterThan(check.right));
      await _pump(tester, _wire(), width: 300);
      final narrowPad = tester.getRect(find.byKey(const Key('simple-mount-pad-s')));
      final flip = tester.getRect(find.byKey(const Key('simple-window-row-Meridian flip')));
      expect(narrowPad.top, greaterThan(flip.bottom));
    });
  });

  group('audit of #1785', () {
    testWidgets('the pad shows a focus ring after an arrow key, never after a click', (tester) async {
      await _pump(tester, _wire(home: false));
      await tester.tap(find.byKey(const Key('simple-mount-pad-stop')));
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-pad-focus')), findsNothing, reason: 'a click draws no ring');
      await tester.sendKeyDownEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pump();
      await tester.sendKeyUpEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-pad-focus')), findsOneWidget, reason: 'keyboard use shows it');
      await tester.tap(find.byKey(const Key('simple-mount-pad-stop')));
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-pad-focus')), findsNothing, reason: 'the next pointer hides it');
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('Finding home… and Taking a frame and solving… blink', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      await tester.tap(find.byKey(const Key('simple-mount-pad-home')));
      await tester.pump();
      expect(find.ancestor(of: find.text('Finding home…'), matching: find.byType(StatusBlink)), findsOneWidget);
      await tester.pump(const Duration(seconds: 4));
      solver.answer = const VerifyPositionResult(success: false, exposureSeconds: 2);
      solver.hold = Completer<void>();
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      expect(find.ancestor(of: find.text('Taking a frame and solving…'), matching: find.byType(StatusBlink)), findsOneWidget);
      solver.hold!.complete();
      await tester.pump();
    });

    testWidgets('the Check answer is a live region for screen readers', (tester) async {
      final handle = tester.ensureSemantics();
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.answer = const VerifyPositionResult(success: false, exposureSeconds: 2);
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      final live = find.ancestor(
        of: find.text("Couldn't solve this frame"),
        matching: find.byWidgetPredicate((w) => w is Semantics && (w.properties.liveRegion ?? false)),
      );
      expect(live, findsOneWidget);
      handle.dispose();
    });

    testWidgets('pad cells grow with the text size', (tester) async {
      await _pump(tester, _wire());
      final normal = tester.getSize(find.byKey(const Key('simple-mount-pad-n'))).width;
      await _pump(tester, _wire(), textScale: 2, width: 1400);
      final large = tester.getSize(find.byKey(const Key('simple-mount-pad-n'))).width;
      expect(large, greaterThan(normal));
      expect(tester.takeException(), isNull, reason: 'Stop fits its cell at 2x');
    });

    testWidgets('a 409 offers the camera settings whatever its words', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.error = const PlateSolveException('Busy right now.', statusCode: 409);
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Camera settings'), findsOneWidget);
    });
  });

  group('Position and Tracking speed', () {
    testWidgets('Home is the pad centre, for a mount that can find home; it sends the mount home', (tester) async {
      final (c, mount, _, _) = await _pump(tester, _wire());
      expect(find.text('Position'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-mount-pad-home')));
      await tester.pump();
      expect(mount.calls, ['home']);
      expect(find.text('Finding home…'), findsOneWidget, reason: 'from the tap');
      mount.set(_wire(runtime: 'slewing'));
      await tester.pump();
      mount.set(_wire(atHome: true, runtime: 'idle'));
      await tester.pump();
      expect(find.text('Finding home…'), findsNothing);
      expect(find.text('Position · At home'), findsOneWidget);
      expect(find.byKey(const Key('simple-mount-park-home')), findsNothing, reason: 'Parked / Unparked only');
      expect(c.read(simpleMountHomeProvider), isFalse);
    });

    testWidgets('Parking… blinks green, as Finding home… does', (tester) async {
      await _pump(tester, _wire());
      await tester.tap(find.byKey(const Key('simple-mount-park-parked')));
      await tester.pump();
      final blink = find.ancestor(of: find.text('Parking…'), matching: find.byType(StatusBlink));
      expect(blink, findsOneWidget);
      expect(tester.widget<StatusBlink>(blink).on, isTrue);
      expect(tester.widget<Text>(find.text('Parking…')).style?.color, AraColors.accentConnected);
      await tester.pump(const Duration(seconds: 4));
    });

    testWidgets('H sends the mount home from the keyboard', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      await tester.tap(find.byKey(const Key('simple-mount-pad-n'))); // focus the pad
      await tester.pump();
      mount.calls.clear();
      await tester.sendKeyEvent(LogicalKeyboardKey.keyH);
      await tester.pump();
      expect(mount.calls, ['home']);
      await tester.pump(const Duration(seconds: 4));
    });

    testWidgets('Home starts on letting go, not on touch, and a slide off cancels it', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      expect(find.byTooltip('Home'), findsOneWidget, reason: 'the house has a name');
      final home = tester.getCenter(find.byKey(const Key('simple-mount-pad-home')));
      final brush = await tester.startGesture(home);
      await tester.pump();
      expect(mount.calls, isEmpty, reason: 'a touch alone sends nothing');
      await brush.cancel();
      await tester.pump();
      expect(mount.calls, isEmpty, reason: 'a cancelled touch sends nothing');
      final g = await tester.startGesture(home);
      await g.up();
      await tester.pump();
      expect(mount.calls, ['home']);
      await tester.pump(const Duration(seconds: 4));
    });

    testWidgets('a touch that slides off Home and lifts sends nothing', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      final g = await tester.startGesture(tester.getCenter(find.byKey(const Key('simple-mount-pad-home'))));
      await tester.pump();
      await g.moveBy(const Offset(0, 120)); // off the cell, as a scroll swipe would
      await tester.pump();
      await g.up();
      await tester.pump();
      expect(mount.calls, isEmpty);
    });

    testWidgets('screen readers hear Home once', (tester) async {
      final handle = tester.ensureSemantics();
      await _pump(tester, _wire());
      expect(find.bySemanticsLabel(RegExp(r'^Home$')), findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'Home\nHome|Home Home')), findsNothing);
      // The tooltip wraps the cell: no node may carry Home as its label and its tooltip.
      final both = find.semantics.byPredicate((n) => n.label == 'Home' && n.tooltip == 'Home');
      final tipOnly = find.semantics.byPredicate((n) => n.tooltip == 'Home');
      expect(both, findsNothing);
      expect(tipOnly, findsNothing, reason: 'the house is named once, by its label');
      handle.dispose();
    });

    testWidgets('the Stop square grows with the pad cell', (tester) async {
      await _pump(tester, _wire(home: false));
      // getRect: the square is scaled by a FittedBox, so its drawn size is what counts.
      final small = tester.getRect(find.byKey(const Key('simple-stop-square'))).width;
      await _pump(tester, _wire(home: false), textScale: 2, width: 1400);
      expect(tester.getRect(find.byKey(const Key('simple-stop-square'))).width, greaterThan(small));
    });

    testWidgets('a Home that moves nothing clears after a few seconds', (tester) async {
      final (c, _, _, _) = await _pump(tester, _wire());
      await tester.tap(find.byKey(const Key('simple-mount-pad-home')));
      await tester.pump();
      await tester.pump(const Duration(seconds: 4));
      expect(c.read(simpleMountHomeProvider), isFalse);
    });

    testWidgets('no Home without the capability; Home takes no tap while parked', (tester) async {
      await _pump(tester, _wire(home: false));
      expect(find.byKey(const Key('simple-mount-pad-home')), findsNothing);
      expect(find.byKey(const Key('simple-mount-pad-stop')), findsOneWidget, reason: 'no Home: the centre is Stop');
      final (_, c2, _, _) = await _pump(tester, _wire(parked: true, runtime: 'parked'));
      await tester.tap(find.byKey(const Key('simple-mount-pad-home')));
      await tester.pump();
      expect(c2.calls, isEmpty, reason: 'parked: Home takes no tap');
    });

    testWidgets('while a plan runs, Home asks first', (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire(), imaging: true);
      await tester.tap(find.byKey(const Key('simple-mount-pad-home')));
      await tester.pump();
      expect(mount.calls, isEmpty);
      await tester.tap(find.text('Home anyway'));
      await tester.pump();
      expect(mount.calls, ['home']);
      await tester.pump(const Duration(seconds: 4)); // nothing moved: the ask clears
    });

    testWidgets('Tracking speed lists the mount rates and sets the one picked; off sidereal says so',
        (tester) async {
      final (_, mount, _, _) = await _pump(tester, _wire());
      expect(find.byKey(const Key('simple-mount-rate-king')), findsNothing, reason: 'not offered');
      expect(find.byKey(const Key('simple-mount-rate-caption')), findsNothing);
      await tester.tap(find.byKey(const Key('simple-mount-rate-lunar')));
      await tester.pump();
      expect(mount.calls, ['rate Lunar']);
      mount.set(_wire(trackingRate: 'Lunar'));
      await tester.pump();
      expect(find.text('Tracking at the lunar rate. Back to Sidereal for stars and galaxies.'), findsOneWidget);
    });
  });

  group('Pointing', () {
    const solved = VerifyPositionResult(
      success: true,
      solvedRaHours: 0.7123,
      solvedDecDegrees: 41.25,
      mountRaHours: 0.712,
      mountDecDegrees: 41.27,
      intendedRaHours: 10.6847 / 15,
      intendedDecDegrees: 41.2692,
      solvedToMountArcmin: 0.8,
      solvedToIntendedArcmin: 2.4,
      exposureSeconds: 2,
      mountEpoch: 'J2000',
      rotatorSyncedDegrees: 87.4,
    );

    testWidgets('Check shows how far from the Target, with Sync and Centre, and the rotator synced', (tester) async {
      final (_, mount, solver, centre) = await _pump(tester, _wire());
      solver.answer = solved;
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Pointing (solved)'), findsOneWidget);
      expect(find.text('2.4′ from M 31'), findsOneWidget);
      expect(find.text('Mount reports 48″ off · Rotator synced to 87.4°'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-mount-sync')));
      await tester.pump();
      expect(mount.calls, ['sync 0.712 41.250']);
      expect(find.text('Synced'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-mount-centre')));
      await tester.pump();
      expect(centre.calls, ['start 0.712 41.269']);
      expect(find.text('Centering…'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-mount-centre-cancel')));
      await tester.pump();
      expect(centre.calls.last, 'cancel');
      centre.jobState = 'complete';
      await tester.pump(const Duration(seconds: 1));
      await tester.pump();
      expect(solver.checks, 2, reason: 'a finished centring checks again');
      await tester.pump(const Duration(seconds: 12)); // the Synced pill fades
    });

    testWidgets('a move after Check drops the answer, so Sync never writes an old solve', (tester) async {
      final (_, mount, solver, _) = await _pump(tester, _wire());
      solver.answer = solved;
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-sync')), findsOneWidget);
      await _hold(tester, 'n', () async {});
      expect(find.byKey(const Key('simple-mount-sync')), findsNothing, reason: 'a pad nudge moved the mount');
      expect(find.text('Pointing'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-sync')), findsOneWidget);
      mount.set(_wire(runtime: 'slewing')); // a GoTo from elsewhere
      await tester.pump();
      mount.set(_wire());
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-sync')), findsNothing, reason: 'a slew moved the mount');
      expect(mount.calls.where((c) => c.startsWith('sync')), isEmpty);
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('a nudge during Check drops its answer too', (tester) async {
      final (_, mount, solver, _) = await _pump(tester, _wire());
      solver.answer = solved;
      solver.hold = Completer<void>();
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await _hold(tester, 'n', () async {});
      solver.hold!.complete();
      await tester.pump();
      await tester.pump();
      expect(find.byKey(const Key('simple-mount-sync')), findsNothing);
      expect(find.text('Pointing'), findsOneWidget);
      await tester.pump(const Duration(seconds: 4));
      expect(mount.calls.where((c) => c.startsWith('sync')), isEmpty);
      await tester.pump(const Duration(seconds: 4)); // the pad's "still moving" settle
    });

    testWidgets('no Target matched reads from the last go-to; within 1′ is On target with no Center', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.answer = const VerifyPositionResult(
        success: true,
        solvedRaHours: 1,
        solvedDecDegrees: 2,
        intendedRaHours: 1,
        intendedDecDegrees: 2,
        solvedToIntendedArcmin: 0.5,
        exposureSeconds: 2,
      );
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text('30″ from the last go-to'), findsOneWidget);
      expect(find.text('On target'), findsOneWidget);
      expect(find.byKey(const Key('simple-mount-centre')), findsNothing);
    });

    testWidgets('no Sync while the plan images (the camera is busy too)', (tester) async {
      await _pump(tester, _wire(), imaging: true);
      expect(find.text('The main camera is busy: wait for its frame, or stop it.'), findsOneWidget);
      expect(tester.widget<SimpleWindowButton>(find.byKey(const Key('simple-mount-check'))).onTap, isNull);
    });

    testWidgets('a Check under way has a status pill, as any frame of the main camera does', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire(), withPill: true);
      solver.answer = const VerifyPositionResult(success: false, exposureSeconds: 2);
      solver.hold = Completer<void>();
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      final pill = find.byType(SimpleStatusPill);
      expect(find.descendant(of: pill, matching: find.textContaining('Taking a frame and solving')), findsOneWidget);
      solver.hold!.complete();
      await tester.pump();
      for (var i = 0; i < 30; i++) {
        await tester.pump(const Duration(milliseconds: 100)); // the pill keeps its minimum, then fades
      }
      expect(find.descendant(of: pill, matching: find.textContaining('Taking a frame and solving')), findsNothing);
    });

    testWidgets('a Check that fails after the window closed says so in a message pill', (tester) async {
      final (c, _, solver, _) = await _pump(tester, _wire());
      solver.error = const PlateSolveException('No star database is installed for ASTAP.', statusCode: 422);
      solver.hold = Completer<void>();
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pumpWidget(UncontrolledProviderScope(container: c, child: const SizedBox()));
      await tester.pump();
      solver.hold!.complete();
      await tester.pump();
      await tester.pump();
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.kind, SimpleMessageKind.error);
      expect(pill.detail, 'No star database is installed for ASTAP.');
      await tester.pump(const Duration(seconds: 25));
    });

    testWidgets('the progress words are not a live region while the pill says them', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.answer = const VerifyPositionResult(success: false, exposureSeconds: 2);
      solver.hold = Completer<void>();
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      final live = find.ancestor(
        of: find.text('Taking a frame and solving…'),
        matching: find.byWidgetPredicate((w) => w is Semantics && (w.properties.liveRegion ?? false)),
      );
      expect(live, findsNothing);
      solver.hold!.complete();
      await tester.pump();
    });

    testWidgets('a failed solve says so with the note', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.answer = const VerifyPositionResult(success: false, exposureSeconds: 2, note: 'Too few stars.');
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text("Couldn't solve this frame"), findsOneWidget);
      expect(tester.widget<Text>(find.text("Couldn't solve this frame")).style?.color, AraColors.accentWarning,
          reason: 'amber: the rig works, this frame did not solve');
      expect(find.text('Too few stars.'), findsOneWidget);
    });

    testWidgets('a solver that is not set up says why and offers its settings', (tester) async {
      final (_, _, solver, _) = await _pump(tester, _wire());
      solver.error = const PlateSolveException('No star database is installed for ASTAP.');
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text('No star database is installed for ASTAP.'), findsOneWidget);
      expect(find.text('Plate solving settings'), findsOneWidget);
      solver.error = const PlateSolveException('Set the focal length and pixel size first.');
      await tester.tap(find.byKey(const Key('simple-mount-check')));
      await tester.pump();
      await tester.pump();
      expect(find.text('Optics settings'), findsOneWidget);
    });
  });
}
