import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_calibration_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_build_activity_state.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/equipment/guider_calibration_dialog.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeCalibrationClient implements GuiderCalibrationClient {
  _FakeCalibrationClient(this.response, {this.throwOnStatus = false});
  CalibrationStatusResponse response;
  final bool throwOnStatus;
  int darkBuilds = 0;
  int defectBuilds = 0;
  bool? darkEnabled;
  bool? defectEnabled;

  @override
  Future<CalibrationStatusResponse> getStatus() async {
    if (throwOnStatus) throw StateError('status failed');
    return response;
  }
  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async =>
      darkBuilds++;
  @override
  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) async =>
      defectBuilds++;
  @override
  Future<void> setDarkLibraryEnabled(bool enabled) async => darkEnabled = enabled;
  @override
  Future<void> setDefectMapEnabled(bool enabled) async => defectEnabled = enabled;
  @override
  void close() {}
}

CalibrationStatusResponse _connected({
  bool darkExists = false,
  bool darkLoaded = false,
  bool autoLoadDarks = false,
  bool defectExists = false,
  bool defectLoaded = false,
  bool autoLoadDefectMap = false,
}) =>
    CalibrationStatusResponse(
      connected: true,
      status: CalibrationStatus(
        profileId: 1,
        darkLibraryExists: darkExists,
        darkLibraryLoaded: darkLoaded,
        autoLoadDarks: autoLoadDarks,
        defectMapExists: defectExists,
        defectMapLoaded: defectLoaded,
        autoLoadDefectMap: autoLoadDefectMap,
        darkCountLoaded: darkLoaded ? 20 : null,
        darkMinExposureSecondsLoaded: darkLoaded ? 1.0 : null,
        darkMaxExposureSecondsLoaded: darkLoaded ? 4.0 : null,
      ),
    );

// The dialog watches guiderBuildActivityProvider, whose real notifier watches
// the WS stream (which would dial a socket in tests) — stub it with a fixed map.
class _StubBuildActivity extends GuiderBuildActivityNotifier {
  _StubBuildActivity(this.initial);
  final Map<CalibrationArtifact, CalibrationBuildActivity> initial;
  @override
  Map<CalibrationArtifact, CalibrationBuildActivity> build() => initial;
}

Widget _host(
  GuiderCalibrationClient fake, {
  Map<CalibrationArtifact, CalibrationBuildActivity> builds = const {},
}) =>
    ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        guiderCalibrationApiFactoryProvider.overrideWithValue((_) => fake),
        guiderBuildActivityProvider.overrideWith(() => _StubBuildActivity(builds)),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => ElevatedButton(
              onPressed: () => showGuiderCalibrationDialog(context),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );

Future<void> _open(WidgetTester tester) async {
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
}

/// Answers the cover-the-scope confirmation that now precedes every build.
Future<void> _confirmCover(WidgetTester tester) async {
  expect(find.text('Cover the scope'), findsOneWidget);
  await tester.tap(find.text("It's covered — build"));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('shows dark-library status + loaded detail when connected', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true))));
    await _open(tester);
    expect(find.text('Guider calibration'), findsOneWidget);
    expect(find.text('Dark library'), findsOneWidget);
    expect(find.text('Loaded'), findsWidgets);
    expect(find.textContaining('20 darks'), findsOneWidget);
  });

  testWidgets('not connected → manage-calibration prompt', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(const CalibrationStatusResponse(connected: false))));
    await _open(tester);
    expect(find.text('Connect the guider to manage calibration.'), findsOneWidget);
  });

  testWidgets('a status fetch failure renders the neutral error message', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(_connected(), throwOnStatus: true)));
    await _open(tester);
    expect(find.text('The last guider request failed. Tap Refresh to recheck.'), findsOneWidget);
  });

  testWidgets('connected but no status → status-unavailable (not "connect")', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(const CalibrationStatusResponse(connected: true))));
    await _open(tester);
    expect(find.text('Calibration status unavailable — tap Refresh.'), findsOneWidget);
    expect(find.text('Connect the guider to manage calibration.'), findsNothing);
  });

  testWidgets('tapping Build dispatches a dark-library build', (tester) async {
    final fake = _FakeCalibrationClient(_connected());
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    // Dark library not built → the button reads "Build".
    await tester.tap(find.widgetWithText(TextButton, 'Build').first);
    await tester.pumpAndSettle();
    await _confirmCover(tester);
    expect(fake.darkBuilds, 1);
  });

  testWidgets('the enable switch reflects auto-load, not transient loaded state', (tester) async {
    // Loaded in memory but auto-load disabled — the switch must read OFF.
    final fake = _FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    final sw = tester.widget<Switch>(find.byType(Switch).first);
    expect(sw.value, isFalse, reason: 'bound to autoLoadDarks (false), not darkLibraryLoaded (true)');
  });

  testWidgets('toggling the dark-library switch calls setDarkLibraryEnabled', (tester) async {
    final fake = _FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true, autoLoadDarks: true));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(fake.darkEnabled, isFalse, reason: 'auto-load was on → toggled off');
  });

  testWidgets('tapping the defect-map Build dispatches a defect-map build', (tester) async {
    final fake = _FakeCalibrationClient(_connected()); // neither built → two "Build" buttons
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    await tester.tap(find.widgetWithText(TextButton, 'Build').at(1)); // defect map is the second
    await tester.pumpAndSettle();
    await _confirmCover(tester);
    expect(fake.defectBuilds, 1);
  });

  testWidgets('an already-built dark library shows Rebuild and dispatches a build', (tester) async {
    final fake = _FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    expect(find.widgetWithText(TextButton, 'Rebuild'), findsWidgets);
    await tester.tap(find.widgetWithText(TextButton, 'Rebuild').first);
    await tester.pumpAndSettle();
    await _confirmCover(tester);
    expect(fake.darkBuilds, 1);
  });

  testWidgets('defect-map built-but-not-loaded shows the right state text', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(_connected(defectExists: true))));
    await _open(tester);
    // Dark library not built → "Not built"; defect map built but not loaded.
    expect(find.text('Built (not loaded)'), findsOneWidget);
    expect(find.text('Not built'), findsOneWidget);
  });

  testWidgets('toggling the defect-map switch calls setDefectMapEnabled', (tester) async {
    final fake = _FakeCalibrationClient(_connected(defectExists: true, autoLoadDefectMap: false));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    // Defect map is the second artifact / second Switch.
    await tester.tap(find.byType(Switch).at(1));
    await tester.pumpAndSettle();
    expect(fake.defectEnabled, isTrue, reason: 'auto-load was off → toggled on');
  });

  testWidgets('cancelling the cover-the-scope confirmation dispatches nothing', (tester) async {
    final fake = _FakeCalibrationClient(_connected());
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    await tester.tap(find.widgetWithText(TextButton, 'Build').first);
    await tester.pumpAndSettle();
    expect(find.text('Cover the scope'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(fake.darkBuilds, 0, reason: 'no confirm → no 202');
  });

  testWidgets('a building artifact shows the live building state and disables BOTH build buttons', (tester) async {
    final fake = _FakeCalibrationClient(_connected());
    await tester.pumpWidget(_host(fake, builds: const {
      CalibrationArtifact.darkLibrary:
          CalibrationBuildActivity(phase: CalibrationBuildPhase.building),
    }));
    // No pumpAndSettle: the indeterminate build indicators animate forever.
    await tester.tap(find.text('open'));
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    expect(find.text('Building — keep the scope covered.'), findsOneWidget);
    expect(find.widgetWithText(TextButton, 'Building…'), findsOneWidget);
    // One camera, one build: the defect-map Build must be disabled too.
    final defectBuild = tester.widget<TextButton>(find.widgetWithText(TextButton, 'Build').first);
    expect(defectBuild.onPressed, isNull, reason: 'shared single-build gate');
  });

  testWidgets('a failed build renders its error from the WS payload', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(_connected()), builds: const {
      CalibrationArtifact.defectMap: CalibrationBuildActivity(
          phase: CalibrationBuildPhase.failed, error: 'camera timeout'),
    }));
    await _open(tester);
    expect(find.text('Build failed: camera timeout'), findsOneWidget);
  });
}