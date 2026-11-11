import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration/calibration_models.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/flat_panel_status.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:openastroara/screens/simple/simple_calibration.dart';
import 'package:openastroara/services/calibration_api.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/state/calibration/calibration_state.dart';
import 'package:openastroara/state/equipment/flat_panel_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/sequence/imaging_run_body.dart';
import 'package:openastroara/models/sequence/instruction_catalog.dart';
import 'package:openastroara/models/sequence/nina_dom.dart';
import 'package:openastroara/models/sequence/condition_catalog.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleShutterButton;
import 'package:openastroara/state/simple_calibration_state.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/simple_run_state.dart';

/// The rig's calibration routes: its sessions, and the matching-flats
/// requests sent.
class _Calibration implements CalibrationClient {
  _Calibration(this.sessions);
  final List<CalibrationSession> sessions;
  final requests = <Map<String, Object?>>[];
  bool fails = false;

  @override
  Future<CursorPage<CalibrationSession>> listSessions({int limit = 200, String? cursor}) async =>
      CursorPage(items: sessions, nextCursor: null, hasMore: false);

  @override
  Future<GeneratedFlatSequence> generateMatchingFlats(
    String sessionId, {
    int? frameCount,
    int? targetAdu,
    bool generateOnly = false,
    bool? coolCamera,
    double? coolToTemperatureC,
    bool? parkMountAfter,
    bool includeCoveredFilters = false,
    bool includeBias = false,
    bool includeDarks = false,
    bool pauseForLensCap = false,
    bool includeFlats = true,
    String flavor = 'panel',
  }) async {
    if (fails) {
      throw DioException(
        requestOptions: RequestOptions(),
        response: Response(requestOptions: RequestOptions(), statusCode: 409, data: {'detail': 'Camera is not connected'}),
        type: DioExceptionType.badResponse,
      );
    }
    requests.add({
      'session': sessionId,
      'flats': includeFlats,
      'darks': includeDarks,
      'bias': includeBias,
      'lensCap': pauseForLensCap,
      'flavor': flavor,
    });
    return const GeneratedFlatSequence(
      generatedSequenceId: 'cal-run',
      generatedSequenceName: 'Flats and darks — NGC 7000',
      totalFlatFrames: 75,
      totalBiasFrames: 30,
      totalDarkFrames: 100,
    );
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// The sequence routes: start and stop recorded.
class _Seq implements SequenceClient {
  final calls = <String>[];
  @override
  Future<String> start(String id) async {
    calls.add('start $id');
    return id;
  }

  @override
  Future<String> stop(String id) async {
    calls.add('stop $id');
    return id;
  }

  /// The bodies the rig keeps, by id.
  final bodies = <String, Map<String, dynamic>>{};

  @override
  Future<SequenceDetail> getSequenceDetail(String id) async => SequenceDetail(id: id, body: bodies[id] ?? const {});

  @override
  Future<SequenceDetail> updateSequence(String id, {String? name, String? description, Map<String, dynamic>? body}) async {
    calls.add('update $id');
    if (body != null) bodies[id] = body;
    return SequenceDetail(id: id, body: bodies[id] ?? const {});
  }

  @override
  Future<bool> deleteSequence(String id) async {
    calls.add('delete $id');
    return bodies.remove(id) != null;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Run extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
  void set(SequenceRunStateInfo s) => state = AsyncData(s);
}

class _Panel extends FlatPanelNotifier {
  _Panel(this.connected, {this.cover = true});
  final bool connected;
  final bool cover;
  @override
  Future<FlatPanelStatus?> build() async => connected
      ? FlatPanelStatus.fromJson({
          'device_id': 'd',
          'name': 'Flip-Flat',
          'state': 'connected',
          'runtime': {'has_cover': cover},
        })
      : null;
}

class _Camera extends CameraStatusNotifier {
  _Camera(this.connected);
  final bool connected;
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson(
      {'device_id': 'c', 'name': 'ASI2600MM', 'state': connected ? 'connected' : 'disconnected'});
}

class _Plan extends SimpleRunNotifier {
  _Plan(this.v);
  final SimpleRun v;
  @override
  SimpleRun build() => v;
}

class _Guider extends GuiderStatusNotifier {
  _Guider(this.runtime);
  final String runtime;
  @override
  Future<void> refresh([GuiderClient? client]) async {}
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(
      {'device_id': 'g', 'name': 'PHD2', 'state': 'connected', 'runtime': {'state': runtime}});
}

class _GuiderCal extends GuiderCalibrationNotifier {
  final calls = <String>[];
  @override
  Future<CalibrationStatusResponse?> build() async => const CalibrationStatusResponse(connected: true, status: null);
  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async => calls.add('build darks');
}

final _night = (start: DateTime.utc(2026, 11, 10, 9), end: DateTime.utc(2026, 11, 10, 16));

CalibrationSession _session({
  String id = 'tonight',
  bool flats = false,
  bool darks = false,
  bool bias = false,
  DateTime? start,
  bool manual = false,
}) => CalibrationSession(
  id: id,
  targetName: 'NGC 7000',
  sessionStartUtc: start ?? _night.start.add(const Duration(minutes: 20)),
  sessionEndUtc: (start ?? _night.start).add(const Duration(hours: 6)),
  lightFrameCount: 186,
  filtersUsed: [
    const CalibrationFilterSummary(filterName: 'L', lightFrameCount: 60, meanExposureSeconds: 300, matchingFlatFrames: 25),
    for (final f in ['R', 'G', 'Ha'])
      CalibrationFilterSummary(filterName: f, lightFrameCount: 40, meanExposureSeconds: 300, matchingFlatFrames: flats ? 25 : 0),
  ],
  matchingFlatsAvailable: flats,
  matchingDarksAvailable: darks,
  matchingBiasAvailable: bias,
  manualCapture: manual,
);

Future<(ProviderContainer, _Calibration, _Seq, _Run)> _pump(
  WidgetTester tester,
  List<CalibrationSession> sessions, {
  bool panel = false,
  bool cover = true,
  bool camera = true,
  Widget? child,
  SimpleRun? plan,
  List<Override> overrides = const [],
}) async {
  final cal = _Calibration(sessions);
  final seq = _Seq();
  final run = _Run();
  final c = ProviderContainer(overrides: [
    calibrationApiProvider.overrideWithValue(cal),
    sequenceApiProvider.overrideWithValue(seq),
    sequenceRunStateProvider.overrideWith(() => run),
    flatPanelProvider.overrideWith(() => _Panel(panel, cover: cover)),
    cameraStatusProvider.overrideWith(() => _Camera(camera)),
    if (plan != null) simpleRunProvider.overrideWith(() => _Plan(plan)),
    ...overrides,
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 400,
          child: child ?? SimpleNightCalibration(startUtc: _night.start, endUtc: _night.end),
        ),
      ),
    ),
  ));
  // Keeps the autoDispose providers the row reads alive between pumps.
  c.listen(flatPanelProvider, (_, _) {});
  await tester.pumpAndSettle();
  return (c, cal, seq, run);
}

void main() {
  testWidgets('Night done: flats and darks missing offers both, flats first, and starts the run (#1563)', (tester) async {
    final (_, cal, seq, _) = await _pump(tester, [_session()]);

    expect(find.text('Missing for tonight'), findsOneWidget);
    expect(find.text('Flats for R, G, Ha · Darks · Bias'), findsOneWidget);
    await tester.tap(find.text('Take flats and darks'));
    await tester.pumpAndSettle();

    expect(cal.requests, [
      {'session': 'tonight', 'flats': true, 'darks': true, 'bias': true, 'lensCap': true, 'flavor': 'sky'},
    ], reason: 'no panel connected: the dawn sky');
    expect(seq.calls, ['start cal-run']);
  });

  testWidgets('Night done: only flats missing asks for flats alone, from the panel when one is connected', (tester) async {
    final (_, cal, _, _) = await _pump(tester, [_session(darks: true, bias: true)], panel: true);

    expect(find.text('Flats for R, G, Ha'), findsOneWidget);
    await tester.tap(find.text('Take flats'));
    await tester.pumpAndSettle();

    expect(cal.requests.single, containsPair('flavor', 'panel'));
    expect(cal.requests.single, allOf(containsPair('flats', true), containsPair('darks', false), containsPair('bias', false)));
  });

  testWidgets('Night done: flats on file (Plan shot them) offers darks only', (tester) async {
    final (_, cal, _, _) = await _pump(tester, [_session(flats: true)]);

    expect(find.text('Darks · Bias'), findsOneWidget);
    await tester.tap(find.text('Take darks'));
    await tester.pumpAndSettle();

    expect(cal.requests.single, allOf(containsPair('flats', false), containsPair('darks', true), containsPair('bias', true)));
  });

  testWidgets('Night done: everything on file says so and offers nothing', (tester) async {
    await _pump(tester, [_session(flats: true, darks: true, bias: true)]);

    expect(find.text('Flats, darks and bias on file'), findsOneWidget);
    expect(find.byKey(const Key('simple-calibration-take')), findsNothing);
  });

  testWidgets('Night done: no session for the run shows no calibration row', (tester) async {
    await _pump(tester, [_session(start: DateTime.utc(2026, 11, 8, 9))]);

    expect(find.byKey(const Key('simple-calibration')), findsNothing);
  });

  testWidgets("Night done: Preview's snapshots are not lights to calibrate (user, 2026-11-11)", (tester) async {
    await _pump(tester, [_session(id: 'preview', manual: true)]);

    expect(find.byKey(const Key('simple-calibration')), findsNothing);
  });

  testWidgets('Night done: the newest session in the run counts, not an older one', (tester) async {
    final (_, cal, _, _) = await _pump(tester, [
      _session(id: 'earlier', start: _night.start.subtract(const Duration(hours: 2))),
      _session(id: 'tonight', flats: true),
    ]);

    await tester.tap(find.byKey(const Key('simple-calibration-take')));
    await tester.pumpAndSettle();

    expect(cal.requests.single['session'], 'tonight');
  });

  testWidgets('Night done: the run shows n of N with a Stop, then done; a stopped one offers what is still missing', (tester) async {
    final (_, _, seq, run) = await _pump(tester, [_session()]);
    await tester.tap(find.byKey(const Key('simple-calibration-take')));
    await tester.pumpAndSettle();

    run.set(const SequenceRunStateInfo(sequenceId: 'cal-run', state: SequenceRunState.running, framesCaptured: 46));
    await tester.pump();
    expect(find.text('Calibration frames · 46 of 205'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-calibration-stop')));
    await tester.pumpAndSettle();
    expect(seq.calls.last, 'stop cal-run');

    run.set(const SequenceRunStateInfo(sequenceId: 'cal-run', state: SequenceRunState.stopped, framesCaptured: 121));
    await tester.pump();
    expect(find.text('Stopped · 121 of 205'), findsOneWidget);
    expect(find.text('Take flats and darks'), findsOneWidget, reason: 'the rig asks only for what is still uncovered');

    run.set(const SequenceRunStateInfo(sequenceId: 'cal-run', state: SequenceRunState.completed, framesCaptured: 205));
    await tester.pump();
    expect(find.text('Calibration frames done'), findsOneWidget);
  });

  testWidgets('Night done: a run that fails to start says why in an error pill', (tester) async {
    final (c, cal, seq, _) = await _pump(tester, [_session()]);
    cal.fails = true;

    await tester.tap(find.byKey(const Key('simple-calibration-take')));
    await tester.pumpAndSettle();

    final pill = c.read(simpleMessagesProvider).single;
    expect(pill.kind, SimpleMessageKind.error);
    expect(pill.title, 'Camera is not connected', reason: "the rig's own words name the cause");
    expect(seq.calls, isEmpty);
  });

  group('Calibration mode (#1757)', () {
    Widget panel() => const SingleChildScrollView(child: SimpleCalibrationPanel());

    Widget chip() => Consumer(builder: (context, ref, _) => Text(simpleCalibrationSummary(ref, context.l10n)));

    testWidgets('the chip says how many sets are missing', (tester) async {
      await _pump(tester, [_session()], child: chip());
      expect(find.text('5 missing'), findsOneWidget, reason: 'flats for R, G and Ha, darks, bias');
    });

    testWidgets('the chip says Ready when every set is covered', (tester) async {
      await _pump(tester, [_session(flats: true, darks: true, bias: true)], child: chip());
      expect(find.text('Ready'), findsOneWidget);
    });

    testWidgets("the chip says No lights when only Preview's snapshots were taken", (tester) async {
      await _pump(tester, [_session(manual: true)], child: chip());
      expect(find.text('No lights'), findsOneWidget);
    });

    testWidgets('the shutter shoots everything missing in one request, then starts it', (tester) async {
      final (_, cal, seq, _) = await _pump(tester, [_session()], child: const SimpleCalibrationShutter());
      tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton)).onPressed!();
      await tester.pumpAndSettle();
      expect(cal.requests.single, allOf(containsPair('flats', true), containsPair('darks', true), containsPair('bias', true)));
      expect(seq.calls, ['start cal-run']);
    });

    testWidgets('a disconnected camera turns the shutter off and says why', (tester) async {
      await _pump(tester, [_session()], camera: false, child: const SimpleCalibrationShutter());
      final shutter = tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton));
      expect(shutter.onPressed, isNull);
      expect(shutter.whyOff, 'Connect the camera first.');
    });

    testWidgets('while the Plan runs, the shutter asks first and sends nothing', (tester) async {
      const queued = SimpleRun(sequenceId: 'queue-1', targets: [SimpleTarget(name: 'M31')]);
      final (c, cal, _, run) = await _pump(tester, [_session()], plan: queued, child: const SimpleCalibrationShutter());
      run.set(const SequenceRunStateInfo(sequenceId: 'queue-1', state: SequenceRunState.running));
      await tester.pump();
      tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton)).onPressed!();
      await tester.pumpAndSettle();
      expect(cal.requests, isEmpty);
      final ask = c.read(simpleMessagesProvider).single;
      expect(ask.kind, SimpleMessageKind.question);
      expect(ask.actions.map((a) => a.label), contains('Add to queue'));
    });

    testWidgets('Flats asks for flats only, from the panel when connected; Darks & bias for darks and bias only', (tester) async {
      final (c, cal, _, _) = await _pump(tester, [_session()], panel: true, child: panel());
      await tester.tap(find.byKey(const Key('simple-calibration-shoot-flats')));
      await tester.pumpAndSettle();
      expect(cal.requests.last, allOf(containsPair('flats', true), containsPair('darks', false), containsPair('bias', false), containsPair('flavor', 'panel')));

      c.read(simpleCalibrationProvider.notifier).state = null;
      c.read(simpleCalibrationTabProvider.notifier).set(SimpleCalibrationTab.darks);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('simple-calibration-shoot-darks')));
      await tester.pumpAndSettle();
      expect(cal.requests.last, allOf(containsPair('flats', false), containsPair('darks', true), containsPair('bias', true)));
    });

    testWidgets('Add to queue puts one Calibration block after the targets, closing the cover, without starting; a second is refused', (tester) async {
      Map<String, dynamic> container(String name, List<Map<String, dynamic>> kids) =>
          withChildren(instructionForType(sequentialContainerType)!.build()..['Name'] = name, kids);
      final darks = withConditions(container('Darks 300 s', [
        instructionForType(takeExposureType)!.build()..['ExposureTime'] = 300.0,
      ]), [conditionForType(loopConditionType)!.build()..['Iterations'] = 20]);
      const queued = SimpleRun(sequenceId: 'queue-1', targets: [SimpleTarget(name: 'M31')]);
      final (c, cal, seq, _) = await _pump(tester, [_session(flats: true)], panel: true, plan: queued, child: panel());
      seq.bodies['queue-1'] = container('Queue', [container('M31', const [])]);
      seq.bodies['cal-run'] = container('Darks — NGC 7000', [darks]);
      c.read(simpleCalibrationTabProvider.notifier).set(SimpleCalibrationTab.darks);
      await tester.pumpAndSettle();

      await tester.tap(find.byKey(const Key('simple-calibration-queue-darks')));
      await tester.pumpAndSettle();

      expect(cal.requests.single, allOf(containsPair('flats', false), containsPair('darks', true), containsPair('lensCap', false)),
          reason: 'the cover closes instead of asking for the lens cap');
      expect(seq.calls, ['delete cal-run', 'update queue-1'], reason: 'no start; the run the generator saved goes');
      final block = childrenOf(seq.bodies['queue-1']!).last;
      expect(block['Name'], kCalibrationBlockName);
      expect(childrenOf(block).first[r'$type'], closeFlatPanelCoverType);
      final target = c.read(simpleRunProvider).targets.last;
      expect(target.calibration, isTrue);
      expect(simplePlanTotal(target.steps), 20);

      await tester.tap(find.byKey(const Key('simple-calibration-queue-darks')));
      await tester.pumpAndSettle();
      expect(c.read(simpleMessagesProvider).first.title, 'The queue already has its calibration frames.');
    });

    testWidgets('Guide Camera: building asks for the guide scope cover first', (tester) async {
      final cal = _GuiderCal();
      final (c, _, _, _) = await _pump(tester, [], child: panel(), overrides: [
        guiderStatusProvider.overrideWith(() => _Guider('stopped')),
        guiderCalibrationProvider.overrideWith(() => cal),
      ]);
      c.read(simpleCalibrationTabProvider.notifier).set(SimpleCalibrationTab.guide);
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('simple-calibration-guide-build-darks')));
      await tester.pumpAndSettle();
      final ask = c.read(simpleMessagesProvider).single;
      expect(ask.title, 'Cover the guide scope, then build.');
      expect(cal.calls, isEmpty, reason: 'nothing is shot before the cover goes on');
      ask.actions.last.onPressed();
      await tester.pump();
      expect(cal.calls, ['build darks']);
    });

    testWidgets('Guide Camera: no build while guiding, and it says why', (tester) async {
      final (c, _, _, _) = await _pump(tester, [], child: panel(), overrides: [
        guiderStatusProvider.overrideWith(() => _Guider('guiding')),
        guiderCalibrationProvider.overrideWith(_GuiderCal.new),
      ]);
      c.read(simpleCalibrationTabProvider.notifier).set(SimpleCalibrationTab.guide);
      await tester.pumpAndSettle();
      expect(tester.widget<FilledButton>(find.byKey(const Key('simple-calibration-guide-build-darks'))).onPressed, isNull);
      expect(find.text('Stop guiding to build: a plan would lose its guide star.'), findsOneWidget);
    });
  });

  test('a Calibration block counts its loops\' frames and exposure time', () {
    Map<String, dynamic> loop(double exp, int n) => withConditions(
        withChildren(instructionForType(sequentialContainerType)!.build(), [
          instructionForType(takeExposureType)!.build()..['ExposureTime'] = exp,
        ]),
        [conditionForType(loopConditionType)!.build()..['Iterations'] = n]);
    final block = calibrationBlockFrom(
        withChildren(instructionForType(sequentialContainerType)!.build(), [loop(300, 20), loop(0.001, 30)]));
    final t = calibrationBlockTime(block);
    expect(t.frames, 50);
    expect(t.seconds, closeTo(6000.03, 1e-6));
  });

  test('the dawn stop leaves the Calibration block alone: darks need no dark sky', () {
    Map<String, dynamic> named(String n) => instructionForType(sequentialContainerType)!.build()..['Name'] = n;
    final root = withChildren(named('Queue'), [named('M31'), named(kCalibrationBlockName)]);
    final stopped = childrenOf(withDawnStop(root, 'Astronomical Dawn'));
    expect(conditionsOf(stopped.first), isNotEmpty);
    expect(conditionsOf(stopped.last), isEmpty);
  });
}
