import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration/calibration_models.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/screens/calibration/calibration_screen.dart';
import 'package:openastroara/services/calibration_api.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/calibration/calibration_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';

class _FakeCalibrationClient implements CalibrationClient {
  final List<CalibrationSession> sessions;
  final DarkLibraryState darkState;
  String? flatsSessionId;
  int? flatsFrameCount;
  bool? flatsCoolCamera;
  double? flatsCoolTo;
  bool? flatsPark;
  bool? flatsIncludeCovered;
  bool? flatsIncludeBias;
  bool? flatsIncludeDarks;
  bool? flatsLensCap;
  bool? flatsIncludeFlats;
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
  Future<CursorPage<CalibrationSession>> listSessions(
          {int limit = 200, String? cursor}) async =>
      CursorPage(items: sessions, nextCursor: null, hasMore: false);

  @override
  Future<CalibrationSession?> getSession(String sessionId) async {
    for (final s in sessions) {
      if (s.id == sessionId) return s;
    }
    return null;
  }

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
  }) async {
    flatsSessionId = sessionId;
    flatsIncludeFlats = includeFlats;
    flatsIncludeBias = includeBias;
    flatsIncludeDarks = includeDarks;
    flatsLensCap = pauseForLensCap;
    flatsPark = parkMountAfter;
    flatsIncludeCovered = includeCoveredFilters;
    flatsFrameCount = frameCount;
    flatsCoolCamera = coolCamera;
    flatsCoolTo = coolToTemperatureC;
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

CalibrationSession _session({
  bool flats = false,
  bool darks = true,
  bool bias = true,
  double? coolerSetpointC,
  List<CalibrationFilterSummary>? filters,
}) =>
    CalibrationSession(
      id: 'cal-sess-1',
      targetName: 'M42',
      sessionStartUtc: DateTime.utc(2026, 6, 30, 22),
      sessionEndUtc: DateTime.utc(2026, 7, 1, 3),
      lightFrameCount: 40,
      filtersUsed: filters ??
          const [
            CalibrationFilterSummary(
                filterName: 'Ha', lightFrameCount: 24, meanExposureSeconds: 300),
            CalibrationFilterSummary(
                filterName: 'OIII', lightFrameCount: 16, meanExposureSeconds: 180),
          ],
      matchingFlatsAvailable: flats,
      matchingDarksAvailable: darks,
      matchingBiasAvailable: bias,
      coolerSetpointC: coolerSetpointC,
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
    expect(find.text('Bias matched'), findsOneWidget);
  });

  testWidgets('a session with flats but no bias offers the bias on its own',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [
      _session(flats: true, bias: false, filters: const [
        CalibrationFilterSummary(
          filterName: 'Ha',
          lightFrameCount: 5,
          meanExposureSeconds: 60,
          matchingFlatFrames: 30,
        ),
      ]),
    ]);
    await _pump(tester, fake);
    expect(find.text('Bias needed'), findsOneWidget);

    await tester.tap(find.text('Capture Bias'));
    await tester.pumpAndSettle();
    final generate = find.widgetWithText(FilledButton, 'Generate & open');
    expect(tester.widget<FilledButton>(generate).onPressed, isNotNull,
        reason: 'the bias is still to shoot');

    await tester.tap(find.text('Bias'));
    await tester.pumpAndSettle();
    expect(tester.widget<FilledButton>(generate).onPressed, isNull,
        reason: 'flats covered and bias unticked: nothing to shoot');

    await tester.tap(find.text('Bias'));
    await tester.pumpAndSettle();
    await tester.tap(generate);
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeBias, isTrue);
    expect(fake.flatsIncludeFlats, isFalse, reason: 'flats already covered');
    expect(fake.flatsIncludeCovered, isFalse);
  });

  testWidgets('missing bias and darks come with the lens-cap pause by default',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [
      _session(flats: true, bias: false, darks: false, filters: const [
        CalibrationFilterSummary(
          filterName: 'Ha',
          lightFrameCount: 5,
          meanExposureSeconds: 60,
          matchingFlatFrames: 30,
        ),
      ]),
    ]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Bias & Darks'));
    await tester.pumpAndSettle();
    expect(find.text('Darks'), findsOneWidget);
    expect(find.text('Pause for the lens cap first'), findsOneWidget,
        reason: 'no flats in this run, so the cap goes on before anything');
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeBias, isTrue);
    expect(fake.flatsIncludeDarks, isTrue);
    expect(fake.flatsLensCap, isTrue);
  });

  testWidgets('the lens-cap pause is an option the user can turn off',
      (tester) async {
    final fake = _FakeCalibrationClient(
        sessions: [_session(bias: false, darks: false)]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats, Bias & Darks'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Pause for the lens cap after the flats'));
    await tester.tap(find.text('Pause for the lens cap after the flats'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeDarks, isTrue);
    expect(fake.flatsLensCap, isFalse);
  });

  testWidgets('lights that already have bias never ask for it', (tester) async {
    final fake = _FakeCalibrationClient(sessions: [_session()]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    expect(find.text('The lights already have matching bias frames.'), findsOneWidget);
    expect(find.textContaining('Pause for the lens cap'), findsNothing,
        reason: 'no bias or darks to shoot, so nothing to pause for');
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeBias, isFalse);
    expect(fake.flatsLensCap, isFalse);
  });

  testWidgets(
      '§39.5 flow: generate matching flats selects the sequence and jumps to Run',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [_session()]);
    final container = await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    expect(find.text('Generate & open'), findsOneWidget);

    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();

    expect(fake.flatsSessionId, 'cal-sess-1');
    expect(fake.flatsFrameCount, 30,
        reason: 'the dialog starts from the profile frames per filter');
    expect(fake.flatsCoolCamera, isFalse,
        reason: 'lights taken warm → the cooling switch starts off');
    expect(fake.flatsPark, isFalse,
        reason: 'the mount never parks unless this run asks for it');
    expect(container.read(selectedSequenceIdProvider), 'seq-flats-1',
        reason: 'the generated sequence is selected for the Run tab');
    expect(container.read(selectedTabIndexProvider), kRunTabIndex,
        reason: 'the shell switches to the Run tab');
  });

  testWidgets('flats for cooled lights cool to the lights\' set-point',
      (tester) async {
    final fake =
        _FakeCalibrationClient(sessions: [_session(coolerSetpointC: -10)]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    expect(find.text('The lights ran at -10 °C.'), findsOneWidget);
    expect(find.widgetWithText(TextField, 'Cool to (°C)'), findsOneWidget);

    await tester.enterText(find.widgetWithText(TextField, 'Cool to (°C)'), '-8');
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();

    expect(fake.flatsCoolCamera, isTrue);
    expect(fake.flatsCoolTo, -8, reason: 'the user can adjust the target');
  });

  testWidgets('ticking park asks the daemon to park after the flats',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [_session()]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Park the mount at the end'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Park the mount at the end'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsPark, isTrue);
  });

  testWidgets(
      'each filter says which flats cover it, and the dialog shoots only the rest',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [
      _session(filters: [
        CalibrationFilterSummary(
          filterName: 'Ha',
          lightFrameCount: 5,
          meanExposureSeconds: 60,
          matchingFlatFrames: 10,
          matchingFlatsNewestUtc: DateTime.utc(2026, 10, 21, 3, 55),
          flatsNote: "rotator angle not recorded for these frames — assumes the camera wasn't rotated",
        ),
        const CalibrationFilterSummary(
          filterName: 'SII',
          lightFrameCount: 5,
          meanExposureSeconds: 60,
          flatsNote: 'flats exist at rotator 34.8°, the lights were at 120°',
        ),
      ]),
    ]);
    await _pump(tester, fake);

    expect(find.textContaining('Ha · 10 flats from Oct'), findsOneWidget);
    expect(find.textContaining('SII · flats exist at rotator 34.8°'), findsOneWidget);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Already covered by flats from this setup (skipped)'), findsOneWidget);
    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeCovered, isFalse, reason: 'covered filters are skipped');
  });

  testWidgets('a fully covered session offers only a deliberate re-shoot',
      (tester) async {
    final fake = _FakeCalibrationClient(sessions: [
      _session(flats: true, filters: const [
        CalibrationFilterSummary(
          filterName: 'Ha',
          lightFrameCount: 5,
          meanExposureSeconds: 60,
          matchingFlatFrames: 30,
        ),
      ]),
    ]);
    await _pump(tester, fake);

    await tester.tap(find.text('Re-shoot flats'));
    await tester.pumpAndSettle();
    expect(find.textContaining('Every filter already has flats from this setup'), findsOneWidget);
    final generate = find.widgetWithText(FilledButton, 'Generate & open');
    expect(tester.widget<FilledButton>(generate).onPressed, isNull,
        reason: 'nothing to shoot until Flats is ticked');

    await tester.tap(find.text('Flats'));
    await tester.pumpAndSettle();
    await tester.tap(generate);
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeFlats, isTrue);
    expect(fake.flatsIncludeCovered, isTrue);
  });

  testWidgets('flats can be left out for a bias-and-darks-only run',
      (tester) async {
    final fake = _FakeCalibrationClient(
        sessions: [_session(bias: false, darks: false)]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats, Bias & Darks'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Flats'));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(TextField, 'Flats per filter'), findsNothing);
    expect(find.text('Capture Bias & Darks'), findsOneWidget,
        reason: 'the title says what the run shoots');
    expect(find.text('Pause for the lens cap first'), findsOneWidget);

    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsIncludeFlats, isFalse);
    expect(fake.flatsIncludeCovered, isFalse);
    expect(fake.flatsIncludeBias, isTrue);
    expect(fake.flatsIncludeDarks, isTrue);
    expect(fake.flatsFrameCount, isNull);
  });

  testWidgets('switching cooling off sends cool_camera false', (tester) async {
    final fake =
        _FakeCalibrationClient(sessions: [_session(coolerSetpointC: -10)]);
    await _pump(tester, fake);

    await tester.tap(find.text('Capture Flats'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cool the camera first'));
    await tester.pumpAndSettle();
    expect(find.widgetWithText(TextField, 'Cool to (°C)'), findsNothing);

    await tester.tap(find.text('Generate & open'));
    await tester.pumpAndSettle();
    expect(fake.flatsCoolCamera, isFalse);
    expect(fake.flatsCoolTo, isNull);
  });

  testWidgets('the dark form starts from the sessions\' set-points',
      (tester) async {
    final fake =
        _FakeCalibrationClient(sessions: [_session(coolerSetpointC: -10)]);
    await _pump(tester, fake);

    await tester.tap(find.text('Dark Library'));
    await tester.pumpAndSettle();
    final temps = tester.widget<TextField>(find.widgetWithText(
        TextField, 'Sensor temperatures (°C, comma-separated)'));
    expect(temps.controller!.text, '-10');
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
        biasEntries: [
          BiasLibraryEntry(
            id: 'b1',
            gain: 100,
            offset: 50,
            bin: 1,
            frameCount: 50,
            capturedUtc: DateTime.utc(2026, 6, 22, 12),
            fileSizeBytes: 3 * 1024 * 1024,
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
    expect(find.text('gain 100 · offset 50 · bin 1×1'), findsOneWidget);
    expect(find.text('50 frames · 3.0 MB · newest 2026-06-22'), findsOneWidget);

    await tester.tap(find.text('Open build sequence'));
    await tester.pumpAndSettle();
    expect(container.read(selectedSequenceIdProvider), 'seq-darks-1');
    expect(container.read(selectedTabIndexProvider), kRunTabIndex);
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
