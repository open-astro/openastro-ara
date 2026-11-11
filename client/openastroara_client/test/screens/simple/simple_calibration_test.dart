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
import 'package:openastroara/screens/simple/simple_preview_panel.dart' show SimpleShutterButton;
import 'package:openastroara/screens/simple/simple_device_windows.dart' show SimpleWindowButton;
import 'package:openastroara/state/simple_calibration_state.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/simple_run_state.dart';
import 'package:openastroara/theme/ara_colors.dart';

/// The rig's calibration routes: its sessions, and the matching-flats
/// requests sent.
class _Calibration implements CalibrationClient {
  _Calibration(this.sessions);
  final List<CalibrationSession> sessions;
  final requests = <Map<String, Object?>>[];
  bool fails = false;

  /// The camera's library, and the builds asked for.
  DarkLibraryState library = const DarkLibraryState(
      status: 'idle', totalCombinations: 0, completedCombinations: 0, generatedSequenceId: null, entries: []);
  final builds = <DarkLibraryBuildRequest>[];

  @override
  Future<DarkLibraryState> darkLibraryStatus() async => library;

  @override
  Future<void> buildDarkLibrary(DarkLibraryBuildRequest request) async {
    builds.add(request);
    library = DarkLibraryState(
        status: 'pending',
        totalCombinations: request.exposureSecondsList.length,
        completedCombinations: 0,
        generatedSequenceId: 'lib-run',
        entries: library.entries,
        biasEntries: library.biasEntries);
  }

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
  Future<SequenceDetail> addRunItem(String id, {required List<int> parentPath, int? index, required Map<String, dynamic> item}) async {
    calls.add('add-live $id');
    final kids = childrenOf(bodies[id]!)..insert(index ?? childrenOf(bodies[id]!).length, item);
    bodies[id] = withChildren(bodies[id]!, kids);
    return SequenceDetail(id: id, body: bodies[id]!);
  }

  @override
  Future<String> create(String name, Map<String, dynamic> body, {String? description, String? idempotencyKey}) async {
    calls.add('create $name');
    bodies['queue-cal'] = body;
    return 'queue-cal';
  }

  @override
  Future<bool> deleteSequence(String id) async {
    calls.add('delete $id');
    return bodies.remove(id) != null;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// The rig's run states: the Plan's selected sequence, and the calibration
/// run's own poll (it is never the selected one).
class _Run extends SequenceRunStateNotifier {
  final calibration = _CalRun();
  @override
  Future<SequenceRunStateInfo?> build() async => null;
  void set(SequenceRunStateInfo s) =>
      s.sequenceId == 'queue-cal' ? calibration.set(s) : state = AsyncData(s);
}

class _CalRun extends SimpleCalibrationRunStateNotifier {
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
  _GuiderCal([this.status]);
  final CalibrationStatus? status;
  final calls = <String>[];
  @override
  Future<CalibrationStatusResponse?> build() async => CalibrationStatusResponse(connected: true, status: status);
  @override
  Future<void> buildDefectMap({int exposureMs = 3000, int frameCount = 10, String? notes, bool loadAfter = true}) async =>
      calls.add('build defects');
  @override
  Future<void> deleteCalibrationFiles({bool darks = true, bool defectmap = true}) async =>
      calls.add('delete darks=$darks defectmap=$defectmap');
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
    simpleCalibrationRunStateProvider.overrideWith(() => run.calibration),
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
  c.listen(sequenceRunStateProvider, (_, _) {});
  await tester.pumpAndSettle();
  return (c, cal, seq, run);
}

/// A dark set in the camera's library.
DarkLibraryEntry _dark(double exposure, {int gain = 100, double temp = -10, int ageDays = 21}) => DarkLibraryEntry(
    id: '$exposure', exposureSeconds: exposure, gain: gain, temperatureC: temp, frameCount: 30,
    capturedUtc: DateTime.now().toUtc().subtract(Duration(days: ageDays)), fileSizeBytes: 1);

final _bias = BiasLibraryEntry(
    id: 'b', gain: 100, offset: null, bin: 1, frameCount: 50, capturedUtc: DateTime.now().toUtc().subtract(const Duration(days: 21)), fileSizeBytes: 1);

/// A queued plan at gain 100: L and Ha at 300 s, and [extra] more rows.
SimpleRun _queued([List<FilterPlanStep> extra = const []]) => SimpleRun(sequenceId: 'queue-1', targets: [
      SimpleTarget(name: 'NGC 7000', gain: 100, steps: [
        const FilterPlanStep(filterName: 'L', exposureSeconds: 300, frameCount: 40),
        ...extra,
      ]),
    ]);

Map<String, dynamic> _container(String name, [List<Map<String, dynamic>> kids = const []]) =>
    withChildren(instructionForType(sequentialContainerType)!.build()..['Name'] = name, kids);

void main() {
  group('Night done (#1563, round 2)', () {
    testWidgets('flats and darks missing: two rows; Take flats asks for flats only and, with no panel, the screen first', (tester) async {
      final (_, cal, seq, _) = await _pump(tester, [_session()]);
      seq.bodies['cal-run'] = _container('run', [_container('Flats L')]);

      expect(find.text('Flats missing'), findsOneWidget);
      expect(find.text('R, G, Ha'), findsOneWidget);
      expect(find.text('Darks missing from the library'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();

      expect(cal.requests.single, allOf(containsPair('flats', true), containsPair('darks', false), containsPair('flavor', 'panel')));
      expect(seq.calls, ['delete cal-run', 'create Calibration', 'start queue-cal'],
          reason: 'a queue of its own, so it has a plan square (user, 2026-11-12)');
      final block = childrenOf(seq.bodies['queue-cal']!).single;
      expect(block['Name'], kCalibrationBlockName);
      expect(childrenOf(block).first['Text'],
          'Put the screen or light box over the scope, then press Resume to take the flats.');
    });

    testWidgets('Take darks asks for the darks and bias the library lacks for tonight', (tester) async {
      final (_, cal, _, _) = await _pump(tester, [_session(flats: true)]);
      await tester.tap(find.byKey(const Key('simple-calibration-take-darks')));
      await tester.pumpAndSettle();
      expect(cal.requests.single, allOf(containsPair('flats', false), containsPair('darks', true), containsPair('bias', true)));
    });

    testWidgets("the library has tonight's darks: flats only, and a connected panel lights itself", (tester) async {
      final (_, cal, seq, _) = await _pump(tester, [_session(darks: true, bias: true)], panel: true);
      expect(find.byKey(const Key('simple-calibration-take-darks')), findsNothing, reason: 'darks come from the library');
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();
      expect(cal.requests.single, containsPair('flavor', 'panel'));
      expect(seq.calls, ['delete cal-run', 'create Calibration', 'start queue-cal']);
      expect(childrenOf(childrenOf(seq.bodies['queue-cal']!).single), isEmpty, reason: 'nothing asked');
    });

    testWidgets('with the twilight sky picked, the flats wait for it and nothing is asked', (tester) async {
      final (c, cal, seq, _) = await _pump(tester, [_session(darks: true, bias: true)]);
      c.read(simpleFlatLightProvider.notifier).set(SimpleFlatLight.sky);
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();
      expect(cal.requests.single, containsPair('flavor', 'sky'));
      expect(childrenOf(childrenOf(seq.bodies['queue-cal']!).single), isEmpty, reason: 'nothing asked');
    });

    testWidgets('everything on file says so and offers nothing', (tester) async {
      await _pump(tester, [_session(flats: true, darks: true, bias: true)]);
      expect(find.text('Flats on file'), findsOneWidget);
      expect(find.byType(FilledButton), findsNothing);
      expect(find.byType(OutlinedButton), findsNothing);
    });

    testWidgets('no session for the run, or only Preview snapshots, shows no calibration rows', (tester) async {
      await _pump(tester, [_session(start: DateTime.utc(2026, 11, 8, 9)), _session(id: 'preview', manual: true)]);
      expect(find.byKey(const Key('simple-calibration')), findsNothing);
    });

    testWidgets('the newest session in the run counts, not an older one', (tester) async {
      final (_, cal, _, _) = await _pump(tester, [
        _session(id: 'earlier', start: _night.start.subtract(const Duration(hours: 2))),
        _session(id: 'tonight', darks: true, bias: true),
      ]);
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();
      expect(cal.requests.single['session'], 'tonight');
    });

    testWidgets('the run shows n of N with a Stop, then done; a stopped one offers what is still missing', (tester) async {
      final (_, _, seq, run) = await _pump(tester, [_session(darks: true, bias: true)], panel: true);
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();

      run.set(const SequenceRunStateInfo(sequenceId: 'queue-cal', state: SequenceRunState.running, framesCaptured: 46));
      await tester.pump();
      expect(find.text('Calibration frames · 46 of 205'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-calibration-stop')));
      await tester.pumpAndSettle();
      expect(seq.calls.last, 'stop queue-cal');

      run.set(const SequenceRunStateInfo(sequenceId: 'queue-cal', state: SequenceRunState.stopped, framesCaptured: 121));
      await tester.pump();
      expect(find.text('Stopped · 121 of 205'), findsOneWidget);
      expect(find.byKey(const Key('simple-calibration-take-flats')), findsOneWidget);

      run.set(const SequenceRunStateInfo(sequenceId: 'queue-cal', state: SequenceRunState.completed, framesCaptured: 205));
      await tester.pump();
      expect(find.text('Calibration frames done'), findsOneWidget);
    });

    testWidgets('a run that fails to start says why in an error pill', (tester) async {
      final (c, cal, seq, _) = await _pump(tester, [_session()]);
      cal.fails = true;
      await tester.tap(find.byKey(const Key('simple-calibration-take-flats')));
      await tester.pumpAndSettle();
      final pill = c.read(simpleMessagesProvider).single;
      expect(pill.kind, SimpleMessageKind.error);
      expect(pill.title, 'Camera is not connected', reason: "the rig's own words name the cause");
      expect(seq.calls, isEmpty);
    });
  });

  group('Calibration mode (#1757, round 2)', () {
    Widget panel() => const SingleChildScrollView(child: SimpleCalibrationPanel());
    Widget panelView() => panel();
    Widget chip() => Consumer(builder: (context, ref, _) => Text(simpleCalibrationSummary(context, ref)));
    Widget shutter() => const SimpleCalibrationShutter();
    SimpleShutterButton button(WidgetTester tester) => tester.widget<SimpleShutterButton>(find.byType(SimpleShutterButton));

    testWidgets('Flats: the chip counts the filters without flats', (tester) async {
      await _pump(tester, [_session()], child: chip());
      expect(find.text('3 missing'), findsOneWidget);
    });

    testWidgets('Flats: the chip says Ready when every filter is covered', (tester) async {
      await _pump(tester, [_session(flats: true)], child: chip());
      expect(find.text('Ready'), findsOneWidget);
    });

    testWidgets("Flats: the chip says No lights when only Preview's snapshots were taken", (tester) async {
      await _pump(tester, [_session(manual: true)], child: chip());
      expect(find.text('No lights'), findsOneWidget);
    });

    testWidgets('Flats: the shutter shoots the missing flats only, its caption says so', (tester) async {
      final (_, cal, seq, _) = await _pump(tester, [_session()], panel: true, child: shutter());
      expect(find.byKey(const Key('simple-calibration-shutter-caption')), findsOneWidget);
      expect(find.text('Shoot flats'), findsOneWidget);
      button(tester).onPressed!();
      await tester.pumpAndSettle();
      expect(cal.requests.single, allOf(containsPair('flats', true), containsPair('darks', false), containsPair('bias', false)));
      expect(seq.calls, ['delete cal-run', 'create Calibration', 'start queue-cal']);
    });

    testWidgets('a disconnected camera turns the shutter off and says why', (tester) async {
      await _pump(tester, [_session()], camera: false, child: shutter());
      expect(button(tester).onPressed, isNull);
      expect(button(tester).whyOff, 'Connect the camera first.');
    });

    testWidgets('while the Plan images, the shutter puts the flats in its queue after the targets', (tester) async {
      final (c, cal, seq, run) = await _pump(tester, [_session()], panel: true, plan: _queued(), child: shutter());
      seq.bodies['queue-1'] = _container('Queue', [_container('NGC 7000')]);
      run.set(const SequenceRunStateInfo(sequenceId: 'queue-1', state: SequenceRunState.running));
      await tester.pump();
      button(tester).onPressed!();
      await tester.pumpAndSettle();
      expect(cal.requests.single, containsPair('flats', true));
      expect(seq.calls, ['delete cal-run', 'add-live queue-1'], reason: 'no second run: the running queue takes it');
      expect(childrenOf(seq.bodies['queue-1']!).last['Name'], kCalibrationBlockName);
      expect(c.read(simpleRunProvider).targets.last.calibration, isTrue, reason: 'a plan square');
    });

    testWidgets('Covered and Missing are outlined pills in their state colour, as in the mock', (tester) async {
      await _pump(tester, [_session()], child: panel());
      BoxDecoration outline(String word) => tester
          .widget<Container>(find.ancestor(of: find.text(word).first, matching: find.byType(Container)).first)
          .decoration! as BoxDecoration;
      final missing = outline('Missing'), covered = outline('Covered');
      expect((missing.border! as Border).top.color, AraColors.accentBusy);
      expect((covered.border! as Border).top.color, AraColors.accentConnected);
      expect(missing.borderRadius, BorderRadius.circular(10));
      expect(missing.color, isNull, reason: 'an outline, not a fill');
      expect(find.text('No flats'), findsNothing, reason: 'the Missing pill already says it (user, 2026-11-11)');
    });

    group('Darks & bias: the camera\'s library', () {
      Future<(ProviderContainer, _Calibration, _Seq, _Run)> library(WidgetTester tester, List<DarkLibraryEntry> darks,
          {List<BiasLibraryEntry>? bias, List<FilterPlanStep> extra = const [], Widget? child, bool panel = false}) async {
        final r = await _pump(tester, [_session()], panel: panel, plan: _queued(extra), child: child ?? panelView(), overrides: [
          simpleCalibrationTabProvider.overrideWith(_DarksTab.new),
        ]);
        r.$2.library = DarkLibraryState(status: 'idle', totalCombinations: 0, completedCombinations: 0,
            generatedSequenceId: null, entries: darks, biasEntries: bias ?? [_bias]);
        r.$1.invalidate(darkLibraryStatusProvider);
        r.$3.bodies['lib-run'] = _container('Dark library', [_container('Darks 120 s')]);
        r.$3.bodies['queue-1'] = _container('Queue', [_container('NGC 7000')]);
        await tester.pumpAndSettle();
        return r;
      }

      testWidgets('covers the plan: says so, lists what is on file with its age, and the shutter has nothing to do', (tester) async {
        await library(tester, [_dark(300)]);
        expect(find.text('Covers your plan'), findsOneWidget);
        expect(find.textContaining('3 weeks old · 30 frames'), findsWidgets);
        expect(find.textContaining('ASI2600MM'), findsOneWidget, reason: 'the library belongs to the camera');
      });

      testWidgets('the library is the camera the rig names, and a set from before cameras were recorded says so', (tester) async {
        final (c, cal, _, _) = await library(tester, [_dark(300)]);
        cal.library = DarkLibraryState(status: 'idle', totalCombinations: 0, completedCombinations: 0,
            generatedSequenceId: null, entries: [_dark(300)], biasEntries: [_bias], camera: 'ZWO ASI533MC Pro');
        c.invalidate(darkLibraryStatusProvider);
        await tester.pumpAndSettle();
        expect(find.text('ZWO ASI533MC Pro · reused for months'), findsOneWidget);
        expect(find.textContaining('camera not recorded'), findsNWidgets(2), reason: 'the legacy dark and bias sets count for any camera');
      });

      testWidgets("a gap: the plan's missing exposure is named, and the shutter builds just that at the plan's gain and temperature", (tester) async {
        final (c, cal, seq, _) = await library(tester, [_dark(300)],
            extra: const [FilterPlanStep(filterName: 'Ha', exposureSeconds: 120, frameCount: 20)], child: shutter());
        expect(find.text('Build 2 min'), findsOneWidget);
        button(tester).onPressed!();
        await tester.pumpAndSettle();
        final b = cal.builds.single;
        expect(b.exposureSecondsList, [120.0]);
        expect(b.gainList, [100]);
        expect(b.targetTemperatureCList, [-10.0]);
        expect(b.reuseExistingFrames, isTrue);
        expect(seq.calls, ['update lib-run', 'delete lib-run', 'update queue-1'],
            reason: 'after the queued target, as a plan square');
        final block = childrenOf(seq.bodies['queue-1']!).last;
        expect(childrenOf(block).first[r'$type'], waitForUserType, reason: 'the lens cap first');
        expect(c.read(simpleMessagesProvider).single.title,
            'The calibration frames are in the queue after your targets. Start the queue now?');
      });

      testWidgets('old darks: an amber Old pill, and the shutter refreshes them (no reuse)', (tester) async {
        final (_, cal, _, _) = await library(tester, [_dark(300, ageDays: 220)], child: Column(children: [shutter(), Expanded(child: panel())]));
        expect(find.text('Old'), findsOneWidget);
        expect(find.text('Refresh 5 min'), findsWidgets);
        button(tester).onPressed!();
        await tester.pumpAndSettle();
        expect(cal.builds.single.reuseExistingFrames, isFalse);
      });

      testWidgets("no bias at the plan's gain: the build shoots bias first, after the lens cap", (tester) async {
        final (_, _, seq, _) = await library(tester, [_dark(300)], bias: const [], child: shutter());
        expect(find.text('Build bias'), findsOneWidget);
        button(tester).onPressed!();
        await tester.pumpAndSettle();
        final kids = childrenOf(childrenOf(seq.bodies['queue-1']!).last);
        expect(kids[0][r'$type'], waitForUserType);
        expect(kids[1]['Name'], startsWith('Bias'));
      });

      testWidgets('Add to queue puts the library run in the Queue as its Calibration block, closing the cover, without starting', (tester) async {
        final (c, cal, seq, _) = await library(tester, [], panel: true);
        seq.bodies['queue-1'] = _container('Queue', [_container('NGC 7000')]);
        await tester.ensureVisible(find.byKey(const Key('simple-calibration-queue-library')));
        await tester.tap(find.byKey(const Key('simple-calibration-queue-library')));
        await tester.pumpAndSettle();
        expect(cal.builds.single.exposureSecondsList, [300.0]);
        expect(seq.calls, ['update lib-run', 'delete lib-run', 'update queue-1'], reason: 'no start');
        final block = childrenOf(seq.bodies['queue-1']!).last;
        expect(block['Name'], kCalibrationBlockName);
        expect(childrenOf(block).first[r'$type'], closeFlatPanelCoverType);
        expect(c.read(simpleRunProvider).targets.last.calibration, isTrue);
      });
    });

    group('Guide Camera', () {
      const built = CalibrationStatus(darkLibraryExists: true, defectMapExists: true, darkLibraryLoaded: true, defectMapLoaded: true);

      testWidgets('building asks for the guide scope cover first', (tester) async {
        final cal = _GuiderCal();
        final (c, _, _, _) = await _pump(tester, [], child: panel(), overrides: [
          guiderStatusProvider.overrideWith(() => _Guider('stopped')),
          guiderCalibrationProvider.overrideWith(() => cal),
          simpleCalibrationTabProvider.overrideWith(_GuideTab.new),
        ]);
        await tester.tap(find.byKey(const Key('simple-calibration-guide-build-darks')));
        await tester.pumpAndSettle();
        final ask = c.read(simpleMessagesProvider).single;
        expect(ask.title, 'Cover the guide scope, then build.');
        expect(cal.calls, isEmpty, reason: 'nothing is shot before the cover goes on');
        ask.actions.last.onPressed();
        await tester.pump();
        expect(cal.calls, ['build darks']);
      });

      testWidgets('no build while guiding, and it says why', (tester) async {
        await _pump(tester, [], child: panel(), overrides: [
          guiderStatusProvider.overrideWith(() => _Guider('guiding')),
          guiderCalibrationProvider.overrideWith(_GuiderCal.new),
          simpleCalibrationTabProvider.overrideWith(_GuideTab.new),
        ]);
        expect(tester.widget<SimpleWindowButton>(find.byKey(const Key('simple-calibration-guide-build-darks'))).onTap, isNull);
        expect(find.text('Stop guiding to build: a plan would lose its guide star.'), findsOneWidget);
      });

      testWidgets('the bad-pixel map has Delete like the dark library, and each deletes only its own file', (tester) async {
        final cal = _GuiderCal(built);
        await _pump(tester, [], child: panel(), overrides: [
          guiderStatusProvider.overrideWith(() => _Guider('stopped')),
          guiderCalibrationProvider.overrideWith(() => cal),
          simpleCalibrationTabProvider.overrideWith(_GuideTab.new),
        ]);
        await tester.ensureVisible(find.byKey(const Key('simple-calibration-guide-delete-defects')));
        await tester.tap(find.byKey(const Key('simple-calibration-guide-delete-defects')));
        await tester.pump();
        await tester.tap(find.byKey(const Key('simple-calibration-guide-delete-darks')));
        await tester.pump();
        expect(cal.calls, ['delete darks=false defectmap=true', 'delete darks=true defectmap=false']);
      });
    });
  });

  test('the library covers a plan within 1 °C at its gain; older than six months is old; bias is matched by gain', () {
    final lib = DarkLibraryState(status: 'idle', totalCombinations: 0, completedCombinations: 0, generatedSequenceId: null,
        entries: [_dark(300, temp: -10.6), _dark(180, gain: 200), _dark(60, ageDays: 200)], biasEntries: [_bias]);
    final cover = libraryCover(lib, (exposures: [60, 120, 180, 300], gain: 100, offset: 50, temperatureC: -10),
        DateTime.now().toUtc());
    expect(cover.missing, [120, 180], reason: '180 s is at another gain');
    expect(cover.old, [60]);
    expect(cover.biasMissing, isFalse);
    expect(libraryCover(lib, (exposures: [300], gain: 200, offset: null, temperatureC: null), DateTime.now().toUtc()).biasMissing,
        isTrue);
  });

  test("a Calibration block counts its loops' frames and exposure time", () {
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

  test('the dawn stop leaves the Calibration and end-of-night Flats blocks alone: they need no dark sky', () {
    Map<String, dynamic> named(String n) => instructionForType(sequentialContainerType)!.build()..['Name'] = n;
    final root = withChildren(named('Queue'), [named('M31'), named(kEndFlatsBlockName), named(kCalibrationBlockName)]);
    final stopped = childrenOf(withDawnStop(root, 'Astronomical Dawn'));
    expect(conditionsOf(stopped[0]), isNotEmpty);
    expect(conditionsOf(stopped[1]), isEmpty);
    expect(conditionsOf(stopped[2]), isEmpty);
  });
}

class _DarksTab extends SimpleCalibrationTabNotifier {
  @override
  SimpleCalibrationTab build() => SimpleCalibrationTab.darks;
}

class _GuideTab extends SimpleCalibrationTabNotifier {
  @override
  SimpleCalibrationTab build() => SimpleCalibrationTab.guide;
}
