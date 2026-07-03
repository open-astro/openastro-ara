import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration/calibration_models.dart';
import 'package:openastroara/screens/calibration/calibration_screen.dart';
import 'package:openastroara/services/calibration_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/calibration/calibration_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';

class _FakeCalibrationClient implements CalibrationClient {
  final List<CalibrationSession> sessions;
  final DarkLibraryState darkState;
  String? flatsSessionId;
  int? flatsFrameCount;
  DarkLibraryBuildRequest? buildRequest;

  _FakeCalibrationClient({
    this.sessions = const [],
    this.darkState = const DarkLibraryState(
      status: 'idle',
      totalCombinations: 0,
      completedCombinations: 0,
      generatedSequenceId: null,
      entries: [],
    ),
  });

  @override
  Future<List<CalibrationSession>> listSessions({int limit = 50}) async =>
      sessions;

  @override
  Future<GeneratedFlatSequence> generateMatchingFlats(
    String sessionId, {
    int? frameCount,
    int? targetAdu,
    bool generateOnly = false,
  }) async {
    flatsSessionId = sessionId;
    flatsFrameCount = frameCount;
    return const GeneratedFlatSequence(
      generatedSequenceId: 'seq-flats-1',
      generatedSequenceName: 'Flats — M42',
      totalFlatFrames: 40,
    );
  }

  @override
  Future<DarkLibraryState> darkLibraryStatus() async => darkState;

  @override
  Future<void> buildDarkLibrary(DarkLibraryBuildRequest request) async {
    buildRequest = request;
  }

  @override
  void close() {}
}

CalibrationSession _session({bool flats = false, bool darks = true}) =>
    CalibrationSession(
      id: 'cal-sess-1',
      targetName: 'M42',
      sessionStartUtc: DateTime.utc(2026, 6, 30, 22),
      sessionEndUtc: DateTime.utc(2026, 7, 1, 3),
      lightFrameCount: 40,
      filtersUsed: const [
        CalibrationFilterSummary(
            filterName: 'Ha', lightFrameCount: 24, meanExposureSeconds: 300),
        CalibrationFilterSummary(
            filterName: 'OIII', lightFrameCount: 16, meanExposureSeconds: 180),
      ],
      matchingFlatsAvailable: flats,
      matchingDarksAvailable: darks,
    );

Future<ProviderContainer> _pump(WidgetTester tester,
    _FakeCalibrationClient fake) async {
  final container = ProviderContainer(overrides: [
    calibrationApiProvider.overrideWithValue(fake),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: CalibrationScreen()),
  ));
  await tester.pumpAndSettle();
  return container;
}

void main() {
  testWidgets('sessions render per-filter summary and coverage badges',
      (tester) async {
    await _pump(tester, _FakeCalibrationClient(sessions: [_session()]));

    expect(find.textContaining('M42'), findsWidgets);
    expect(find.textContaining('Ha 24×300s'), findsOneWidget);
    expect(find.text('Flats needed'), findsOneWidget);
    expect(find.text('Darks matched'), findsOneWidget);
  });

  testWidgets(
      '§39.5 flow: generate matching flats selects the sequence and jumps to Run',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [_session()]);
    final container = await _pump(tester, fake);

    await tester.tap(find.text('Capture Matching Flats'));
    await tester.pumpAndSettle();
    expect(find.text('Generate & open'), findsOneWidget);

    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();

    expect(fake.flatsSessionId, 'cal-sess-1');
    expect(fake.flatsFrameCount, 20, reason: 'the dialog default');
    expect(container.read(selectedSequenceIdProvider), 'seq-flats-1',
        reason: 'the generated sequence is selected for the Run tab');
    expect(container.read(selectedTabIndexProvider), 1,
        reason: 'the shell switches to the Run tab');
  });

  testWidgets('dark library lists entries and opens the build sequence',
      (tester) async {
    final fake = _FakeCalibrationClient(
      darkState: DarkLibraryState(
        status: 'pending',
        totalCombinations: 2,
        completedCombinations: 1,
        generatedSequenceId: 'seq-darks-1',
        entries: [
          DarkLibraryEntry(
            id: 'e1',
            exposureSeconds: 300,
            gain: 100,
            temperatureC: -10,
            frameCount: 30,
            capturedUtc: DateTime.utc(2026, 6, 20),
            fileSizeBytes: 512 * 1024 * 1024,
          ),
          DarkLibraryEntry(
            id: 'e2',
            exposureSeconds: 0.5,
            gain: null,
            temperatureC: 0,
            frameCount: 50,
            capturedUtc: DateTime.utc(2026, 6, 21),
            fileSizeBytes: 1024 * 1024,
          ),
        ],
      ),
    );
    final container = await _pump(tester, fake);

    await tester.tap(find.text('Dark Library'));
    await tester.pumpAndSettle();

    expect(find.text('Status: pending'), findsOneWidget);
    expect(find.text('1/2 combinations'), findsOneWidget);
    expect(find.textContaining('300s · gain 100'), findsOneWidget);
    expect(find.textContaining('0.5s · default gain'), findsOneWidget,
        reason: 'null gain renders honestly, sub-second exposures keep precision');

    await tester.tap(find.text('Open build sequence'));
    await tester.pumpAndSettle();
    expect(container.read(selectedSequenceIdProvider), 'seq-darks-1');
    expect(container.read(selectedTabIndexProvider), 1);
  });

  testWidgets('the dark build form submits the parsed matrix', (tester) async {
    final fake = _FakeCalibrationClient(sessions: [_session()]);
    await _pump(tester, fake);

    await tester.tap(find.text('Dark Library'));
    await tester.pumpAndSettle();

    await tester.enterText(
        find.widgetWithText(TextField, 'Exposures (s, comma-separated)'),
        '0.5, 60');
    await tester.enterText(
        find.widgetWithText(TextField, 'Sensor temperatures (°C, comma-separated)'),
        '-10');
    await tester.ensureVisible(find.text('Generate build sequence'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Generate build sequence'), warnIfMissed: true);
    await tester.pumpAndSettle();

    final req = fake.buildRequest;
    expect(req, isNotNull);
    expect(req!.exposureSecondsList, [0.5, 60]);
    expect(req.gainList, [100]);
    expect(req.targetTemperatureCList, [-10]);
    expect(req.framesPerCombination, 30);
    expect(req.reuseExistingFrames, isTrue);
  });
}
