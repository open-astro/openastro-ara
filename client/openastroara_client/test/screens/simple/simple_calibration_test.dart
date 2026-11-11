import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
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

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Run extends SequenceRunStateNotifier {
  @override
  Future<SequenceRunStateInfo?> build() async => null;
  void set(SequenceRunStateInfo s) => state = AsyncData(s);
}

class _Panel extends FlatPanelNotifier {
  _Panel(this.connected);
  final bool connected;
  @override
  Future<FlatPanelStatus?> build() async => connected
      ? FlatPanelStatus.fromJson({'device_id': 'd', 'name': 'Flip-Flat', 'state': 'connected'})
      : null;
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
}) async {
  final cal = _Calibration(sessions);
  final seq = _Seq();
  final run = _Run();
  final c = ProviderContainer(overrides: [
    calibrationApiProvider.overrideWithValue(cal),
    sequenceApiProvider.overrideWithValue(seq),
    sequenceRunStateProvider.overrideWith(() => run),
    flatPanelProvider.overrideWith(() => _Panel(panel)),
  ]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(
      home: Scaffold(
        body: SizedBox(
          width: 400,
          child: SimpleNightCalibration(startUtc: _night.start, endUtc: _night.end),
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
}
